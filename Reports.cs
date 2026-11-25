using System.Globalization;
using System.Net;
using System.Text;

namespace HomeNet;

public static class Reports
{
    public static readonly string[] CsvKinds = ["samples", "minutes", "annotations", "incidents", "speeds", "gaps", "wifi", "heating"];
    private const long Window = 300000;
    private static readonly string[] Layers = ["gateway", "public", "dns"];
    public static NestTimeline Heating(Store db, string? device, long from, long to) =>
        NestEvents.Timeline(db.NestHistory(device, to), from, to);

    public static Correlation[] Correlate(Store db, Settings settings, long from, long to, string? network = null, string? device = null)
    {
        var manual = db.Records<Annotation>("annotation", from, to).Where(x => x.Kind is "hvac-start" or "hvac-stop");
        var automatic = Heating(db, device, from, to).Heating.Where(x => x.Start >= from && x.Start < to)
            .Select(x => new Annotation(-x.Start!.Value, x.Start.Value, x.Received, "hvac-start", "", "nest"));
        var subjects = manual.Concat(automatic).OrderByDescending(x => x.At);
        var networks = network == null ? new[] { WifiIdentity.NestWifi, WifiIdentity.Astound, WifiIdentity.Legacy } : new[] { network };
        return (from row in subjects
                 from wifi in networks
                 let before = db.WindowPoints(settings, row.At - Window, row.At, wifi)
                 let after = db.WindowPoints(settings, row.At, row.At + Window, wifi)
                 let control = db.WindowPoints(settings, row.At - 2 * Window, row.At - Window, wifi)
                 where before.Length + after.Length + control.Length > 0
                 select new Correlation(row, before, after, control, Coverage(before, settings), Coverage(after, settings), Coverage(control, settings), wifi)).ToArray();
    }

    public static object[] Compare(Store db, Settings settings, long from, long to)
    {
        var full = Summary(db, settings, from, to, null);
        return WifiIdentity.HomeNetworks.Select(network =>
        {
            var rows = full.Where(p => p.Network == network).ToArray();
            var gateway = rows.FirstOrDefault(p => p.Id == "gateway" && p.Protocol == "icmp");
            var dns = rows.FirstOrDefault(p => p.Id == "dns-local");
            var external = rows.Where(p => p.Layer == "public" && p.Protocol == "icmp").ToArray();
            var attempts = external.Sum(p => p.Attempts);
            var successes = external.Sum(p => p.Successes);
            var seconds = (gateway?.Rounds ?? 0) * settings.PingSeconds;
            return (object)new { network, observedSeconds = Math.Min(seconds, (to - from) / 1000), publicAttempts = attempts,
                publicLoss = attempts == 0 ? null : (double?)(100.0 * (attempts - successes) / attempts),
                publicRtt = successes == 0 ? null : (double?)(external.Sum(p => p.Rtt.GetValueOrDefault() * p.Successes) / successes),
                gatewayLoss = gateway?.Loss, dnsAttempts = dns?.Attempts ?? 0, dnsFailure = dns?.Loss };
        }).ToArray();
    }

    private static Point[] Summary(Store db, Settings settings, long from, long to, string? network) =>
        to - from <= 43200000 && from > Settings.Now - settings.RawDays * 86400000L
            ? db.Points(from, to, settings, singleWindow: true, network: network) : db.ReportMinutes(from, to, network);

    private static double Coverage(Point[] points, Settings settings) => Math.Min(100, points.Where(p => p.Layer == "public" && p.Protocol == "icmp")
        .Select(p => (double)p.Attempts).DefaultIfEmpty().Max() * settings.PingSeconds / 3);

    private static Point[] WindowPoints(this Store db, Settings settings, long start, long end, string network)
    {
        var rows = start > Settings.Now - settings.RawDays * 86400000L
            ? db.Points(start, end, settings, singleWindow: true, network: network)
            : db.ReportMinutes((start + 59999) / 60000 * 60000, end / 60000 * 60000, network);
        return rows.Where(p => Layers.Contains(p.Layer)).ToArray();
    }

    public static async Task Csv(Stream stream, Store db, Settings settings, string kind, long from, long to, string? network = null, string? device = null)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
        switch (kind)
        {
            case "samples":
                await Row("utc", "eastern", "target", "layer", "protocol", "outcome", "rtt_ms", "detail", "under_load", "wifi", "gateway_ipv4", "dns_ipv4");
                foreach (var s in db.Samples(from, to, network))
                    await Row(Utc(s.At), Settings.Local(s.At), s.Id, s.Layer, s.Protocol, s.Outcome, s.Ms, s.Detail, s.Loaded, s.Network, s.Gateway, s.Dns);
                break;
            case "minutes":
                await Row("utc_minute", "eastern", "target", "layer", "protocol", "attempts", "successes", "loss_pct", "avg_ms", "p95_ms", "jitter_ms", "under_load_count", "wifi");
                foreach (var p in db.Points(from, to, settings, summary: true, network: network))
                    await Row(Utc(p.At), Settings.Local(p.At), p.Id, p.Layer, p.Protocol, p.Attempts, p.Successes, p.Loss, p.Rtt, p.P95, p.Jitter, p.Loaded, p.Network);
                break;
            case "annotations":
                await Row("event_utc", "eastern", "recorded_utc", "type", "note", "source");
                foreach (var x in db.Records<Annotation>("annotation", from, to))
                    await Row(Utc(x.At), Settings.Local(x.At), Utc(x.RecordedAt), x.Kind, x.Note, x.Source);
                break;
            case "incidents":
                await Row("start_utc", "start_eastern", "end_utc", "end_eastern", "class", "observation", "suspected", "confirmed", "wifi", "end_reason");
                foreach (var x in db.Records<Incident>("incident", from, to).Where(i => network == null || i.Network == network))
                    await Row(Utc(x.Start), Settings.Local(x.Start), Utc(x.End), x.End is long end ? Settings.Local(end) : null,
                        x.Diagnosis.Kind, x.Diagnosis.Observation, x.Diagnosis.Suspected, x.Diagnosis.Confirmed, x.Network, x.EndReason);
                break;
            case "speeds":
                await Row("start_utc", "eastern", "end_utc", "mode", "status", "server", "download_mbps", "upload_mbps", "latency_ms", "wifi");
                foreach (var x in db.Records<SpeedRun>("speed", from, to).Where(s => network == null || s.Network == network))
                    await Row(Utc(x.Start), Settings.Local(x.Start), Utc(x.End), x.Mode,
                        x.End == null && Settings.Now - x.Start > 60000 ? "interrupted" : x.Status, x.Server, x.Download, x.Upload, x.Latency, x.Network);
                break;
            case "wifi":
                await Row("utc", "eastern", "previous_wifi", "current_wifi", "reason");
                foreach (var w in db.Records<WifiChange>("wifi", from, to).Where(w => network == null || w.Current == network || w.Previous == network))
                    await Row(Utc(w.At), Settings.Local(w.At), w.Previous, w.Current, w.Reason);
                break;
            case "heating":
                await Row("started_utc", "ended_utc", "first_observed_utc", "last_observed_utc", "active", "received_utc", "source");
                foreach (var span in Heating(db, device, from, to).Heating)
                    await Row(Utc(span.Start), Utc(span.End), Utc(span.ObservedFrom), Utc(span.ObservedTo), span.Active, Utc(span.Received), "nest");
                break;
            case "gaps":
                await Row("start_utc", "end_utc", "reason");
                foreach (var x in db.Records<Gap>("gap", from, to)) await Row(Utc(x.Start), Utc(x.End), x.Reason);
                break;
        }
        await writer.FlushAsync();

        Task Row(params object?[] values) => writer.WriteLineAsync(string.Join(",", values.Select(Field)));
    }
    private static string Field(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var first = text.TrimStart().FirstOrDefault();
        if (value is string && (first is '=' or '+' or '-' or '@' || (text.Length > 0 && char.IsControl(text[0]))))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public static string Html(Store db, Settings settings, long from, long to, string? network = null, string? device = null)
    {
        var points = Summary(db, settings, from, to, network);
        var incidents = db.Records<Incident>("incident", from, to).Where(i => network == null || i.Network == network).ToArray();
        var gaps = db.Records<Gap>("gap", from, to);
        var snapshots = db.Records<Snapshot>("snapshot", from, to);
        var annotations = db.Records<Annotation>("annotation", from, to);
        var speeds = db.Records<SpeedRun>("speed", from, to).Where(s => network == null || s.Network == network).ToArray();
        var sb = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Home connectivity report</title><link rel="stylesheet" href="/report.css"></head><body><main>
            <h1>Home connectivity report</h1>
            <p>Windows Wi-Fi observation point. All local timestamps include the Eastern time offset. ICMP at intermediate hops is advisory. DOCSIS signal and modem logs require independent verification.</p>
            """);
        sb.AppendLine($"<p><strong>Window</strong> {H(Settings.Local(from))} to {H(Settings.Local(to))}</p>");
        sb.AppendLine("<h2>Probe results</h2><table><tr><th>Wi-Fi</th><th>Layer</th><th>Target</th><th>Protocol</th><th>Attempts</th><th>Loss</th><th>Mean RTT</th></tr>");
        foreach (var group in points.GroupBy(p => (p.Network, p.Layer, p.Id, p.Protocol)))
        {
            var attempts = group.Sum(p => p.Attempts);
            var success = group.Sum(p => p.Successes);
            var average = success == 0 ? "unavailable" : F(group.Sum(p => p.Rtt.GetValueOrDefault() * p.Successes) / success) + " ms";
            sb.AppendLine($"<tr><td>{H(group.Key.Network)}</td><td>{H(group.Key.Layer)}</td><td>{H(group.Key.Id)}</td><td>{H(group.Key.Protocol)}</td><td>{attempts}</td><td>{(attempts == 0 ? "—" : F(100.0 * (attempts - success) / attempts) + "%")}</td><td>{average}</td></tr>");
        }
        sb.AppendLine("</table><h2>Incident timeline</h2>");
        if (incidents.Length == 0) sb.AppendLine("<p>No sustained diagnoses recorded in this window.</p>");
        foreach (var x in incidents)
        {
            sb.AppendLine($"<article><h3>{H(x.Network)} · {H(Settings.Local(x.Start))} to {H(x.End is long end ? Settings.Local(end) : "open")}</h3>");
            sb.AppendLine($"<p><strong>Observed</strong> {H(x.Diagnosis.Observation)}<br><strong>Suspected</strong> {H(x.Diagnosis.Suspected)}<br><strong>Confirmed</strong> {H(x.Diagnosis.Confirmed ?? "Cause unconfirmed.")}</p>");
            var incidentStart = Math.Max(from, x.Start);
            var incidentEnd = Math.Min(to, x.End ?? to);
            var evidenceEnd = Math.Min(incidentEnd, incidentStart + 300000);
            var evidence = incidentStart > Settings.Now - settings.RawDays * 86400000L
                ? db.Points(incidentStart, evidenceEnd, settings, singleWindow: true, network: x.Network)
                : db.ReportMinutes(incidentStart, evidenceEnd, x.Network);
            var results = evidence.Where(p => p.Layer is "gateway" or "public" or "dns" or "modem")
                .Select(p => $"{p.Id} {p.Protocol}: {p.Successes}/{p.Attempts} successful");
            sb.AppendLine($"<p>First five minutes within the report window: {H(string.Join(" · ", results.DefaultIfEmpty("No retained probe samples.")))}</p>");
            foreach (var capture in snapshots.Where(z => z.IncidentId == x.Id))
            {
                sb.AppendLine($"<p>{H(capture.Phase)} at {H(Settings.Local(capture.At))}. Gateway {H(capture.Link.Gateway ?? "unavailable")}, Wi-Fi signal {H(capture.Link.SignalDbm?.ToString() ?? "unavailable")} dBm.</p>");
                if (capture.Wlan.Length > 0) sb.AppendLine($"<pre>{H(string.Join('\n', capture.Wlan))}</pre>");
                if (!string.IsNullOrWhiteSpace(capture.Trace)) sb.AppendLine($"<pre>{H(capture.Trace)}</pre>");
            }
            sb.AppendLine("</article>");
        }
        sb.AppendLine("<h2>Collection gaps and test load</h2><ul>");
        foreach (var gap in gaps) sb.AppendLine($"<li>{H(Settings.Local(gap.Start))} to {H(Settings.Local(gap.End))} · {H(gap.Reason)}</li>");
        foreach (var speed in speeds) sb.AppendLine($"<li>{H(Settings.Local(speed.Start))} · speed test {H(speed.Status)} · down {F(speed.Download)} / up {F(speed.Upload)} Mbps</li>");
        if (gaps.Length + speeds.Length == 0) sb.AppendLine("<li>No recorded gaps or speed tests in this window.</li>");
        sb.AppendLine("</ul><h2>Household annotations</h2><ul>");
        foreach (var a in annotations) sb.AppendLine($"<li>{H(Settings.Local(a.At))} · {H(a.Kind)} · {H(a.Note)}</li>");
        sb.AppendLine("</ul><h2>Wi-Fi changes</h2><ul>");
        foreach (var change in db.Records<WifiChange>("wifi", from, to).Where(w => network == null || w.Previous == network || w.Current == network))
            sb.AppendLine($"<li>{H(Settings.Local(change.At))} · {H(change.Previous)} → {H(change.Current)} · {H(change.Reason)}</li>");
        sb.AppendLine("</ul><h2>Nest-reported heating</h2><ul>");
        var nest = Heating(db, device, from, to);
        foreach (var span in nest.Heating)
            sb.AppendLine($"<li>{H(span.Start is long start ? Settings.Local(start) : "Start unknown")} to {H(span.End is long end ? Settings.Local(end) : span.Active ? "open" : "End unknown")} · first observed {H(Settings.Local(span.ObservedFrom))}</li>");
        foreach (var gap in nest.Gaps)
            sb.AppendLine($"<li>Coverage gap from {H(Settings.Local(gap.Start))} · {H(gap.Reason)}</li>");
        sb.AppendLine("</ul><p>Annotations show timing only. An observed association does not establish a cause.</p></main></body></html>");
        return sb.ToString();
    }
    private static string F(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
    private static string H(string value) => WebUtility.HtmlEncode(value);
    private static string? Utc(long? value) => value is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O") : null;
}
