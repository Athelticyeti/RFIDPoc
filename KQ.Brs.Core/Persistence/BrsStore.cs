using System.Globalization;
using System.Text.Json;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace KQ.Brs.Core.Persistence;

/// <summary>Everything needed to rebuild the engine's state on startup.</summary>
public sealed record StoredState(
    List<Bag> Bags, Dictionary<string, string> Bindings, List<BagEvent> Events, List<BagException> Exceptions,
    List<TypeBMessage> Messages, int StrayReads);

/// <summary>
/// SQLite store (spec 5, POC: SQLite instead of PostgreSQL). bag_events is append-only, enforced by triggers.
/// Bag static data is stored; status is rebuilt by replaying events.
/// </summary>
public sealed class BrsStore
{
    public BrsStore(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        Exec(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF;");
        return connection;
    }

    public void EnsureSchema()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS bags (
                plate TEXT PRIMARY KEY, flight TEXT NOT NULL, destination TEXT NOT NULL, surname TEXT NOT NULL,
                initial TEXT NOT NULL, class INTEGER NOT NULL, tag_type INTEGER NOT NULL, weight_kg INTEGER NOT NULL,
                source INTEGER NOT NULL, authority INTEGER NOT NULL, pax_status INTEGER NOT NULL, deleted INTEGER NOT NULL,
                original_plate TEXT);
            CREATE TABLE IF NOT EXISTS bindings (epc TEXT PRIMARY KEY, plate TEXT NOT NULL, bound_utc TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_bindings_plate ON bindings(plate);
            CREATE TABLE IF NOT EXISTS bag_events (
                id TEXT PRIMARY KEY, plate TEXT NOT NULL, flight TEXT NOT NULL, kind TEXT NOT NULL, occurred_utc TEXT NOT NULL,
                scan_point TEXT, message_type INTEGER, reader_id TEXT, antenna INTEGER, rssi REAL, outcome INTEGER,
                load_authorised INTEGER, uld TEXT, handler TEXT, new_status INTEGER, raw_message TEXT, note TEXT, scan_key TEXT);
            CREATE INDEX IF NOT EXISTS ix_events_plate_time ON bag_events(plate, occurred_utc);
            CREATE TRIGGER IF NOT EXISTS bag_events_no_update BEFORE UPDATE ON bag_events
                BEGIN SELECT RAISE(ABORT, 'bag_events is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS bag_events_no_delete BEFORE DELETE ON bag_events
                BEGIN SELECT RAISE(ABORT, 'bag_events is append-only'); END;
            CREATE TABLE IF NOT EXISTS exceptions (
                id TEXT PRIMARY KEY, plate TEXT NOT NULL, epc TEXT, type INTEGER NOT NULL, severity INTEGER NOT NULL,
                state INTEGER NOT NULL, message TEXT NOT NULL, source INTEGER NOT NULL, scan_point TEXT,
                raised_utc TEXT NOT NULL, ack_utc TEXT, resolved_utc TEXT, resolved_by TEXT, note TEXT, overridden INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_exceptions_state ON exceptions(state);
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY, direction INTEGER NOT NULL, type INTEGER NOT NULL, plate TEXT, raw TEXT NOT NULL,
                created_utc TEXT NOT NULL, sent_utc TEXT);
            CREATE TABLE IF NOT EXISTS scan_windows (
                id INTEGER PRIMARY KEY AUTOINCREMENT, reader_id TEXT NOT NULL, epc TEXT NOT NULL, scan_point TEXT NOT NULL,
                start_utc TEXT NOT NULL, end_utc TEXT NOT NULL, reads INTEGER NOT NULL, best_antenna INTEGER NOT NULL,
                max_rssi REAL, reads_per_antenna TEXT NOT NULL, scan_key TEXT);
            CREATE TABLE IF NOT EXISTS reader_log (id INTEGER PRIMARY KEY AUTOINCREMENT, utc TEXT NOT NULL, status TEXT NOT NULL, reason TEXT);
            CREATE TABLE IF NOT EXISTS counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
            """);
        // Databases created before scans were linked to their windows.
        AddColumnIfMissing(c, "bag_events", "scan_key", "TEXT");
        AddColumnIfMissing(c, "scan_windows", "scan_key", "TEXT");
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_scan_windows_key ON scan_windows(scan_key);");
    }

    private static void AddColumnIfMissing(SqliteConnection c, string table, string column, string type)
    {
        var exists = false;
        Query(c, $"SELECT name FROM pragma_table_info('{table}')", r => exists |= r.GetString(0) == column);
        if (!exists) Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {type};");
    }

    /// <summary>Deletes all demo data. Tag bindings can be kept so the same physical tags map to the same bags.</summary>
    public void Reset(bool keepBindings)
    {
        Dictionary<string, (string Plate, string Bound)> bindings = new();
        if (keepBindings && File.Exists(Path))
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT epc, plate, bound_utc FROM bindings";
            using var r = cmd.ExecuteReader();
            while (r.Read()) bindings[r.GetString(0)] = (r.GetString(1), r.GetString(2));
        }

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(Path + suffix)) File.Delete(Path + suffix);
        }
        EnsureSchema();

        if (bindings.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var (epc, (plate, bound)) in bindings)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO bindings (epc, plate, bound_utc) VALUES ($e, $p, $b)";
            cmd.Parameters.AddWithValue("$e", epc);
            cmd.Parameters.AddWithValue("$p", plate);
            cmd.Parameters.AddWithValue("$b", bound);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public StoredState Load()
    {
        using var c = Open();
        var bags = new List<Bag>();
        Query(c, "SELECT plate, flight, destination, surname, initial, class, tag_type, weight_kg, source, authority, pax_status, deleted, original_plate FROM bags", r =>
            bags.Add(new Bag
            {
                Plate = new LicencePlate(r.GetString(0)), Flight = r.GetString(1), Destination = r.GetString(2),
                Surname = r.GetString(3), Initial = r.GetString(4), Class = (CabinClass)r.GetInt32(5),
                TagType = (TagType)r.GetInt32(6), WeightKg = r.GetInt32(7), Source = (BagSource)r.GetInt32(8),
                AuthorityToLoad = r.GetInt32(9) != 0, PassengerStatus = (PassengerStatus)r.GetInt32(10),
                Deleted = r.GetInt32(11) != 0, OriginalPlate = r.IsDBNull(12) ? null : new LicencePlate(r.GetString(12)),
            }));

        var bindings = new Dictionary<string, string>();
        Query(c, "SELECT epc, plate FROM bindings", r => bindings[r.GetString(0)] = r.GetString(1));

        var events = new List<BagEvent>();
        // A scan's event holds its first read; its closed window (same scan_key) holds the best antenna and strongest RSSI.
        Query(c, """
            SELECT e.id, e.plate, e.flight, e.kind, e.occurred_utc, e.scan_point, e.uld, e.new_status, e.handler, e.note,
                   e.reader_id, COALESCE(w.best_antenna, e.antenna), COALESCE(w.max_rssi, e.rssi), e.scan_key
            FROM bag_events e
            LEFT JOIN scan_windows w ON w.scan_key = e.scan_key AND w.scan_point = e.scan_point
            ORDER BY e.rowid
            """, r =>
            events.Add(new BagEvent
            {
                Id = r.GetString(0), Plate = r.GetString(1), Flight = r.GetString(2), Kind = r.GetString(3),
                OccurredUtc = ParseTime(r.GetString(4)), ScanPoint = Str(r, 5), Uld = Str(r, 6),
                NewStatus = r.IsDBNull(7) ? null : (BagStatus)r.GetInt32(7), Handler = Str(r, 8), Note = Str(r, 9),
                ReaderId = Str(r, 10), Antenna = r.IsDBNull(11) ? null : r.GetInt32(11), Rssi = r.IsDBNull(12) ? null : r.GetDouble(12),
                ScanKey = Str(r, 13),
            }));

        var exceptions = new List<BagException>();
        Query(c, "SELECT id, plate, epc, type, severity, state, message, source, scan_point, raised_utc, ack_utc, resolved_utc, resolved_by, note, overridden FROM exceptions", r =>
            exceptions.Add(new BagException
            {
                Id = r.GetString(0), Plate = r.GetString(1), Epc = Str(r, 2), Type = (ExceptionType)r.GetInt32(3),
                Severity = (Severity)r.GetInt32(4), State = (ExceptionState)r.GetInt32(5), Message = r.GetString(6),
                Source = (BagSource)r.GetInt32(7), ScanPoint = Str(r, 8), RaisedUtc = ParseTime(r.GetString(9)),
                AcknowledgedUtc = TimeOrNull(r, 10), ResolvedUtc = TimeOrNull(r, 11), ResolvedBy = Str(r, 12),
                ResolutionNote = Str(r, 13), Overridden = r.GetInt32(14) != 0,
            }));

        var messages = new List<TypeBMessage>();
        Query(c, "SELECT id, direction, type, plate, raw, created_utc, sent_utc FROM messages ORDER BY created_utc DESC LIMIT 2000", r =>
            messages.Add(new TypeBMessage(r.GetString(0), (MessageDirection)r.GetInt32(1), (MessageType)r.GetInt32(2),
                Str(r, 3), r.GetString(4), ParseTime(r.GetString(5)), TimeOrNull(r, 6))));
        messages.Reverse();

        var stray = 0;
        Query(c, "SELECT value FROM counters WHERE name = 'stray'", r => stray = r.GetInt32(0));

        return new StoredState(bags, bindings, events, exceptions, messages, stray);
    }

    // ----- write helpers used by PersistenceWriter (all run inside its transaction) -----

    internal static void UpsertBag(SqliteCommand cmd, Bag b)
    {
        cmd.CommandText = """
            INSERT INTO bags (plate, flight, destination, surname, initial, class, tag_type, weight_kg, source, authority, pax_status, deleted, original_plate)
            VALUES ($plate, $flight, $dest, $surname, $initial, $class, $tag, $weight, $source, $auth, $pax, $deleted, $orig)
            ON CONFLICT(plate) DO UPDATE SET flight=$flight, destination=$dest, surname=$surname, initial=$initial, class=$class,
                tag_type=$tag, weight_kg=$weight, source=$source, authority=$auth, pax_status=$pax, deleted=$deleted, original_plate=$orig
            """;
        cmd.Parameters.AddWithValue("$plate", b.Plate.Value);
        cmd.Parameters.AddWithValue("$flight", b.Flight);
        cmd.Parameters.AddWithValue("$dest", b.Destination);
        cmd.Parameters.AddWithValue("$surname", b.Surname);
        cmd.Parameters.AddWithValue("$initial", b.Initial);
        cmd.Parameters.AddWithValue("$class", (int)b.Class);
        cmd.Parameters.AddWithValue("$tag", (int)b.TagType);
        cmd.Parameters.AddWithValue("$weight", b.WeightKg);
        cmd.Parameters.AddWithValue("$source", (int)b.Source);
        cmd.Parameters.AddWithValue("$auth", b.AuthorityToLoad ? 1 : 0);
        cmd.Parameters.AddWithValue("$pax", (int)b.PassengerStatus);
        cmd.Parameters.AddWithValue("$deleted", b.Deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$orig", (object?)b.OriginalPlate?.Value ?? DBNull.Value);
    }

    internal static void InsertEvent(SqliteCommand cmd, BagEvent e)
    {
        cmd.CommandText = """
            INSERT INTO bag_events (id, plate, flight, kind, occurred_utc, scan_point, message_type, reader_id, antenna, rssi,
                outcome, load_authorised, uld, handler, new_status, raw_message, note, scan_key)
            VALUES ($id, $plate, $flight, $kind, $t, $sp, $mt, $reader, $ant, $rssi, $outcome, $auth, $uld, $handler, $status, $raw, $note, $key)
            """;
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$plate", e.Plate);
        cmd.Parameters.AddWithValue("$flight", e.Flight);
        cmd.Parameters.AddWithValue("$kind", e.Kind);
        cmd.Parameters.AddWithValue("$t", FormatTime(e.OccurredUtc));
        cmd.Parameters.AddWithValue("$sp", Db(e.ScanPoint));
        cmd.Parameters.AddWithValue("$mt", Db(e.MessageType is { } mt ? (int)mt : null));
        cmd.Parameters.AddWithValue("$reader", Db(e.ReaderId));
        cmd.Parameters.AddWithValue("$ant", Db(e.Antenna));
        cmd.Parameters.AddWithValue("$rssi", Db(e.Rssi));
        cmd.Parameters.AddWithValue("$outcome", Db(e.Outcome is { } o ? (int)o : null));
        cmd.Parameters.AddWithValue("$auth", Db(e.LoadAuthorised is { } a ? (a ? 1 : 0) : null));
        cmd.Parameters.AddWithValue("$uld", Db(e.Uld));
        cmd.Parameters.AddWithValue("$handler", Db(e.Handler));
        cmd.Parameters.AddWithValue("$status", Db(e.NewStatus is { } s ? (int)s : null));
        cmd.Parameters.AddWithValue("$raw", Db(e.RawMessage));
        cmd.Parameters.AddWithValue("$key", Db(e.ScanKey));
        cmd.Parameters.AddWithValue("$note", Db(e.Note));
    }

    internal static void UpsertException(SqliteCommand cmd, BagException e)
    {
        cmd.CommandText = """
            INSERT INTO exceptions (id, plate, epc, type, severity, state, message, source, scan_point, raised_utc, ack_utc, resolved_utc, resolved_by, note, overridden)
            VALUES ($id, $plate, $epc, $type, $sev, $state, $msg, $source, $sp, $raised, $ack, $resolved, $by, $note, $ovr)
            ON CONFLICT(id) DO UPDATE SET state=$state, message=$msg, ack_utc=$ack, resolved_utc=$resolved, resolved_by=$by, note=$note, overridden=$ovr
            """;
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$plate", e.Plate);
        cmd.Parameters.AddWithValue("$epc", Db(e.Epc));
        cmd.Parameters.AddWithValue("$type", (int)e.Type);
        cmd.Parameters.AddWithValue("$sev", (int)e.Severity);
        cmd.Parameters.AddWithValue("$state", (int)e.State);
        cmd.Parameters.AddWithValue("$msg", e.Message);
        cmd.Parameters.AddWithValue("$source", (int)e.Source);
        cmd.Parameters.AddWithValue("$sp", Db(e.ScanPoint));
        cmd.Parameters.AddWithValue("$raised", FormatTime(e.RaisedUtc));
        cmd.Parameters.AddWithValue("$ack", Db(e.AcknowledgedUtc is { } a ? FormatTime(a) : null));
        cmd.Parameters.AddWithValue("$resolved", Db(e.ResolvedUtc is { } r ? FormatTime(r) : null));
        cmd.Parameters.AddWithValue("$by", Db(e.ResolvedBy));
        cmd.Parameters.AddWithValue("$note", Db(e.ResolutionNote));
        cmd.Parameters.AddWithValue("$ovr", e.Overridden ? 1 : 0);
    }

    internal static void UpsertMessage(SqliteCommand cmd, TypeBMessage m)
    {
        cmd.CommandText = """
            INSERT INTO messages (id, direction, type, plate, raw, created_utc, sent_utc) VALUES ($id, $dir, $type, $plate, $raw, $created, $sent)
            ON CONFLICT(id) DO UPDATE SET sent_utc=$sent
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$dir", (int)m.Direction);
        cmd.Parameters.AddWithValue("$type", (int)m.Type);
        cmd.Parameters.AddWithValue("$plate", Db(m.Plate));
        cmd.Parameters.AddWithValue("$raw", m.Raw);
        cmd.Parameters.AddWithValue("$created", FormatTime(m.CreatedUtc));
        cmd.Parameters.AddWithValue("$sent", Db(m.SentUtc is { } s ? FormatTime(s) : null));
    }

    internal static void UpsertBinding(SqliteCommand cmd, string epc, string plate, DateTimeOffset utc)
    {
        cmd.CommandText = """
            DELETE FROM bindings WHERE plate = $plate AND epc <> $epc;
            INSERT INTO bindings (epc, plate, bound_utc) VALUES ($epc, $plate, $t) ON CONFLICT(epc) DO UPDATE SET plate=$plate, bound_utc=$t
            """;
        cmd.Parameters.AddWithValue("$epc", epc);
        cmd.Parameters.AddWithValue("$plate", plate);
        cmd.Parameters.AddWithValue("$t", FormatTime(utc));
    }

    internal static void InsertScanWindow(SqliteCommand cmd, ScanWindowSummary w)
    {
        cmd.CommandText = """
            INSERT INTO scan_windows (reader_id, epc, scan_point, start_utc, end_utc, reads, best_antenna, max_rssi, reads_per_antenna, scan_key)
            VALUES ($reader, $epc, $sp, $start, $end, $reads, $best, $rssi, $per, $key)
            """;
        cmd.Parameters.AddWithValue("$reader", w.ReaderId);
        cmd.Parameters.AddWithValue("$epc", w.Epc);
        cmd.Parameters.AddWithValue("$sp", w.ScanPointId);
        cmd.Parameters.AddWithValue("$start", FormatTime(w.StartUtc));
        cmd.Parameters.AddWithValue("$end", FormatTime(w.EndUtc));
        cmd.Parameters.AddWithValue("$reads", w.Reads);
        cmd.Parameters.AddWithValue("$best", w.BestAntenna);
        cmd.Parameters.AddWithValue("$rssi", Db(w.MaxRssi));
        cmd.Parameters.AddWithValue("$per", JsonSerializer.Serialize(w.ReadsPerAntenna));
        cmd.Parameters.AddWithValue("$key", w.ScanKey);
    }

    internal static void SetCounter(SqliteCommand cmd, string name, int value)
    {
        cmd.CommandText = "INSERT INTO counters (name, value) VALUES ($n, $v) ON CONFLICT(name) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$v", value);
    }

    internal static void InsertReaderLog(SqliteCommand cmd, DateTimeOffset utc, string status, string? reason)
    {
        cmd.CommandText = "INSERT INTO reader_log (utc, status, reason) VALUES ($t, $s, $r)";
        cmd.Parameters.AddWithValue("$t", FormatTime(utc));
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$r", Db(reason));
    }

    // ----- helpers -----

    public static string FormatTime(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? TimeOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseTime(r.GetString(i));

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static object Db(object? value) => value ?? DBNull.Value;

    internal static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static void Query(SqliteConnection c, string sql, Action<SqliteDataReader> row)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) row(r);
    }
}
