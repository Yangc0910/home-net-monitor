using System.ComponentModel;
using System.Text.Json;

namespace HomeNet;

public sealed class SpeedTests(Settings settings, DataPath data, Store db, IHostApplicationLifetime lifetime, ILogger<SpeedTests> log)
{
    private readonly object gate = new();
    private Link? previous;
    private long previousAt;
    private long quietFrom;
    private bool healthy;
    private long observed;
    private volatile bool loaded;
    private bool crossedNetwork;
    public bool Loaded => loaded;
    public bool Enabled => settings.Speed.Enabled;

    public void NetworkChanged()
    {
        lock (gate)
        {
            if (loaded) crossedNetwork = true;
            previous = null;
            quietFrom = 0;
            healthy = false;
        }
    }

    public void Observe(Link link, Sample[] samples, long at)
    {
        lock (gate)
        {
            observed = at;
            healthy = link.Connected && samples.Any(s => s.Id == "gateway" && s.Ok) &&
                      samples.Count(s => s.Protocol == "tcp" && s.Ok) >= 2 &&
                      samples.Any(s => s.Id == "dns-local" && s.Ok);
            if (previous?.Rx is long rx && previous.Tx is long tx && link.Rx is long nextRx && link.Tx is long nextTx &&
                nextRx >= rx && nextTx >= tx && at > previousAt && at - previousAt <= settings.CheckSeconds * 1700L &&
                Math.Max(nextRx - rx, nextTx - tx) * 8.0 / (at - previousAt) / 1000 < settings.Speed.BusyMbps)
                quietFrom = quietFrom == 0 ? previousAt : quietFrom;
            else quietFrom = 0;
            previous = link;
            previousAt = at;
            if (!Enabled || !healthy || quietFrom == 0 || at - quietFrom < 30000 || loaded) return;

            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at), Settings.Eastern);
            var start = TimeOnly.ParseExact(settings.Speed.WindowStart, "HH:mm").ToTimeSpan();
            if (local.TimeOfDay < start || local.TimeOfDay >= start + TimeSpan.FromMinutes(settings.Speed.WindowMinutes)) return;
            var today = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local.Date, Settings.Eastern)).ToUnixTimeMilliseconds();
            if (!db.Records<SpeedRun>("speed", today, at + 1).Any(s => s.Mode == "scheduled"))
                StartLocked("scheduled", at);
        }
    }

    public string? Manual()
    {
        lock (gate)
        {
            var at = Settings.Now;
            if (!Enabled) return "disabled";
            if (loaded) return "already-running";
            if (!healthy || at - observed > settings.CheckSeconds * 2000L) return "connectivity-unavailable";
            if (quietFrom == 0 || at - quietFrom < 30000) return "waiting-for-idle";
            return StartLocked("manual", at);
        }
    }

    private string? StartLocked(string mode, long at)
    {
        if (loaded) return "already-running";
        var config = settings.Speed;
        if (string.IsNullOrWhiteSpace(config.Executable) || string.IsNullOrWhiteSpace(config.ServerFile)) return "server-not-configured";
        var exe = Path.GetFullPath(config.Executable, data.Directory);
        var file = Path.GetFullPath(config.ServerFile, data.Directory);
        if (!File.Exists(exe) || !File.Exists(file)) return "server-not-configured";
        string server;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var list = document.RootElement;
            if (list.GetArrayLength() != 1 || !list[0].TryGetProperty("server", out var value) ||
                !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
                return "invalid-server";
            foreach (var key in new[] { "dlURL", "ulURL", "pingURL", "getIpURL" })
            {
                var endpoint = list[0].GetProperty(key).GetString();
                if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(uri, endpoint, out var resolved) ||
                    resolved.Scheme != uri.Scheme || resolved.Authority != uri.Authority) return "invalid-server";
            }
            server = uri.Authority;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
        { return "invalid-server"; }
        var run = new SpeedRun(at, null, mode, "running", server, null, null, null, previous == null ? WifiIdentity.Unknown : WifiIdentity.Network(previous));
        db.Put("speed", at, at, run);
        crossedNetwork = false;
        loaded = true;
        _ = Execute(exe, file, run, lifetime.ApplicationStopping);
        return null;
    }

    private async Task Execute(string exe, string file, SpeedRun run, CancellationToken ct)
    {
        var status = "failed";
        double? download = null, upload = null, latency = null;
        try
        {
            var (code, output) = await Child.Run(exe, ["--ipv4", "--no-icmp", "--local-json", file, "--json",
                "--duration", "5", "--concurrent", "2", "--telemetry-level", "disabled"], 45, ct);
            if (code == 0)
            {
                using var document = JsonDocument.Parse(output);
                var value = document.RootElement[0];
                download = Number(value, "download") / 1e6;
                upload = Number(value, "upload") / 1e6;
                latency = Number(value, "ping");
                status = download > 0 && upload > 0 ? "ok" : "failed";
            }
        }
        catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "interrupted" : "timeout"; }
        catch (Exception ex) when (ex is JsonException or IOException or Win32Exception or InvalidOperationException or IndexOutOfRangeException)
        { log.LogWarning("Speed test failed ({Type})", ex.GetType().Name); }
        finally
        {
            lock (gate)
            {
                loaded = false;
                try { db.Put("speed", run.Start, run.Start, run with { End = Settings.Now, Status = status, Network = crossedNetwork ? WifiIdentity.Switching : run.Network, Download = download, Upload = upload, Latency = latency }); }
                catch (Exception ex) { log.LogWarning("Could not store speed result ({Type})", ex.GetType().Name); }
            }
        }
    }
    private static double? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var result) && double.IsFinite(result) && result >= 0
        ? result : null;
}
