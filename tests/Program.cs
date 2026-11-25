using HomeNet;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--ipv4"))
{
    if (!args.Contains("--json") || !args.Contains("disabled") || !args.Contains("--local-json")) return;
    await Task.Delay(1200);
    Console.WriteLine("""[{"download":150000000,"upload":25000000,"ping":18,"ip":"203.0.113.41"}]""");
    return;
}

var link = new Link(true, true, "Wi-Fi", "192.168.86.1", ["192.168.86.1"], -50, 0, 0);
var start = Settings.Now / 60000 * 60000 - 60000;
Sample S(string id, string domain, string protocol, bool ok, double ms = 20, long? at = null, bool loaded = false) =>
    new(at ?? start, id, id, domain, protocol, ok ? "ok" : "timeout", ok ? ms : null, Loaded: loaded);
Sample[] Good() =>
[
    S("gateway","gateway","icmp",true), S("a","public","icmp",true), S("b","public","icmp",true),
    S("a","public","tcp",true), S("b","public","tcp",true), S("dns-local","dns","dns",true), S("dns-reference","dns","dns",true)
];
void Expect(bool ok, string name)
{
    if (!ok) throw new Exception("Failed: " + name);
    Console.WriteLine("Passed: " + name);
}
Diagnosis? Eval(params Sample[] samples) => Rules.Evaluate(link, samples, true);
Expect(Eval(Good()) == null, "healthy");
Expect(Rules.Evaluate(link with { Connected = false }, Good(), true)?.Kind == "wifi", "observed Wi-Fi disconnection");
Expect(Eval(Good().Select(x => x.Id == "dns-local" ? x with { Outcome = "failure", Ms = null } : x).ToArray())?.Kind == "dns", "isolated configured DNS failure");
Expect(Eval([..Good(), S("isp","isp","icmp",false)]) == null, "intermediate ICMP failure with working destination");
Expect(Eval([..Good(), S("web-a","public","https",false), S("web-b","public","https",true)])?.Kind == "external", "one external service fails");
var wan = Good().Select(x => x.Layer == "public" ? x with { Outcome = "timeout", Ms = null } : x).ToArray();
Expect(Eval(wan)?.Kind == "wan", "cross-protocol WAN failure");
Expect(Eval([..wan, S("upstream","upstream","icmp",true), S("isp","isp","icmp",false)])?.Kind == "isp", "suspected access path");
Expect(Eval([..wan, S("modem-status","modem","http",false)])?.Kind == "modem", "modem path suspicion");
Expect(Eval(Good().Select(x => x.Id == "gateway" ? x with { Ms = 140, Loaded = true } : x).ToArray()) == null, "load does not create latency incident");
var evidence = Eval(wan);
var tracker = new Tracker();
tracker = tracker.Step(start, evidence).Next;
Expect(tracker.Step(start + 5000, null).Changes.Length == 0, "short transient creates no incident");
tracker = tracker.Step(start + 5000, evidence).Next;
var opened = tracker.Step(start + 10000, evidence);
Expect(opened.Changes.Single().Start == start, "three rounds backdate event to first observation");
var recovered = opened.Next.Step(start + 15000, null).Next.Step(start + 20000, null);
Expect(recovered.Changes.Single().End == start + 20000, "two healthy rounds close event");

var settings = new Settings();
var folder = Path.Combine(Path.GetTempPath(), "homenet-replay-" + Guid.NewGuid());
var db = new Store(new DataPath(folder));
var samples = new[] { S("a","public","icmp",true,10), S("a","public","icmp",true,25, start + 5000),
    S("a","public","icmp",false,at:start + 10000), S("a","public","icmp",true,50, start + 15000) };
db.Write(start + 15000, samples, new Gap(start - 20000, start - 1, "test-gap"), opened.Changes);
var row = db.Points(start, start + 60000, settings).Single();
Expect(row.Attempts == 4 && row.Loss == 25 && row.Jitter == 15 && row.P95 == 50, "history denominator, p95 and contiguous jitter");
db.Maintain(start + 60000, settings);
db.Maintain(start + 61000, settings);
Expect(db.Points(start, start + 60000, settings, summary: true).Single() == row, "rollup does not duplicate");
Expect(db.State("heartbeat") == start + 15000 && db.Records<Gap>("gap", start - 60000, start + 60000).Length == 1, "persistent heartbeat and collection gap");
db.Add(start + 7000, "hvac-start", "=formula");
var correlation = Reports.Correlate(db, settings, start, start + 60000).Single();
Expect(correlation.Before.Single().Attempts == 2 && correlation.After.Single().Loss == 50, "HVAC event uses matching raw samples");
await using var output = new MemoryStream();
await Reports.Csv(output, db, settings, "annotations", start, start + 60000);
Expect(System.Text.Encoding.UTF8.GetString(output.ToArray()).Contains("'=formula"), "CSV formula protection");
var copy = Path.Combine(folder, "backup.db");
db.Backup(copy);
Expect(new FileInfo(copy).Length > 0, "WAL-safe backup");
var serverFile = Path.Combine(folder, "server.json");
File.WriteAllText(serverFile, """
    [{"server":"https://speedtest.example.com/","dlURL":"down","ulURL":"up","pingURL":"ping","getIpURL":"ip"}]
    """);
var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Settings.Eastern);
var speedSettings = new Settings { Speed = new(true, Environment.ProcessPath, serverFile, $"{(local.Hour + 12) % 24:00}:00", 15) };
var runner = new SpeedTests(speedSettings, new(folder), db, new Lifetime(), NullLogger<SpeedTests>.Instance);
var now = Settings.Now;
runner.Observe(link, Good(), now - 15000);
runner.Observe(link with { Rx = 3000000 }, Good(), now);
Expect(runner.Manual() == "waiting-for-idle", "busy-link skip");
runner = new SpeedTests(speedSettings, new(folder), db, new Lifetime(), NullLogger<SpeedTests>.Instance);
runner.Observe(link, Good(), now - 30000);
runner.Observe(link, Good(), now - 15000);
runner.Observe(link, Good(), now);
Expect(runner.Manual() == null && runner.Loaded, "speed command uses test backend");
Expect(runner.Manual() == "already-running", "parallel speed test is rejected");
for (var i = 0; runner.Loaded && i < 50; i++) await Task.Delay(100);
var speedResult = db.Records<SpeedRun>("speed", now - 1, Settings.Now + 1).Single();
Expect(speedResult.Status == "ok" && speedResult.Download == 150 && speedResult.Upload == 25, "speed result and Mbps conversion");

var nestLink = link with { Ssid = WifiIdentity.NestWifi, WlanSequence = 100 };
var astoundLink = link with { Ssid = WifiIdentity.Astound, Gateway = "192.168.0.1", Dns = ["192.168.0.1"], WlanSequence = 103 };
Expect(WifiIdentity.RoundNetwork(nestLink, astoundLink) == WifiIdentity.Switching, "cross-round SSID change");
Expect(WifiIdentity.RoundNetwork(nestLink, nestLink with { WlanSequence = 104 }) == WifiIdentity.Switching, "fast reconnect during a round");
Expect(WifiIdentity.Network(astoundLink) == WifiIdentity.Astound && WifiIdentity.Network(link) == WifiIdentity.Unknown, "SSID and unknown are distinct");

var oldDir = Path.Combine(folder, "v1");
Directory.CreateDirectory(oldDir);
using (var original = new SqliteConnection("Data Source=" + Path.Combine(oldDir, "monitor.db")))
{
    original.Open();
    using var command = original.CreateCommand();
    command.CommandText = """
        PRAGMA user_version=1;
        CREATE TABLE targets(ref INTEGER PRIMARY KEY,id TEXT,label TEXT,layer TEXT,protocol TEXT,UNIQUE(id,label,layer,protocol));
        CREATE TABLE samples(at INTEGER,ref INTEGER,outcome TEXT,ms REAL,detail TEXT,loaded INTEGER);
        CREATE TABLE minutes(at INTEGER,ref INTEGER,attempts INTEGER,successes INTEGER,loaded INTEGER,rtt REAL,p95 REAL,jitter REAL);
        INSERT INTO targets VALUES(1,'gateway','old gateway','gateway','icmp');
        INSERT INTO samples VALUES($at,1,'ok',8,NULL,0);
        INSERT INTO minutes VALUES($at,1,1,1,0,8,8,NULL);
        """;
    command.Parameters.AddWithValue("$at", start);
    command.ExecuteNonQuery();
}
var upgraded = new Store(new DataPath(oldDir));
Expect(upgraded.Samples(start, start + 1000).Single().Network == WifiIdentity.Legacy &&
    upgraded.Points(start, start + 1000, settings, summary: true).Single().Network == WifiIdentity.Legacy,
    "v1 database keeps raw rows and minute rollups without SSID");
const string device = "enterprises/1cdd3304-2439-4ead-aad7-af86c6cb15be/devices/fixture";
var begin = start + 60000;
void NetworkRound(long at, string network, long epoch, bool okay)
{
    Sample[] taken = [S("gateway","gateway","icmp",okay,at:at), S("a","public","icmp",okay,at:at), S("dns-local","dns","dns",okay,at:at)];
    upgraded.Write(at, taken, null, [], new(at,network,epoch,astoundLink.Gateway,astoundLink.Dns));
}
NetworkRound(begin, WifiIdentity.NestWifi, 1, true);
NetworkRound(begin + 5000, WifiIdentity.Switching, 2, false);
NetworkRound(begin + 10000, WifiIdentity.Astound, 3, false);
NetworkRound(begin + 15000, WifiIdentity.NestWifi, 4, true);
Expect(upgraded.Points(begin, begin + 60000, settings, series: "gateway/icmp", network: WifiIdentity.NestWifi).Single() is { Attempts: 2, Loss: 0, Jitter: null },
    "filter and jitter separate Wi-Fi sessions");
Expect(upgraded.Points(begin, begin + 60000, settings, network: WifiIdentity.Switching).All(p => p.Attempts == 1 && p.Loss == 100),
    "transition retained separately from home network counts");
upgraded.Maintain(begin + 60000, settings);
Expect(upgraded.Points(begin, begin + 60000, settings, summary: true, network: WifiIdentity.Astound, series: "gateway/icmp").Single().Loss == 100,
    "minute rollup preserves network attribution");

NestEvent E(string id, long at, string kind, string value, long? received = null) => new(device,id,at,received ?? at,kind,value);
var from = begin - 60000;
var to = begin + 60000;
var fixture = new[]
{
    E("stop",begin+35000,"hvac","OFF"),
    E("first",from,"snapshot","HEATING"), E("until",from+10000,"hvac","OFF"),
    E("start",begin+10000,"hvac","HEATING",begin+45000),
    E("dup",begin+22000,"hvac","HEATING"),
    E("feed-down",begin+17000,"feed","unavailable"), E("feed-up",begin+45000,"feed","available"),
    E("offline",begin+50000,"connectivity","OFFLINE")
};
upgraded.WriteNest(fixture);
upgraded.WriteNest([fixture[0],fixture[3]]);
var saved = upgraded.NestHistory(device,to);
var timeline = NestEvents.Timeline(saved,from,to);
Expect(saved.Length == fixture.Length, "Pub/Sub redelivery is idempotent at the SQLite boundary");
Expect(timeline.Heating[0] is { Start: null, End: not null } && timeline.Heating[1].Start == begin+10000 &&
    timeline.Heating[1].End == begin+35000 && timeline.Heating[1].Received == begin+45000,
    "initial heating has unknown start and delayed events use the source clock");
Expect(timeline.Gaps.Any(g => g.Reason == "event-feed" && g.End == begin+45000) &&
    timeline.Gaps.Any(g => g.Reason == "thermostat-offline" && g.End == null), "feed recovery and device offline are distinct");
var comparisonWindows = Reports.Correlate(upgraded, settings, begin, to);
Expect(comparisonWindows.Any(w => w.Network == WifiIdentity.NestWifi && w.Event.Source == "nest") &&
    comparisonWindows.Any(w => w.Network == WifiIdentity.Astound && w.Event.Source == "nest") &&
    comparisonWindows.All(w => w.Before.Concat(w.After).Concat(w.Control).All(p => p.Network == w.Network)),
    "HVAC windows containing a switch are compared by network");

using var handler = new NestFixture(device);
using var client = new HttpClient(handler);
var cloud = new NestCloud(client,new("1cdd3304-2439-4ead-aad7-af86c6cb15be","fixture","projects/fixture/subscriptions/heating",
    "fixture-client","fixture-secret","refresh-fixture","not-read.json"),folder,_=>Task.FromResult("pubsub-fixture"));
Expect(await cloud.Snapshot(CancellationToken.None) == ("HEATING",true), "Nest token renewal and device state use read-only endpoints");
var messages = await cloud.Pull(CancellationToken.None);
Expect(messages.Events.Length == 1 && messages.Acks.Length == 2 && messages.Events[0].Value == "HEATING", "Pub/Sub decodes selected device and skips unrelated payload");
upgraded.WriteNest(messages.Events);
await cloud.Acknowledge(messages.Acks, CancellationToken.None);
Expect(handler.Paths.Count == 4 && handler.Paths.Last().EndsWith(":acknowledge"), "acknowledgment follows successful local persistence");

Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
Directory.Delete(folder, recursive: true);

sealed class NestFixture(string device) : HttpMessageHandler
{
    public List<string> Paths { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Paths.Add(request.RequestUri!.AbsolutePath);
        string body = request.RequestUri.AbsolutePath switch
        {
            "/token" => """{"access_token":"sdm-fixture","expires_in":3600}""",
            var path when path.StartsWith("/v1/enterprises/") => """
                {"traits":{"sdm.devices.traits.ThermostatHvac":{"status":"HEATING"},"sdm.devices.traits.Connectivity":{"status":"ONLINE"}}}
                """,
            var path when path.EndsWith(":pull") => JsonSerializer.Serialize(new { receivedMessages = new[]
            {
                new { ackId = "a", message = new { data = Encode(device) } },
                new { ackId = "b", message = new { data = Encode(device + "-other") } }
            }}),
            var path when path.EndsWith(":acknowledge") => "{}",
            _ => throw new InvalidOperationException("Unexpected Google endpoint.")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
    private static string Encode(string name) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
    {
        eventId = "fixture-event", timestamp = DateTimeOffset.UtcNow,
        resourceUpdate = new { name, traits = new Dictionary<string, object> { [NestEvents.Hvac] = new { status = "HEATING" } } }
    }));
}

sealed class Lifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}