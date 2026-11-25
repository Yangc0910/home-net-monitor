using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace HomeNet;

public sealed record Live(long At, Link? Link, Sample[] Samples, Diagnosis? Evidence, Incident? Active, string? StorageError, string[] Wlan, string Network = "unknown");
public sealed class Collector(Settings settings, Store db, Probes probes, SpeedTests speed, ILogger<Collector> logger) : BackgroundService
{
    private volatile Live live = new(0, null, [], null, null, null, []);
    public Live Live => live;
    private readonly Channel<(Incident Event, long At, Link Link, string[] Wlan)> snapshots =
        Channel.CreateBounded<(Incident, long, Link, string[])>(new BoundedChannelOptions(12) { FullMode = BoundedChannelFullMode.DropOldest });
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.WhenAll(Collect(ct), Capture(ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task Collect(CancellationToken ct)
    {
        long? persisted = null;
        var started = false;
        var hadStorageFailure = false;
        var tracker = new Tracker();
        long nextCheck = 0, lastMaintenance = 0;
        Sample[] slow = [];
        string[] wlan = [];
        Link? committedLink = null;
        long session = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.PingSeconds));
            do
            {
                var at = Settings.Now;
                var before = ReadLink();
                var changed = committedLink == null || WifiIdentity.Changed(committedLink, before);
                var full = changed || clock.ElapsedMilliseconds + 50 >= nextCheck;
                if (full)
                {
                    wlan = probes.RecentWlan();
                    nextCheck = (clock.ElapsedMilliseconds + 50) / (settings.CheckSeconds * 1000L) * (settings.CheckSeconds * 1000L) + settings.CheckSeconds * 1000L;
                }
                var samples = await probes.Collect(before, at, full, speed.Loaded, ct);
                var current = ReadLink();
                var network = WifiIdentity.RoundNetwork(before, current);
                var crossed = network == WifiIdentity.Switching;
                var newSession = changed || crossed;
                if (newSession)
                {
                    slow = [];
                    speed.NetworkChanged();
                }
                for (var i = 0; i < samples.Length; i++) samples[i] = samples[i] with { Network = network };
                if (full && !crossed) slow = samples.Where(s => s.Protocol != "icmp").ToArray();
                if (crossed) nextCheck = 0;
                var latest = full || newSession ? samples : [..samples, ..slow];
                var evidence = crossed ? null : Rules.Evaluate(current, latest, full: true);
                var wifi = new WifiRound(at, network, newSession ? at : session, before.Gateway, before.Dns);
                var change = newSession ? new WifiChange(at, committedLink == null ? WifiIdentity.Unknown : WifiIdentity.Network(committedLink),
                    WifiIdentity.Network(current), crossed ? "during-round" : committedLink == null ? "initial" : "connection-changed") : null;
                try
                {
                    if (!started)
                    {
                        persisted = db.State("heartbeat");
                        tracker = new(db.Records<Incident>("incident", at - 86400000, at + 1).FirstOrDefault(x => x.End == null));
                    }
                    Gap? gap = persisted is long previous && (at - previous > settings.PingSeconds * 2200L || hadStorageFailure)
                        ? new(previous + 1, at, hadStorageFailure ? "storage-unavailable" : started ? "host-suspended-or-collector-delayed" : "service-not-running") : null;
                    var next = newSession ? new Tracker() : tracker;
                    Incident[] changes = newSession && tracker.Active is { } active
                        ? [active with { End = at, EndReason = change?.Reason == "initial" ? "collector-restarted" : "wifi-session-ended" }] : [];
                    // Cross-protocol conclusions advance only on fresh cross-protocol observations.
                    if (!crossed && (full || evidence?.Kind is "wifi" or "local" or "icmp" or "latency" &&
                        next.Active?.Diagnosis.Kind is null or "wifi" or "local" or "icmp" or "latency"))
                    {
                        (next, var diagnosed) = next.Step(at, evidence, network);
                        changes = [..changes, ..diagnosed];
                    }
                    db.Write(at, samples, gap, changes, wifi, change);
                    tracker = next;
                    session = wifi.Session;
                    committedLink = current;
                    persisted = at;
                    started = true;
                    hadStorageFailure = false;
                    foreach (var incident in changes) snapshots.Writer.TryWrite((incident, at, current, wlan));
                    live = new(at, current, latest, evidence, tracker.Active, null, wlan, network);
                    if (full && !crossed) RunMaintenanceAction(() => speed.Observe(current, latest, at), "Speed scheduler unavailable");
                    if (at - lastMaintenance >= 60000)
                    {
                        RunMaintenanceAction(() => db.Maintain(at, settings), "Database maintenance unavailable");
                        lastMaintenance = at;
                    }
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                {
                    if (!hadStorageFailure) logger.LogWarning(ex, "Storage unavailable");
                    hadStorageFailure = true;
                    live = new(at, current, latest, evidence, tracker.Active, "Storage unavailable", wlan, network);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Collector failed");
            Environment.Exit(1);
        }
        finally { snapshots.Writer.TryComplete(); }
    }

    private Link ReadLink()
    {
        try { return probes.ReadLink(); }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        { return new(false, false, null, null, [], null, null, null); }
    }

    private void RunMaintenanceAction(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "{Message}", message); }
    }

    private async Task Capture(CancellationToken ct)
    {
        var lastTrace = new Dictionary<string, long>();
        await foreach (var item in snapshots.Reader.ReadAllAsync(ct))
        {
            var phase = item.Event.End.HasValue ? "recovery-or-transition" : "start";
            string? trace = null;
            if (item.At - lastTrace.GetValueOrDefault(phase) >= 60000)
            {
                lastTrace[phase] = item.At;
                try { trace = await probes.Trace(ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { trace = "Trace unavailable (" + ex.GetType().Name + ")"; }
            }
            try { db.Put("snapshot", item.Event.Id * 2 + (item.Event.End.HasValue ? 1 : 0), item.At, new Snapshot(item.At, item.Event.Id, phase, item.Link, item.Wlan, trace)); }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            { logger.LogWarning(ex, "Could not persist diagnostic snapshot"); }
        }
    }
}
