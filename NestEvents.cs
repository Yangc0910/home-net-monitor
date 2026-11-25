using System.Text.Json;

namespace HomeNet;

public sealed record NestSettings(bool Enabled = false, string CredentialsFile = "nest.credentials.json");
public sealed record NestCredentials(string DeviceAccessProject, string DeviceId, string Subscription, string ClientId,
    string ClientSecret, string RefreshToken, string SubscriberKeyFile);
public sealed record NestEvent(string Device, string Id, long At, long Received, string Kind, string Value);
public sealed record HeatingSpan(long? Start, long? End, long ObservedFrom, long ObservedTo, bool Active, long Received);
public sealed record NestGap(long Start, long? End, string Reason);
public sealed record NestTimeline(HeatingSpan[] Heating, NestGap[] Gaps);
public sealed record NestLive(string Status, string? Hvac, bool? Online, long? LastContact);

public static class NestEvents
{
    public const string Hvac = "sdm.devices.traits.ThermostatHvac";
    public const string Connectivity = "sdm.devices.traits.Connectivity";

    public static IEnumerable<NestEvent> Decode(JsonElement payload, string device, long received)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in payload.EnumerateArray())
                foreach (var e in Decode(item, device, received)) yield return e;
            yield break;
        }
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("eventId", out var eventId) ||
            eventId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(eventId.GetString()) ||
            !payload.TryGetProperty("timestamp", out var timestamp) || !timestamp.TryGetDateTimeOffset(out var time) ||
            !payload.TryGetProperty("resourceUpdate", out var update) ||
            !update.TryGetProperty("name", out var target) || target.GetString() != device ||
            !update.TryGetProperty("traits", out var traits)) yield break;
        var at = time.ToUnixTimeMilliseconds();
        if (at < 946684800000 || at > received + 300000) yield break;
        var connected = Status(traits, Connectivity);
        if (connected is "ONLINE" or "OFFLINE")
            yield return new(device, eventId.GetString()!, at, received, "connectivity", connected);
        var hvac = Status(traits, Hvac);
        if (hvac is "HEATING" or "COOLING" or "OFF")
            yield return new(device, eventId.GetString()!, at, received, "hvac", hvac);
    }

    public static string? Status(JsonElement traits, string name) =>
        traits.TryGetProperty(name, out var trait) && trait.TryGetProperty("status", out var status) ? status.GetString() : null;

    public static NestTimeline Timeline(IEnumerable<NestEvent> source, long from, long to)
    {
        var spans = new List<HeatingSpan>();
        var gaps = new List<NestGap>();
        long? start = null, seen = null, offline = null, feedLost = null;
        bool? heating = null;
        var received = 0L;
        foreach (var e in source.Where(e => e.At < to).OrderBy(e => e.At)
            .ThenBy(e => e.Kind == "connectivity" ? 0 : e.Kind == "hvac" ? 1 : 2).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            switch (e.Kind)
            {
                case "feed":
                    if (e.Value == "unavailable") feedLost ??= e.At;
                    else if (feedLost is long lost)
                    {
                        AddGap(lost, e.At, "event-feed");
                        feedLost = null;
                    }
                    continue;
                case "connectivity":
                    if (e.Value == "OFFLINE")
                    {
                        offline ??= e.At;
                        Close(e.At, null);
                        heating = null;
                    }
                    else if (offline is long disconnected)
                    {
                        AddGap(disconnected, e.At, "thermostat-offline");
                        offline = null;
                    }
                    continue;
            }
            if (offline != null || e.Kind is not ("hvac" or "snapshot")) continue;
            var next = e.Value == "HEATING";
            if (heating == next) continue;
            if (next)
            {
                // Only an actual change event establishes that heating started at a known time.
                start = e.Kind == "hvac" ? e.At : null;
                seen = e.At;
                received = e.Received;
            }
            else Close(e.At, e.Kind == "hvac" ? e.At : null);
            heating = next;
        }
        if (seen is long active) AddSpan(start, null, active, to, true, received);
        if (offline is long offlineAt) AddGap(offlineAt, null, "thermostat-offline");
        if (feedLost is long feedAt) AddGap(feedAt, null, "event-feed");
        return new(spans.ToArray(), gaps.ToArray());

        void Close(long observedTo, long? end)
        {
            if (seen is long first) AddSpan(start, end, first, observedTo, false, received);
            start = null;
            seen = null;
        }
        void AddSpan(long? started, long? ended, long first, long last, bool active, long arrived)
        {
            if (last >= from && first < to) spans.Add(new(started, ended, first, last, active, arrived));
        }
        void AddGap(long started, long? ended, string reason)
        {
            if ((ended ?? to) > from && started < to) gaps.Add(new(started, ended, reason));
        }
    }
}
