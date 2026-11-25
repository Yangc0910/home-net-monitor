using System.Text.Json;
using System.Net;

namespace HomeNet;

public sealed record Endpoint(string Id, string Label, string Address, string Layer);
public sealed record Sample(long At, string Id, string Label, string Layer, string Protocol, string Outcome, double? Ms, string? Detail = null, bool Loaded = false, string Network = "legacy", string? Gateway = null, string? Dns = null)
{
    public bool Ok => Outcome == "ok";
    public bool Failed => Outcome is "timeout" or "failure";
}
public sealed record Link(bool Known, bool Connected, string? Adapter, string? Gateway, string[] Dns, int? SignalDbm, long? Rx, long? Tx, string? Ssid = null, long WlanSequence = 0);
public sealed record Diagnosis(string Kind, string Observation, string Suspected, string? Confirmed = null);
public sealed record Incident(long Id, long Start, long? End, Diagnosis Diagnosis, string Network = "legacy", string? EndReason = null);
public sealed record Annotation(long Id, long At, long RecordedAt, string Kind, string Note, string Source);
public sealed record Gap(long Start, long End, string Reason);
public sealed record Snapshot(long At, long IncidentId, string Phase, Link Link, string[] Wlan, string? Trace);
public sealed record SpeedRun(long Start, long? End, string Mode, string Status, string? Server, double? Download, double? Upload, double? Latency, string Network = "legacy");
public sealed record Point(long At, string Id, string Label, string Layer, string Protocol, long Attempts, long Successes, long Loaded, double? Rtt, double? P95, double? Jitter, string Network = "legacy", long Rounds = 0)
{
    public double? Loss => Attempts == 0 ? null : 100.0 * (Attempts - Successes) / Attempts;
}
public sealed record Correlation(Annotation Event, Point[] Before, Point[] After, Point[] Control, double BeforeCoverage, double AfterCoverage, double ControlCoverage, string Network = "legacy");
public sealed record SpeedSettings(bool Enabled = false, string? Executable = null, string? ServerFile = null, string WindowStart = "03:00", int WindowMinutes = 60, int BusyMbps = 1);
public sealed class Settings
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public int Port { get; init; } = 8765;
    public int PingSeconds { get; init; } = 5;
    public int CheckSeconds { get; init; } = 15;
    public int RawDays { get; init; } = 30;
    public int HistoryDays { get; init; } = 365;
    public string Adapter { get; init; } = "Wi-Fi";
    public string? ModemStatusUrl { get; init; }
    public NestSettings Nest { get; init; } = new();
    public SpeedSettings Speed { get; init; } = new();
    public Endpoint[] Endpoints { get; init; } =
    [
        new("upstream", "上游本地设备", "192.168.0.1", "upstream"),
        new("isp", "RCN 路径参考", "207.172.217.231", "isp"),
        new("cloudflare", "Cloudflare", "1.1.1.1", "public"),
        new("google", "Google", "8.8.8.8", "public"),
        new("quad9", "Quad9", "9.9.9.9", "public")
    ];
    public static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static string Local(long ms) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), Eastern).ToString("yyyy-MM-dd HH:mm:ss zzz");
    public void Validate()
    {
        if (Port is < 1024 or > 65535 || PingSeconds is < 2 or > 60 || CheckSeconds < PingSeconds || CheckSeconds > 120 ||
            RawDays < 1 || HistoryDays < RawDays || HistoryDays > 3650 || string.IsNullOrWhiteSpace(Adapter))
            throw new InvalidDataException("Invalid port, intervals, retention, or adapter.");
        if (Endpoints.Any(e => !IPAddress.TryParse(e.Address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Label) || e.Layer is not ("upstream" or "isp" or "public" or "modem")
            || e.Id is "gateway" or "modem-status" or "https-google" or "https-cloudflare" or "dns-local" or "dns-reference") ||
            Endpoints.Select(e => e.Id).Distinct().Count() != Endpoints.Length ||
            Endpoints.Where(e => e.Layer == "public").Select(e => e.Address).Distinct().Count() < 2)
            throw new InvalidDataException("Endpoints require unique IDs, IPv4 addresses, and two distinct public references.");
        if (ModemStatusUrl != null && (!Uri.TryCreate(ModemStatusUrl, UriKind.Absolute, out var modem) ||
            modem.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(modem.UserInfo) || !IPAddress.TryParse(modem.Host, out var modemIp) || !Private(modemIp)))
            throw new InvalidDataException("Use an explicitly verified private IPv4 modem status URL without credentials.");
        if (!TimeOnly.TryParseExact(Speed.WindowStart, "HH:mm", out var start) || Speed.WindowMinutes is < 1 or > 240 ||
            start.ToTimeSpan().TotalMinutes + Speed.WindowMinutes > 1440 || Speed.BusyMbps < 1)
            throw new InvalidDataException("Invalid speed test window or traffic threshold.");
    }
    private static bool Private(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }
}
public sealed record DataPath(string Directory);
