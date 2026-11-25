using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;

namespace HomeNet;

public sealed class NestMonitor(Settings settings, DataPath data, Store db, ILogger<NestMonitor> log) : BackgroundService
{
    private volatile NestLive live = new(settings.Nest?.Enabled == true ? "starting" : "disabled", null, null, null);
    private volatile string? device;
    public NestLive Live => live;
    public string? Device => device;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (settings.Nest is not { Enabled: true } enabled) return;
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        NestCloud? cloud = null;
        long nextSnapshot = 0;
        bool? feedUp = null;
        var lastDelay = 12;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (cloud == null)
                {
                    var root = Path.GetDirectoryName(data.Directory)!;
                    var file = Path.GetFullPath(enabled.CredentialsFile, root);
                    var config = JsonSerializer.Deserialize<NestCredentials>(await File.ReadAllTextAsync(file, ct), Settings.Json)
                        ?? throw new InvalidDataException("Missing credentials.");
                    cloud = new NestCloud(http, config, Path.GetDirectoryName(file)!);
                    device = cloud.Device;
                }
                var at = Settings.Now;
                if (at >= nextSnapshot)
                {
                    var snapshot = await cloud.Snapshot(ct);
                    at = Settings.Now;
                    var initial = new List<NestEvent>();
                    if (snapshot.Online is bool online && db.NestLatest(device!, true) != (online ? "ONLINE" : "OFFLINE"))
                        initial.Add(Local("connectivity", online ? "ONLINE" : "OFFLINE"));
                    if (snapshot.Hvac is { } hvac && snapshot.Online != false &&
                        (nextSnapshot == 0 || db.NestLatest(device!, false) != hvac))
                        initial.Add(Local("snapshot", hvac));
                    db.WriteNest(initial);
                    live = new("connecting", snapshot.Hvac, snapshot.Online, live.LastContact);
                    nextSnapshot = at + 300000;
                }
                var batch = await cloud.Pull(ct);
                if (batch.Events.Length > 0) db.WriteNest(batch.Events);
                if (batch.Acks.Length > 0) await cloud.Acknowledge(batch.Acks, ct);
                if (feedUp != true)
                {
                    db.WriteNest([Local("feed", "available")]);
                    feedUp = true;
                }
                var onlineNow = db.NestLatest(device!, true) switch
                { "ONLINE" => true, "OFFLINE" => false, _ => (bool?)null };
                var current = db.NestLatest(device!, false);
                live = new(onlineNow == false ? "thermostat-offline" : "connected", onlineNow == false ? null : current, onlineNow, Settings.Now);
                lastDelay = batch.Acks.Length >= NestCloud.BatchSize ? 1 : 12;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (feedUp != false)
                {
                    log.LogWarning("Nest event feed unavailable ({Type}, HTTP {Status})", ex.GetType().Name, (ex as HttpRequestException)?.StatusCode);
                    if (device != null)
                    {
                        try { db.WriteNest([Local("feed", "unavailable")]); feedUp = false; }
                        catch (Exception writeError)
                        { log.LogWarning("Could not record Nest feed gap ({Type})", writeError.GetType().Name); }
                    }
                }
                live = live with { Status = cloud == null ? "setup-required" : "feed-unavailable", Hvac = null, Online = null };
                nextSnapshot = 0;
                lastDelay = Math.Min(Math.Max(15, lastDelay * 2), 120);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(lastDelay), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }

        NestEvent Local(string kind, string value)
        {
            var at = Settings.Now;
            return new(device!, "local-" + at.ToString(CultureInfo.InvariantCulture), at, at, kind, value);
        }
    }
}

public sealed class NestCloud
{
    public const int BatchSize = 30;
    private readonly HttpClient http;
    private readonly NestCredentials config;
    private readonly Func<CancellationToken, Task<string>> subscriberToken;
    private readonly string subscriptionUri;
    private string? access;
    private DateTimeOffset expires;
    public string Device { get; }

    public NestCloud(HttpClient http, NestCredentials config, string credentialDirectory, Func<CancellationToken, Task<string>>? tokenProvider = null)
    {
        this.http = http;
        this.config = config;
        if (!Guid.TryParse(config.DeviceAccessProject, out _) || string.IsNullOrWhiteSpace(config.DeviceId) ||
            !Regex.IsMatch(config.DeviceId, @"^[A-Za-z0-9_-]+$") ||
            string.IsNullOrWhiteSpace(config.Subscription) ||
            !Regex.IsMatch(config.Subscription, @"^projects/[A-Za-z0-9_-]+/subscriptions/[A-Za-z0-9._~-]+$") ||
            string.IsNullOrWhiteSpace(config.ClientId) || string.IsNullOrWhiteSpace(config.ClientSecret) ||
            string.IsNullOrWhiteSpace(config.RefreshToken) || string.IsNullOrWhiteSpace(config.SubscriberKeyFile))
            throw new InvalidDataException("Invalid Nest configuration.");
        Device = $"enterprises/{config.DeviceAccessProject}/devices/{config.DeviceId}";
        subscriptionUri = "https://pubsub.googleapis.com/v1/" + config.Subscription;
        subscriberToken = tokenProvider ?? ReadSubscriber(Path.GetFullPath(config.SubscriberKeyFile, credentialDirectory));
    }

    public async Task<(string? Hvac, bool? Online)> Snapshot(CancellationToken ct)
    {
        if (access == null || expires <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = config.ClientId, ["client_secret"] = config.ClientSecret,
                ["refresh_token"] = config.RefreshToken, ["grant_type"] = "refresh_token"
            });
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
            response.EnsureSuccessStatusCode();
            using var document = await Read(response, ct);
            access = document.RootElement.GetProperty("access_token").GetString()!;
            expires = DateTimeOffset.UtcNow.AddSeconds(document.RootElement.GetProperty("expires_in").GetInt32());
        }
        try
        {
            using var result = await Send(HttpMethod.Get, "https://smartdevicemanagement.googleapis.com/v1/" + Device, access, null, ct);
            var traits = result.RootElement.GetProperty("traits");
            var hvac = NestEvents.Status(traits, NestEvents.Hvac);
            var online = NestEvents.Status(traits, NestEvents.Connectivity) switch { "ONLINE" => true, "OFFLINE" => false, _ => (bool?)null };
            return (hvac is "HEATING" or "COOLING" or "OFF" ? hvac : null, online);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        { access = null; throw; }
    }

    public async Task<(NestEvent[] Events, string[] Acks)> Pull(CancellationToken ct)
    {
        var token = await SubscriberToken(ct);
        using var result = await Send(HttpMethod.Post, subscriptionUri + ":pull", token,
            new { maxMessages = BatchSize, returnImmediately = true }, ct);
        if (!result.RootElement.TryGetProperty("receivedMessages", out var messages)) return ([], []);
        var records = new List<NestEvent>();
        var acks = new List<string>();
        var received = Settings.Now;
        foreach (var delivery in messages.EnumerateArray())
        {
            if (!delivery.TryGetProperty("ackId", out var ack) || ack.GetString() is not { Length: > 0 } id) continue;
            try
            {
                if (delivery.TryGetProperty("message", out var message) && message.TryGetProperty("data", out var value) &&
                    value.GetString() is { } content)
                {
                    using var payload = JsonDocument.Parse(Convert.FromBase64String(content));
                    records.AddRange(NestEvents.Decode(payload.RootElement, Device, received));
                }
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
            { /* An invalid or unrelated event has no local state to save. */ }
            acks.Add(id);
        }
        return (records.ToArray(), acks.ToArray());
    }
    public async Task Acknowledge(string[] acks, CancellationToken ct)
    {
        using var result = await Send(HttpMethod.Post, subscriptionUri + ":acknowledge", await SubscriberToken(ct), new { ackIds = acks }, ct);
    }

    private Task<string> SubscriberToken(CancellationToken ct) => subscriberToken(ct);

    private static Func<CancellationToken, Task<string>> ReadSubscriber(string path)
    {
        var credential = CredentialFactory.FromFile<ServiceAccountCredential>(path)
            .ToGoogleCredential().CreateScoped("https://www.googleapis.com/auth/pubsub");
        return ct => credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);
    }

    private async Task<JsonDocument> Send(HttpMethod method, string address, string token, object? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content != null) request.Content = JsonContent.Create(content);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await Read(response, ct);
    }

    private static async Task<JsonDocument> Read(HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return JsonDocument.Parse(bytes.Length == 0 ? "{}"u8.ToArray() : bytes);
    }
}
