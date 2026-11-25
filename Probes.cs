using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DnsClient;

namespace HomeNet;

public sealed class Probes(Settings settings)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = async (context, ct) =>
        {
            var hosts = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, ct);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(hosts, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        }
    });
    public Link ReadLink()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.Name.Equals(settings.Adapter, StringComparison.OrdinalIgnoreCase));
        if (nic == null) return new(false, false, null, null, [], null, null, null);
        var props = nic.GetIPProperties();
        var up = nic.OperationalStatus == OperationalStatus.Up;
        var gateway = props.GatewayAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray();
        var stats = nic.GetIPStatistics();
        var (ssid, sequence) = WifiIdentity.Read(nic.Id, up);
        return new(true, up, nic.Name, gateway, dns, up ? Wlan.Signal(nic.Id) : null, stats.BytesReceived, stats.BytesSent, ssid, sequence);
    }
    public async Task<Sample[]> Collect(Link link, long at, bool full, bool loaded, CancellationToken ct)
    {
        var gateway = new Endpoint("gateway", "当前网络网关", link.Gateway ?? "", "gateway");
        var tasks = settings.Endpoints.Where(e => e.Layer != "upstream" || e.Address != link.Gateway).Prepend(gateway).Select(e => Ping(e, at, loaded, ct)).ToList();
        if (full)
        {
            tasks.AddRange(settings.Endpoints.Where(e => e.Layer == "public").Select(e => Timed(e, "tcp", at, loaded, ct, async token =>
            {
                using var tcp = new TcpClient(AddressFamily.InterNetwork);
                await tcp.ConnectAsync(IPAddress.Parse(e.Address), 443, token);
                return (true, e.Address + ":443");
            })));
            tasks.Add(DnsCheck("dns-local", "配置的 DNS", link.Dns, at, loaded, ct));
            tasks.Add(DnsCheck("dns-reference", "公共 DNS 对照", ["1.1.1.1"], at, loaded, ct));
            tasks.Add(Web(new("https-google", "Google HTTPS", "https://www.gstatic.com/generate_204", "public"), true, at, loaded, ct));
            tasks.Add(Web(new("https-cloudflare", "Cloudflare HTTPS", "https://cp.cloudflare.com/generate_204", "public"), true, at, loaded, ct));
            if (settings.ModemStatusUrl is { } url) tasks.Add(Web(new("modem-status", "Modem 状态页", url, "modem"), false, at, loaded, ct));
        }
        return await Task.WhenAll(tasks);
    }
    private static async Task<Sample> Ping(Endpoint e, long at, bool loaded, CancellationToken ct)
    {
        if (!IPAddress.TryParse(e.Address, out var address))
            return new(at, e.Id, e.Label, e.Layer, "icmp", "unavailable", null, "No IPv4 gateway", loaded);
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, Limit, cancellationToken: ct);
            return new(at, e.Id, e.Label, e.Layer, "icmp", reply.Status switch
            {
                IPStatus.Success => "ok", IPStatus.TimedOut => "timeout", _ => "failure"
            }, reply.Status == IPStatus.Success ? reply.RoundtripTime : null, e.Address + " " + reply.Status, loaded);
        }
        catch (Exception ex) when (ex is PingException or SocketException)
        {
            return new(at, e.Id, e.Label, e.Layer, "icmp", "failure", null, ex.GetType().Name, loaded);
        }
    }
    private Task<Sample> Web(Endpoint e, bool expect204, long at, bool loaded, CancellationToken ct) =>
        Timed(e, e.Address.StartsWith("https:") ? "https" : "http", at, loaded, ct, async token =>
        {
            using var response = await http.GetAsync(e.Address, HttpCompletionOption.ResponseHeadersRead, token);
            var status = (int)response.StatusCode;
            return (expect204 ? status == 204 : status is >= 200 and < 500, "HTTP " + status);
        });
    private static Task<Sample> DnsCheck(string id, string label, string[] servers, long at, bool loaded, CancellationToken ct)
    {
        var e = new Endpoint(id, label, "", "dns");
        if (servers.Length == 0) return Task.FromResult(new Sample(at, id, label, "dns", "dns", "unavailable", null, "No IPv4 resolver", loaded));
        return Timed(e, "dns", at, loaded, ct, async token =>
        {
            var resolver = new LookupClient(new LookupClientOptions(servers.Select(IPAddress.Parse).ToArray())
            { UseCache = false, Retries = 0, Timeout = Limit, ThrowDnsErrors = false, UseRandomNameServer = false });
            var reply = await resolver.QueryAsync("example.com", QueryType.A, cancellationToken: token);
            return (!reply.HasError && reply.Answers.ARecords().Any(), string.Join(", ", servers) + " " + reply.Header.ResponseCode);
        });
    }
    private static async Task<Sample> Timed(Endpoint e, string protocol, long at, bool loaded, CancellationToken ct,
        Func<CancellationToken, Task<(bool Ok, string Detail)>> action)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Limit);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var (ok, detail) = await action(timeout.Token);
            return new(at, e.Id, e.Label, e.Layer, protocol, ok ? "ok" : "failure",
                ok ? Stopwatch.GetElapsedTime(start).TotalMilliseconds : null, detail, loaded);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(at, e.Id, e.Label, e.Layer, protocol, "timeout", null, null, loaded);
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or DnsResponseException or IOException)
        {
            return new(at, e.Id, e.Label, e.Layer, protocol, "failure", null, ex.GetType().Name, loaded);
        }
    }
    public string[] RecentWlan()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Microsoft-Windows-WLAN-AutoConfig/Operational", PathType.LogName,
                "*[System[(EventID=8001 or EventID=8002 or EventID=8003) and TimeCreated[timediff(@SystemTime)<=300000]]]") { ReverseDirection = true });
            var events = new List<string>();
            for (var i = 0; i < 12; i++)
            {
                using var entry = reader.ReadEvent();
                if (entry == null) break;
                if (entry.TimeCreated is DateTime time)
                    events.Add(new DateTimeOffset(time).ToUniversalTime().ToString("O") + " WLAN " + entry.Id);
            }
            return events.ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException)
        {
            return ["WLAN event log unavailable"];
        }
    }
    public async Task<string?> Trace(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var (_, output) = await Child.Run(Path.Combine(Environment.SystemDirectory, "tracert.exe"),
            ["-4", "-d", "-h", "12", "-w", "450", settings.Endpoints.First(e => e.Layer == "public").Address], 22, ct);
        return output.Length > 8000 ? output[..8000] : output;
    }
}
