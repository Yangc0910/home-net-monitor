namespace HomeNet;

public static class Rules
{
    public static Diagnosis? Evaluate(Link link, IReadOnlyCollection<Sample> samples, bool full)
    {
        var gateway = samples.FirstOrDefault(x => x.Id == "gateway");
        var external = samples.Where(x => x.Layer == "public").ToArray();
        var downstream = samples.Where(x => x.Layer is "upstream" or "isp" or "public").ToArray();
        var tcpOk = external.Count(x => x.Protocol == "tcp" && x.Ok);
        var publicPingFailed = external.Count(x => x.Protocol == "icmp" && x.Failed);
        var tcpFailed = external.Count(x => x.Protocol == "tcp" && x.Failed);
        var httpsFailed = external.Count(x => x.Protocol == "https" && x.Failed);
        bool Failed(string id) => samples.Any(x => x.Id == id && x.Failed);
        bool Ok(string id) => samples.Any(x => x.Id == id && x.Ok);
        if (link.Known && !link.Connected)
            return new("wifi", "Windows reported the Wi-Fi interface down.", "PC Wi-Fi adapter, access point, or radio path.", "Wi-Fi interface down on this PC.");
        if (gateway is null || !gateway.Ok && !gateway.Failed)
            return new("unclear", "No usable IPv4 gateway observation.", "Association, address assignment, or routing.");
        if (gateway.Failed)
        {
            if (downstream.Any(x => x.Ok))
                return new("icmp", "The gateway missed ICMP while downstream probes replied.", "Gateway ICMP handling.");
            return new("local", "The gateway and downstream probes stopped replying.", "PC Wi-Fi, mesh link, or gateway.");
        }
        if (full && tcpOk >= 2 && Failed("dns-local") && Ok("dns-reference"))
            return new("dns", "Configured DNS failed while the reference resolver and direct TCP replied.", "Configured DNS resolver or its forwarding path.");
        if (full && publicPingFailed >= 2 && (tcpFailed >= 2 || httpsFailed >= 2) && tcpOk == 0)
        {
            if (samples.Any(x => x.Layer == "modem" && x.Failed))
                return new("modem", "Gateway replied, public targets failed on multiple protocols, and the configured modem probe failed.",
                    "Modem, its management path, or its connection to the mesh router.");
            if (samples.Any(x => x.Layer == "isp" && x.Failed) && samples.Any(x => x.Layer is "upstream" or "modem" && x.Ok))
                return new("isp", "Local gateway and upstream device replied, while the ISP reference and multiple public probes failed.",
                    "Modem-to-ISP access or ISP path. Local reachability can persist after DOCSIS drops.");
            return new("wan", "Gateway replied while multiple public targets failed on ICMP and TCP or HTTPS.", "Broadband or upstream path.");
        }
        if (full && samples.Any(x => x.Layer == "modem" && x.Failed))
            return new("modem-status", "The configured modem probe failed.", "Modem management path. DOCSIS status unavailable.");
        if (full && tcpOk >= 2 && (httpsFailed == 1 || tcpFailed == 1))
            return new("external", "One external service or TCP target failed while other public TCP targets replied.", "That service or its path.");
        if (full && (publicPingFailed >= 2 || httpsFailed >= 2 || tcpFailed >= 2) && tcpOk >= 2)
            return new("unclear", "External probe results disagree across protocols.", "Protocol-specific filtering, DNS, or partial degradation.");
        if (!samples.Any(s => s.Loaded) && (gateway.Ms >= 100 ||
            external.Count(x => x.Protocol == "icmp" && x.Ms >= 250) >= 2))
            return new("latency", "Repeated probe latency exceeded the configured baseline thresholds.", "Queueing, Wi-Fi interference, or path congestion.");
        return null;
    }
}

public sealed record Tracker(Incident? Active = null, Diagnosis? Candidate = null, long First = 0, int Count = 0, int Good = 0)
{
    public (Tracker Next, Incident[] Changes) Step(long at, Diagnosis? evidence, string network = "legacy")
    {
        if (evidence == null)
        {
            if (Active == null) return (new(), []);
            if (Good + 1 < 2) return (this with { Good = Good + 1, Count = 0, Candidate = null }, []);
            return (new(), [Active with { End = at }]);
        }
        if (Active?.Diagnosis.Kind == evidence.Kind) return (new(Active), []);
        var same = Candidate?.Kind == evidence.Kind;
        var next = new Tracker(Active, evidence, same ? First : at, same ? Count + 1 : 1);
        if (next.Count < 3) return (next, []);
        var current = new Incident(at, next.First, null, evidence, network);
        return (new(current), Active == null ? [current] : [Active with { End = next.First }, current]);
    }
}
