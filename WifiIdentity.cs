using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using Windows.Networking.Connectivity;

namespace HomeNet;

public sealed record WifiRound(long At, string Network, long Session, string? Gateway, string[] Dns);
public sealed record WifiChange(long At, string Previous, string Current, string Reason);

public static class WifiIdentity
{
    public const string NestWifi = "Gongxifacai";
    public const string Astound = "Astound";
    public const string Unknown = "unknown";
    public const string Other = "other";
    public const string Switching = "switching";
    public const string Legacy = "legacy";
    public static readonly string[] HomeNetworks = [NestWifi, Astound];

    public static bool Valid(string? name) => name == null || name is NestWifi or Astound or Unknown or Other or Switching or Legacy;

    public static string Network(Link link) => link.Ssid switch
    {
        NestWifi => NestWifi,
        Astound => Astound,
        null or "" => Unknown,
        _ => Other
    };

    public static string RoundNetwork(Link before, Link after) => Changed(before, after) ? Switching : Network(after);

    public static bool Changed(Link left, Link right) =>
        left.Connected != right.Connected || left.Ssid != right.Ssid || left.WlanSequence != right.WlanSequence ||
        left.Gateway != right.Gateway || !left.Dns.SequenceEqual(right.Dns);

    public static (string? Ssid, long Sequence) Read(string adapterId, bool connected)
    {
        if (!Guid.TryParse(adapterId, out var adapter)) return (null, 0);
        string? ssid = null;
        if (connected)
        {
            try
            {
                var ssids = NetworkInformation.GetConnectionProfiles()
                    .Where(p => p.IsWlanConnectionProfile && p.NetworkAdapter?.NetworkAdapterId == adapter)
                    .Select(p => p.WlanConnectionProfileDetails?.GetConnectedSsid())
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                if (ssids.Length == 1) ssid = ssids[0];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { /* WLAN events are the secondary source. */ }
        }
        try
        {
            var id = adapter.ToString("B");
            var upper = id.ToUpperInvariant();
            var xpath = $"*[System[(EventID=8001 or EventID=8003)] and EventData[Data[@Name='InterfaceGuid']='{id}' or Data[@Name='InterfaceGuid']='{upper}']]";
            using var reader = new EventLogReader(new EventLogQuery("Microsoft-Windows-WLAN-AutoConfig/Operational", PathType.LogName, xpath)
            { ReverseDirection = true });
            using var last = reader.ReadEvent();
            if (last == null) return (ssid, 0);
            if (connected && ssid == null && last.Id == 8001 && last.TimeCreated is DateTime time &&
                DateTime.Now - time < TimeSpan.FromMilliseconds(Environment.TickCount64))
            {
                var xml = XDocument.Parse(last.ToXml());
                ssid = xml.Descendants().FirstOrDefault(n => n.Name.LocalName == "Data" && (string?)n.Attribute("Name") == "SSID")?.Value;
            }
            return (ssid, last.RecordId ?? 0);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException or System.Xml.XmlException)
        { return (ssid, 0); }
    }
}
