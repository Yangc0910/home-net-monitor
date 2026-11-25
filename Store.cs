using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HomeNet;

public sealed class Store(DataPath path)
{
    private readonly object gate = new();
    private bool initialized;
    private SqliteConnection Open()
    {
        lock (gate)
        {
            Directory.CreateDirectory(path.Directory);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(path.Directory, "monitor.db"), DefaultTimeout = 2
            }.ToString());
            try
            {
                connection.Open();
                if (!initialized)
                {
                    using var cmd = Command(connection, """
                        PRAGMA journal_mode=WAL;
                        CREATE TABLE IF NOT EXISTS targets(ref INTEGER PRIMARY KEY,id TEXT,label TEXT,layer TEXT,protocol TEXT,UNIQUE(id,label,layer,protocol));
                        CREATE TABLE IF NOT EXISTS samples(at INTEGER,ref INTEGER,outcome TEXT,ms REAL,detail TEXT,loaded INTEGER);
                        CREATE INDEX IF NOT EXISTS sample_time ON samples(at);
                        CREATE TABLE IF NOT EXISTS minutes(at INTEGER,ref INTEGER,attempts INTEGER,successes INTEGER,loaded INTEGER,rtt REAL,p95 REAL,jitter REAL);
                        CREATE INDEX IF NOT EXISTS minute_time ON minutes(at);
                        CREATE TABLE IF NOT EXISTS records(type TEXT,id INTEGER,at INTEGER,json TEXT,PRIMARY KEY(type,id)) WITHOUT ROWID;
                        CREATE INDEX IF NOT EXISTS record_time ON records(type,at);
                        CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY,value INTEGER);
                        """);
                    cmd.ExecuteNonQuery();
                    using var version = Command(connection, "PRAGMA user_version");
                    var schema = (long)version.ExecuteScalar()!;
                    if (schema > 2) throw new InvalidDataException("The monitor database requires a newer version.");
                    if (schema < 2)
                    {
                        using var transaction = connection.BeginTransaction();
                        using var migration = Command(connection, """
                            ALTER TABLE minutes ADD COLUMN network TEXT NOT NULL DEFAULT 'legacy';
                            ALTER TABLE minutes ADD COLUMN rounds INTEGER NOT NULL DEFAULT 0;
                            CREATE TABLE wifi_rounds(at INTEGER PRIMARY KEY,network TEXT NOT NULL,session INTEGER NOT NULL,gateway TEXT,dns TEXT NOT NULL);
                            CREATE TABLE nest_events(device TEXT NOT NULL, id TEXT NOT NULL, at INTEGER NOT NULL, received INTEGER NOT NULL, kind TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY(device,id,kind)) WITHOUT ROWID;
                            CREATE INDEX nest_event_time ON nest_events(device,at);
                            PRAGMA user_version=2;
                            """, transaction);
                        migration.ExecuteNonQuery();
                        transaction.Commit();
                    }
                    initialized = true;
                }
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Key, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public void Write(long at, IList<Sample> samples, Gap? gap, IEnumerable<Incident> incidents, WifiRound? wifi = null, WifiChange? change = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var insert = Command(c, "INSERT INTO samples VALUES($at,$ref,$o,$ms,$detail,$loaded)", tx,
            ("$at", 0), ("$ref", 0), ("$o", ""), ("$ms", null), ("$detail", null), ("$loaded", 0));
        foreach (var s in samples)
        {
            using var definition = Command(c, """
                INSERT OR IGNORE INTO targets(id,label,layer,protocol) VALUES($id,$label,$layer,$protocol);
                SELECT ref FROM targets WHERE id=$id AND label=$label AND layer=$layer AND protocol=$protocol;
                """, tx, ("$id", s.Id), ("$label", s.Label), ("$layer", s.Layer), ("$protocol", s.Protocol));
            var reference = (long)definition.ExecuteScalar()!;
            object?[] values = [s.At, reference, s.Outcome, s.Ms, s.Detail, s.Loaded ? 1 : 0];
            for (var i = 0; i < values.Length; i++) insert.Parameters[i].Value = values[i] ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        if (wifi != null)
        {
            using var context = Command(c, "INSERT OR REPLACE INTO wifi_rounds VALUES($at,$net,$session,$gateway,$dns)", tx,
                ("$at", at), ("$net", wifi.Network), ("$session", wifi.Session), ("$gateway", wifi.Gateway), ("$dns", string.Join(",", wifi.Dns)));
            context.ExecuteNonQuery();
        }
        if (change != null) Put(c, "wifi", change.At, change.At, change, tx);
        if (gap != null) Put(c, "gap", gap.Start, gap.Start, gap, tx);
        foreach (var incident in incidents) Put(c, "incident", incident.Id, incident.Start, incident, tx);
        Set(c, "heartbeat", at, tx);
        tx.Commit();
    }
    public long? State(string key)
    {
        using var c = Open();
        return State(c, key);
    }
    private static long? State(SqliteConnection c, string key, SqliteTransaction? tx = null)
    {
        using var cmd = Command(c, "SELECT value FROM state WHERE key=$k", tx, ("$k", key));
        return cmd.ExecuteScalar() as long?;
    }
    private static void Set(SqliteConnection c, string key, long value, SqliteTransaction tx)
    {
        using var cmd = Command(c, "INSERT OR REPLACE INTO state VALUES($k,$v)", tx, ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }
    private static void Put<T>(SqliteConnection c, string type, long id, long at, T value, SqliteTransaction? tx = null)
    {
        using var cmd = Command(c, "INSERT OR REPLACE INTO records VALUES($type,$id,$at,$json)", tx,
            ("$type", type), ("$id", id), ("$at", at), ("$json", JsonSerializer.Serialize(value, Settings.Json)));
        cmd.ExecuteNonQuery();
    }
    public void Put<T>(string type, long id, long at, T value)
    {
        using var c = Open();
        Put(c, type, id, at, value);
    }
    public Annotation Add(long at, string kind, string note)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = Settings.Now;
        var id = Math.Max(now, (State(c, "annotationId", tx) ?? 0) + 1);
        var entry = new Annotation(id, at, now, kind, note, "manual");
        Set(c, "annotationId", id, tx);
        Put(c, "annotation", id, at, entry, tx);
        tx.Commit();
        return entry;
    }
    public T[] Records<T>(string type, long from, long to)
    {
        using var c = Open();
        using var cmd = Command(c, """
            SELECT json FROM records WHERE type=$type AND at<$to AND
            (at >= $from OR ($type='incident' AND (json_extract(json,'$.end') IS NULL OR json_extract(json,'$.end') >= $from))
                         OR ($type='gap' AND json_extract(json,'$.end') >= $from)) ORDER BY at DESC
            """, null, ("$type", type), ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var rows = new List<T>();
        while (r.Read()) rows.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Settings.Json)!);
        return rows.ToArray();
    }

    // Jitter uses adjacent successful rounds within a bucket. A failure, skipped probe, or time gap breaks the sequence.
    private const string Aggregate = """
        WITH previous AS (
          SELECT samples.*,protocol,(samples.at/$bucket)*$bucket AS slot,coalesce(network,'legacy') AS net,
            lag(ms) OVER w AS prev, lag(samples.at) OVER w AS prev_at,
            coalesce(session,0) AS epoch, lag(coalesce(session,0)) OVER w AS prev_epoch,
            CASE WHEN ms IS NOT NULL THEN row_number() OVER s END AS rank,
            count(ms) OVER p AS good
          FROM samples JOIN targets USING(ref) LEFT JOIN wifi_rounds USING(at)
          WHERE samples.at >= $from AND samples.at < $to AND ($network IS NULL OR coalesce(network,'legacy')=$network)
          WINDOW p AS (PARTITION BY samples.at/$bucket,ref,coalesce(network,'legacy')),
                 w AS (p ORDER BY samples.at),
                 s AS (p ORDER BY ms IS NULL,ms)
        )
        SELECT slot AS at,ref,
          sum(outcome IN ('ok','failure','timeout')) AS attempts,sum(outcome='ok') AS successes,sum(loaded) AS loaded,
          avg(ms) AS rtt,max(CASE WHEN rank=(95*good+99)/100 THEN ms END) AS p95,
          avg(CASE WHEN epoch=prev_epoch AND at-prev_at <= CASE WHEN protocol='icmp' THEN $ping ELSE $check END THEN abs(ms-prev) END) AS jitter,
          net AS network,count(*) AS rounds
        FROM previous GROUP BY slot,ref,net
        """;
    private static (string, object?)[] AggregationArgs(long from, long to, long bucket, Settings settings, string? network = null) =>
        [("$from", from), ("$to", to), ("$bucket", bucket), ("$ping", settings.PingSeconds * 1500), ("$check", settings.CheckSeconds * 1500), ("$network", network)];

    public Point[] Points(long from, long to, Settings settings, bool summary = false, bool singleWindow = false, string? series = null, string? network = null)
    {
        using var c = Open();
        if (summary)
        {
            using var minute = Command(c, "SELECT at,id,label,layer,protocol,attempts,successes,loaded,rtt,p95,jitter,network,rounds FROM minutes JOIN targets USING(ref) WHERE at >= $from AND at < $to AND ($series IS NULL OR id||'/'||protocol=$series) AND ($network IS NULL OR network=$network) ORDER BY at,id,protocol", null, ("$from", from / 60000 * 60000), ("$to", to), ("$series", series), ("$network", network));
            return ReadPoints(minute);
        }
        using var cmd = Command(c, "SELECT at,id,label,layer,protocol,attempts,successes,loaded,rtt,p95,jitter,network,rounds FROM (" + Aggregate + ") JOIN targets USING(ref) WHERE ($series IS NULL OR id||'/'||protocol=$series) ORDER BY at,id,protocol", null, [..AggregationArgs(from, to, singleWindow ? long.MaxValue : 60000, settings, network), ("$series", series)]);
        return ReadPoints(cmd);
    }
    public object[] Series()
    {
        using var c = Open();
        using var query = Command(c, "SELECT DISTINCT id,label,protocol FROM targets ORDER BY ref");
        using var reader = query.ExecuteReader();
        var rows = new List<object>();
        while (reader.Read()) rows.Add(new { id = reader.GetString(0), label = reader.GetString(1), protocol = reader.GetString(2) });
        return rows.ToArray();
    }

    public Point[] ReportMinutes(long from, long to, string? network = null)
    {
        using var c = Open();
        using var cmd = Command(c, """
            SELECT 0,id,max(label),layer,protocol,sum(attempts),sum(successes),sum(loaded),
                sum(rtt*successes)/nullif(sum(successes),0),NULL,NULL,network,sum(rounds)
            FROM minutes JOIN targets USING(ref) WHERE at >= $from AND at < $to AND ($network IS NULL OR network=$network)
            GROUP BY id,layer,protocol,network ORDER BY network,layer,id,protocol
            """, null, ("$from", from / 60000 * 60000), ("$to", to), ("$network", network));
        return ReadPoints(cmd);
    }

    private static Point[] ReadPoints(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var items = new List<Point>();
        while (r.Read()) items.Add(new Point(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), Real(r, 8), Real(r, 9), Real(r, 10), r.GetString(11), r.GetInt64(12)));
        return items.ToArray();
    }
    private static double? Real(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    public IEnumerable<Sample> Samples(long from, long to, string? network = null)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT samples.at,id,label,layer,protocol,outcome,ms,detail,loaded,coalesce(network,'legacy'),gateway,dns FROM samples JOIN targets USING(ref) LEFT JOIN wifi_rounds USING(at) WHERE samples.at >= $from AND samples.at < $to AND ($network IS NULL OR coalesce(network,'legacy')=$network) ORDER BY samples.at", null, ("$from", from), ("$to", to), ("$network", network));
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return new Sample(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            Real(r, 6), r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11));
    }
    public void Maintain(long at, Settings settings)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var complete = at / 60000 * 60000;
        var from = State(c, "rollup", tx) ?? (at - 60000) / 60000 * 60000;
        if (from < complete)
        {
            // Bound recovery work after a long gap.
            from = Math.Max(from, (at - settings.RawDays * 86400000L) / 60000 * 60000);
            var to = Math.Min(complete, from + 1440 * 60000L);
            using var roll = Command(c, "INSERT INTO minutes " + Aggregate, tx, AggregationArgs(from, to, 60000, settings));
            roll.ExecuteNonQuery();
            Set(c, "rollup", to, tx);
        }
        if (at - (State(c, "pruned", tx) ?? 0) > 3600000)
        {
            using var trim = Command(c, """
                DELETE FROM samples WHERE at < min($raw,coalesce((SELECT value FROM state WHERE key='rollup'),$raw));
                DELETE FROM wifi_rounds WHERE at < min($raw,coalesce((SELECT value FROM state WHERE key='rollup'),$raw));
                DELETE FROM minutes WHERE at < $history;
                DELETE FROM nest_events WHERE at < $history;
                DELETE FROM records WHERE at < $history
                  AND (type <> 'incident' OR (json_extract(json,'$.end') IS NOT NULL AND json_extract(json,'$.end') < $history));
                """, tx, ("$raw", at - settings.RawDays * 86400000L), ("$history", at - settings.HistoryDays * 86400000L));
            trim.ExecuteNonQuery();
            Set(c, "pruned", at, tx);
        }
        tx.Commit();
    }

    public void WriteNest(IEnumerable<NestEvent> events)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var e in events)
        {
            using var cmd = Command(c, "INSERT OR IGNORE INTO nest_events VALUES($device,$id,$at,$received,$kind,$value)",
                tx, ("$device", e.Device), ("$id", e.Id), ("$at", e.At), ("$received", e.Received), ("$kind", e.Kind), ("$value", e.Value));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public NestEvent[] NestHistory(string? device, long to)
    {
        using var c = Open();
        using var cmd = Command(c, """
            SELECT device,id,at,received,kind,value FROM nest_events
            WHERE at<$to AND device=coalesce($device,(SELECT device FROM nest_events ORDER BY received DESC LIMIT 1)) ORDER BY at,id
            """, null, ("$to", to), ("$device", device));
        using var rows = cmd.ExecuteReader();
        var result = new List<NestEvent>();
        while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetInt64(3), rows.GetString(4), rows.GetString(5)));
        return result.ToArray();
    }

    public string? NestLatest(string device, bool connectivity)
    {
        using var c = Open();
        var kinds = connectivity ? "('connectivity')" : "('hvac','snapshot')";
        using var cmd = Command(c, "SELECT value FROM nest_events WHERE device=$device AND kind IN " + kinds +
            " ORDER BY at DESC, kind='hvac' DESC, id DESC LIMIT 1", null, ("$device", device));
        return cmd.ExecuteScalar() as string;
    }

    public void Backup(string destination)
    {
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }
}
