using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Plugins.Mixxx;
using Noctis.Services;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Mixxx plugin: key and BPM conversion, path matching, where Mixxx keeps its library,
/// reading a mixxxdb.sqlite built the way Mixxx's own schema migrations build it (read-only,
/// also while "Mixxx" holds a write lock), and the whole import through the real plugin
/// loader.
/// </summary>
public class MixxxPluginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-mixxx-" + Guid.NewGuid().ToString("N"));

    public MixxxPluginTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Key and BPM ──

    [Theory]
    [InlineData(1, "C major", "8B")]
    [InlineData(2, "C# major", "3B")]   // Mixxx D_FLAT_MAJOR
    [InlineData(7, "F# major", "2B")]
    [InlineData(12, "B major", "1B")]
    [InlineData(13, "C minor", "5A")]
    [InlineData(16, "D# minor", "2A")]  // Mixxx E_FLAT_MINOR
    [InlineData(22, "A minor", "8A")]
    [InlineData(23, "A# minor", "3A")]  // Mixxx B_FLAT_MINOR
    [InlineData(24, "B minor", "10A")]
    public void KeyName_UsesNoctisNotation_ThatAutoMixReads(int keyId, string expected, string camelot)
    {
        Assert.Equal(expected, MixxxImport.KeyName(keyId));
        var parsed = AutoMixKeyTempo.NormalizeCamelotKey(expected);
        Assert.Equal(camelot, $"{parsed!.Value.Number}{parsed.Value.Mode}");
    }

    [Fact]
    public void KeyName_CoversAll24Keys_Once_AndNothingElse()
    {
        var names = Enumerable.Range(1, 24).Select(MixxxImport.KeyName).ToList();
        Assert.All(names, n => Assert.NotNull(AutoMixKeyTempo.NormalizeCamelotKey(n)));
        Assert.Equal(24, names.Select(n => AutoMixKeyTempo.NormalizeCamelotKey(n)).Distinct().Count());
        Assert.Null(MixxxImport.KeyName(0));   // INVALID
        Assert.Null(MixxxImport.KeyName(25));
        Assert.Null(MixxxImport.KeyName(-1));
    }

    [Theory]
    [InlineData(127.98, 128)]
    [InlineData(128.4, 128)]
    [InlineData(174.0, 174)]
    [InlineData(126.5, 126)] // Math.Round, like Noctis's BpmDetector
    [InlineData(0.0, 0)]
    [InlineData(0.4, 0)]
    [InlineData(-120.0, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(1000.0, 0)]
    public void Bpm_RoundsLikeNoctis(double mixxx, int expected) => Assert.Equal(expected, MixxxImport.Bpm(mixxx));

    // ── Paths ──

    [Fact]
    public void NormalizePath_WindowsSeparatorsAndPrefix_AndNfc()
    {
        Assert.Equal("C:/Music/A.mp3", MixxxImport.NormalizePath(@"C:\Music\A.mp3", windows: true));
        Assert.Equal("C:/Music/A.mp3", MixxxImport.NormalizePath(@"\\?\C:\Music\A.mp3", windows: true));
        Assert.Equal("//nas/share/A.mp3", MixxxImport.NormalizePath(@"\\nas\share\A.mp3", windows: true));
        // On Linux and macOS a backslash is part of a file name.
        Assert.Equal(@"/music/a\b.mp3", MixxxImport.NormalizePath(@"/music/a\b.mp3", windows: false));
        // "é" decomposed (macOS HFS+ style) equals "é" composed.
        Assert.Equal(MixxxImport.NormalizePath("/m/Caf\u00e9.mp3", false), MixxxImport.NormalizePath("/m/Cafe\u0301.mp3", false));
        Assert.Equal("", MixxxImport.NormalizePath(null, true));
        Assert.Equal("/m/a\uD800b.mp3", MixxxImport.NormalizePath("/m/a\uD800b.mp3", false)); // not valid Unicode: kept as is
    }

    private static TrackInfo Info(string path, int bpm = 0, string key = "") =>
        new(Guid.NewGuid().ToString("D"), "t", "a", "al", "aa", TimeSpan.Zero, 0, "", 0, path, false, 0, 0) { Bpm = bpm, MusicalKey = key };

    [Fact]
    public void Plan_MatchesByPath_FillsOnlyEmpty_AndCounts()
    {
        var empty = Info(@"C:\Music\One.mp3");
        var full = Info(@"C:\Music\Two.mp3", 100, "C major");
        var bpmOnly = Info(@"C:\Music\Three.mp3", 90);
        var missing = Info(@"C:\Music\Four.mp3");
        var stream = Info("https://example.com/stream.mp3");
        var mixxx = new[]
        {
            new MixxxTrack("c:/music/one.MP3", 127.9, 22),  // other case: same file on Windows
            new MixxxTrack("C:/Music/Two.mp3", 128, 1),
            new MixxxTrack("C:/Music/Three.mp3", 140, 13),
            new MixxxTrack("C:/Music/Unanalysed.mp3", 0, 0),
        };

        var plan = MixxxImport.Plan(new[] { empty, full, bpmOnly, missing, stream }, mixxx, overwrite: false, windows: true, ignoreCase: true);

        Assert.Equal(3, plan.Matched);
        Assert.Equal(1, plan.AlreadySet);
        Assert.Equal(1, plan.NotInMixxx);
        Assert.Equal(new[]
        {
            new TrackAnalysisUpdate(empty.Id, 128, "A minor"),
            new TrackAnalysisUpdate(bpmOnly.Id, null, "C minor"),
        }, plan.Updates);
    }

    [Fact]
    public void Plan_Overwrite_ReplacesDifferentValues()
    {
        var full = Info("/music/two.mp3", 100, "C major");
        var same = Info("/music/same.mp3", 128, "A minor");
        var plan = MixxxImport.Plan(new[] { full, same },
            new[] { new MixxxTrack("/music/two.mp3", 128, 22), new MixxxTrack("/music/same.mp3", 128, 22) },
            overwrite: true, windows: false, ignoreCase: false);
        Assert.Equal(new[] { new TrackAnalysisUpdate(full.Id, 128, "A minor") }, plan.Updates);
        Assert.Equal(1, plan.AlreadySet);
    }

    [Fact]
    public void Plan_IsCaseSensitive_WhenTheFileSystemIs()
    {
        var plan = MixxxImport.Plan(new[] { Info("/music/One.mp3") }, new[] { new MixxxTrack("/music/one.mp3", 128, 22) },
            overwrite: false, windows: false, ignoreCase: false);
        Assert.Empty(plan.Updates);
        Assert.Equal(1, plan.NotInMixxx);
    }

    [Fact]
    public void Plan_ConflictingRowsForOnePath_ImportNothing()
    {
        var t = Info(@"C:\Music\One.mp3");
        var plan = MixxxImport.Plan(new[] { t },
            new[] { new MixxxTrack("C:/Music/One.mp3", 128, 22), new MixxxTrack("c:/music/one.mp3", 64, 22) },
            overwrite: false, windows: true, ignoreCase: true);
        Assert.Empty(plan.Updates);
        Assert.Equal(1, plan.NotInMixxx);

        // The same values twice are not a conflict.
        plan = MixxxImport.Plan(new[] { t },
            new[] { new MixxxTrack("C:/Music/One.mp3", 128, 22), new MixxxTrack("c:/music/one.mp3", 128.2, 22) },
            overwrite: false, windows: true, ignoreCase: true);
        Assert.Single(plan.Updates);
    }

    [Fact]
    public void Summary_SaysWhatHappened()
    {
        var plan = new ImportPlan(new[] { new TrackAnalysisUpdate("x", 1) }, Matched: 3, AlreadySet: 2, NotInMixxx: 1200);
        Assert.Equal($"BPM/key from Mixxx set on 1 track · 2 already had them · {1200:N0} not in Mixxx.", MixxxImport.Summary(1, plan));
        Assert.StartsWith("None of your", MixxxImport.Summary(0, new ImportPlan(Array.Empty<TrackAnalysisUpdate>(), 0, 0, 5)));
    }

    // ── Where the library is ──

    [Fact]
    public void CandidatePaths_FollowTheMixxxManual()
    {
        var home = Path.Combine("H", "me");
        var local = Path.Combine("H", "me", "AppData", "Local");
        Assert.Equal(new[] { Path.Combine(local, "Mixxx", "mixxxdb.sqlite") },
            MixxxDatabase.CandidatePaths("windows", home, local));
        Assert.Equal(new[]
        {
            Path.Combine(home, "Library", "Containers", "org.mixxx.mixxx", "Data", "Library", "Application Support", "Mixxx", "mixxxdb.sqlite"),
            Path.Combine(home, "Library", "Application Support", "Mixxx", "mixxxdb.sqlite"),
        }, MixxxDatabase.CandidatePaths("macos", home, local));
        Assert.Equal(new[]
        {
            Path.Combine(home, ".mixxx", "mixxxdb.sqlite"),
            Path.Combine(home, ".var", "app", "org.mixxx.Mixxx", ".mixxx", "mixxxdb.sqlite"),
        }, MixxxDatabase.CandidatePaths("linux", home, local));
    }

    [Fact]
    public void ResolvePath_DefaultQuotesHomeAndFolders()
    {
        Assert.Equal("default", MixxxDatabase.ResolvePath("  ", () => "default", _dir));
        Assert.Equal("default", MixxxDatabase.ResolvePath(null, () => "default", _dir));
        var file = Path.Combine(_dir, "x.sqlite");
        Assert.Equal(file, MixxxDatabase.ResolvePath($" \"{file}\" ", () => "default", _dir));
        Assert.Equal(Path.Combine(_dir, "mixxxdb.sqlite"), MixxxDatabase.ResolvePath(_dir, () => "default", _dir));
        Assert.Equal(Path.Combine(_dir, ".mixxx", "mixxxdb.sqlite"), MixxxDatabase.ResolvePath("~/.mixxx/mixxxdb.sqlite", () => "default", _dir));
    }

    // ── Reading mixxxdb.sqlite ──

    [Fact]
    public void Read_ReturnsListedTracks_WithBpmAndKeyId()
    {
        var db = MixxxFixture.Create(Path.Combine(_dir, "mixxxdb.sqlite"),
            ("/music/a.mp3", 127.98, 22, "Am", 0, 0),
            ("/music/b.flac", 0, 0, "", 0, 0),           // not analysed
            ("/music/hidden.mp3", 120, 1, "C", 1, 0),     // removed from Mixxx's library
            ("/music/gone.mp3", 120, 1, "C", 0, 1),       // file missing for Mixxx
            ("/music/c.mp3", null, 13, "Cm", 0, 0));      // NULL bpm

        var rows = MixxxDatabase.Read(db);

        Assert.Equal(new[]
        {
            new MixxxTrack("/music/a.mp3", 127.98, 22),
            new MixxxTrack("/music/b.flac", 0, 0),
            new MixxxTrack("/music/c.mp3", 0, 13),
        }, rows.OrderBy(r => r.Location, StringComparer.Ordinal));
    }

    [Fact]
    public void Read_OldSchemaWithoutKeyId_StillGivesBpm()
    {
        var db = MixxxFixture.Create(Path.Combine(_dir, "old.sqlite"), schemaRevision: 18, ("/music/a.mp3", 128.0, 22, "Am", 0, 0));
        Assert.Equal(new MixxxTrack("/music/a.mp3", 128, 0), Assert.Single(MixxxDatabase.Read(db)));
    }

    [Fact]
    public void Read_NeverWritesTheFile()
    {
        var db = MixxxFixture.Create(Path.Combine(_dir, "mixxxdb.sqlite"), ("/music/a.mp3", 128.0, 22, "Am", 0, 0));
        var before = SHA256.HashData(File.ReadAllBytes(db));
        var stamp = File.GetLastWriteTimeUtc(db);

        MixxxDatabase.Read(db);
        MixxxDatabase.Read(db);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(db)));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(db));
        Assert.Equal(new[] { "mixxxdb.sqlite" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Read_WorksWhileMixxxHoldsAWriteTransaction_AndSeesCommittedData()
    {
        var db = MixxxFixture.Create(Path.Combine(_dir, "mixxxdb.sqlite"), ("/music/a.mp3", 128.0, 22, "Am", 0, 0));
        using var mixxx = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        mixxx.Open();
        using var tx = mixxx.BeginTransaction();
        using (var update = mixxx.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE library SET bpm = 90";
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        var row = Assert.Single(MixxxDatabase.Read(db));
        Assert.Equal(128, row.Bpm);
        tx.Rollback();
    }

    [Fact]
    public void Read_RefusesWhatIsNotAMixxxLibrary()
    {
        var other = Path.Combine(_dir, "other.sqlite");
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = other, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE library (id INTEGER PRIMARY KEY, title TEXT)";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => MixxxDatabase.Read(other));

        var junk = Path.Combine(_dir, "junk.sqlite");
        File.WriteAllBytes(junk, Enumerable.Repeat((byte)7, 4096).ToArray());
        Assert.Throws<SqliteException>(() => MixxxDatabase.Read(junk));
        Assert.Throws<FileNotFoundException>(() => MixxxDatabase.Read(Path.Combine(_dir, "none.sqlite")));
    }

    // ── The packaged plugin ──

    [Fact]
    public void Manifest_TargetsApi12_WithMinimalPermissions()
    {
        var manifest = PluginManifest.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.Mixxx/plugin.json")));
        Assert.Equal(PluginApi.Version, manifest.ApiVersion);
        Assert.Equal(new[] { "library.read", "library.write.analysis", "menu.commands", "notifications" }, manifest.Permissions);
        Assert.Empty(manifest.Warnings);
        Assert.Equal(new[] { "databasePath", "overwrite" }, manifest.Settings.Select(s => s.Key));
        Assert.Equal(new MixxxPlugin().Info.Id, manifest.Id);
        Assert.NotNull(manifest.CheckCompatibility("1.5.8")); // the last release without API 1.2
        Assert.Null(manifest.CheckCompatibility("1.5.9"));
    }

    [AvaloniaFact]
    public async Task Import_ThroughTheRealLoader_FillsBpmAndKey_AndSaysSo()
    {
        var music = Path.Combine(_dir, "Music");
        var one = Path.Combine(music, "One.mp3");
        var two = Path.Combine(music, "Two.mp3");
        var three = Path.Combine(music, "Three.mp3");
        var db = MixxxFixture.Create(Path.Combine(_dir, "mixxx", "mixxxdb.sqlite"),
            (Qt(one), 127.98, 22, "Am", 0, 0),
            (Qt(two), 100.0, 1, "C", 0, 0));

        using var box = new PluginSandbox();
        var folder = box.WriteFolder("dev.noctis.plugins.mixxx", File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.Mixxx/plugin.json")));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Noctis.Plugins.Mixxx.dll"), Path.Combine(folder, "Noctis.Plugins.Mixxx.dll"));
        box.Approve(("dev.noctis.plugins.mixxx", new[] { "library.read", "library.write.analysis", "menu.commands", "notifications" }));
        var library = new FakeLibraryService();
        var a = new Track { Title = "One", FilePath = one };
        var b = new Track { Title = "Two", FilePath = two, Bpm = 99, MusicalKey = "G major" };
        var c = new Track { Title = "Three", FilePath = three };
        library.TrackList.AddRange(new[] { a, b, c });
        var host = box.NewHost(library: library, appVersion: "1.5.9");
        var notices = new List<PluginNotice>();
        host.NotificationRequested += (_, n) => notices.Add(n);
        host.LoadAll();

        var plugin = host.Plugins.Single();
        Assert.Equal(PluginStatus.Running, plugin.Status);
        plugin.SettingItems.Single(s => s.Key == "databasePath").TextValue = Path.GetDirectoryName(db)!; // a folder works
        var command = host.TrackCommands.Single();
        Assert.Equal(MixxxPlugin.CommandLabel, command.Label);

        command.Execute(c);
        for (var i = 0; i < 400 && notices.Count == 0; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(PluginStatus.Running, plugin.Status);
        Assert.Equal((128, "A minor"), (a.Bpm, a.MusicalKey));
        Assert.Equal((99, "G major"), (b.Bpm, b.MusicalKey)); // fill-only by default
        Assert.Equal((0, ""), (c.Bpm, c.MusicalKey));
        Assert.Equal(1, library.SaveCount);
        Assert.Equal("BPM/key from Mixxx set on 1 track · 1 already had it · 1 not in Mixxx.", Assert.Single(notices).Message);
        Assert.True(host.Remove(plugin, deleteData: true)); // loaded from memory: nothing locked
    }

    [AvaloniaFact]
    public async Task Import_WithoutAMixxxLibrary_SaysWhereItLooked()
    {
        using var box = new PluginSandbox();
        box.Approve(("dev.noctis.plugins.mixxx", new[] { "library.read", "library.write.analysis", "menu.commands", "notifications" }));
        var manifest = File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.Mixxx/plugin.json"));
        var host = box.NewHost(library: new FakeLibraryService(), appVersion: "1.5.9");
        var instance = new MixxxPlugin();
        var plugin = host.AddInProcess(box.WriteFolder("dev.noctis.plugins.mixxx", manifest), PluginManifest.Parse(manifest), () => instance);
        var missing = Path.Combine(_dir, "nowhere", "mixxxdb.sqlite");
        plugin.SettingItems.Single(s => s.Key == "databasePath").TextValue = missing;

        var message = await instance.ImportAsync();

        Assert.Equal($"Mixxx library not found at {missing}. Set where it is in Settings → Plugins → Mixxx.", message);
        Assert.Equal(PluginStatus.Running, plugin.Status);
    }

    /// <summary>Mixxx stores Qt paths: '/' separators on every OS.</summary>
    private static string Qt(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
}

/// <summary>
/// Builds a mixxxdb.sqlite the way Mixxx's res/schema.xml migrations do (revision 1 base tables,
/// the revision 3 library rebuild, then each ALTER TABLE up to revision 40), restricted to the
/// tables the plugin reads.
/// </summary>
internal static class MixxxFixture
{
    private static readonly (int Revision, string Sql)[] Migrations =
    {
        (1, """
            CREATE TABLE IF NOT EXISTS settings (name TEXT UNIQUE NOT NULL, value TEXT, locked INTEGER DEFAULT 0, hidden INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS track_locations (
              id INTEGER PRIMARY KEY AUTOINCREMENT, location varchar(512) UNIQUE, filename varchar(512),
              directory varchar(512), filesize INTEGER, fs_deleted INTEGER, needs_verification INTEGER);
            """),
        (3, """
            CREATE TABLE IF NOT EXISTS library (
              id INTEGER PRIMARY KEY AUTOINCREMENT, artist varchar(64), title varchar(64), album varchar(64),
              year varchar(16), genre varchar(64), tracknumber varchar(3),
              location INTEGER REFERENCES track_locations(location), comment varchar(256), url varchar(256),
              duration FLOAT, bitrate INTEGER, samplerate INTEGER, cuepoint INTEGER, bpm FLOAT,
              wavesummaryhex BLOB, channels INTEGER, datetime_added DEFAULT CURRENT_TIMESTAMP,
              mixxx_deleted INTEGER, played INTEGER, header_parsed INTEGER DEFAULT 0);
            """),
        (4, "ALTER TABLE library ADD COLUMN filetype varchar(8) DEFAULT '?';"),
        (6, "ALTER TABLE library ADD COLUMN replaygain FLOAT DEFAULT 0;"),
        (7, """
            ALTER TABLE library ADD COLUMN timesplayed INTEGER DEFAULT 0;
            ALTER TABLE library ADD COLUMN rating INTEGER DEFAULT 0;
            ALTER TABLE library ADD COLUMN key varchar(8) DEFAULT '';
            """),
        (12, "ALTER TABLE Library ADD COLUMN beats BLOB; ALTER TABLE Library ADD COLUMN beats_version TEXT;"),
        (14, "ALTER TABLE library ADD COLUMN composer varchar(64) DEFAULT '';"),
        (17, "ALTER TABLE Library ADD COLUMN bpm_lock INTEGER DEFAULT 0; ALTER TABLE Library ADD COLUMN beats_sub_version TEXT DEFAULT '';"),
        (18, "ALTER TABLE Library ADD COLUMN keys BLOB; ALTER TABLE Library ADD COLUMN keys_version TEXT; ALTER TABLE Library ADD COLUMN keys_sub_version TEXT;"),
        (19, "ALTER TABLE Library ADD COLUMN key_id INTEGER DEFAULT 0;"),
        (21, "ALTER TABLE Library ADD COLUMN grouping TEXT DEFAULT ''; ALTER TABLE Library ADD COLUMN album_artist TEXT DEFAULT '';"),
        (24, """
            ALTER TABLE library ADD COLUMN coverart_source INTEGER DEFAULT 0;
            ALTER TABLE library ADD COLUMN coverart_type INTEGER DEFAULT 0;
            ALTER TABLE library ADD COLUMN coverart_location TEXT DEFAULT '';
            ALTER TABLE library ADD COLUMN coverart_hash INTEGER DEFAULT 0;
            """),
        (25, "ALTER TABLE library ADD COLUMN replaygain_peak REAL DEFAULT -1.0;"),
        (26, "ALTER TABLE library ADD COLUMN tracktotal TEXT DEFAULT '//';"),
        (31, "ALTER TABLE library ADD COLUMN color INTEGER;"),
        (33, "ALTER TABLE library ADD COLUMN coverart_color INTEGER; ALTER TABLE library ADD COLUMN coverart_digest BLOB;"),
        (35, "ALTER TABLE library ADD COLUMN last_played_at DATETIME DEFAULT NULL;"),
        (37, "ALTER TABLE library ADD COLUMN source_synchronized_ms INTEGER DEFAULT NULL;"),
        (40, "ALTER TABLE library ADD COLUMN tuning_frequency_hz FLOAT DEFAULT 0.0;"),
    };

    /// <summary>Rows: (location, bpm or null, key_id, key text, mixxx_deleted, fs_deleted).</summary>
    public static string Create(string path, params (string Location, double? Bpm, int KeyId, string Key, int Deleted, int FsDeleted)[] rows)
        => Create(path, 40, rows);

    public static string Create(string path, int schemaRevision, params (string Location, double? Bpm, int KeyId, string Key, int Deleted, int FsDeleted)[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Foreign keys off, as in Mixxx (Qt's SQLite driver leaves them off): library.location
        // "REFERENCES track_locations(location)" but holds track_locations.id.
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = false }.ToString());
        c.Open();
        foreach (var (revision, sql) in Migrations.Where(m => m.Revision <= schemaRevision))
            Exec(c, sql);
        Exec(c, $"INSERT INTO settings (name, value) VALUES ('mixxx.schema.version', '{schemaRevision}')");
        foreach (var row in rows)
        {
            using var loc = c.CreateCommand();
            loc.CommandText = "INSERT INTO track_locations (location, filename, directory, filesize, fs_deleted, needs_verification) " +
                              "VALUES ($l, $f, $d, 1234, $fs, 0); SELECT last_insert_rowid();";
            loc.Parameters.AddWithValue("$l", row.Location);
            loc.Parameters.AddWithValue("$f", row.Location[(row.Location.LastIndexOf('/') + 1)..]);
            loc.Parameters.AddWithValue("$d", row.Location[..Math.Max(0, row.Location.LastIndexOf('/'))]);
            loc.Parameters.AddWithValue("$fs", row.FsDeleted);
            var locationId = (long)loc.ExecuteScalar()!;

            using var lib = c.CreateCommand();
            var keyColumns = schemaRevision >= 19 ? ", key_id" : "";
            var keyValues = schemaRevision >= 19 ? ", $kid" : "";
            lib.CommandText = $"INSERT INTO library (artist, title, location, bpm, key, mixxx_deleted, played{keyColumns}) VALUES ('a', 't', $loc, $bpm, $key, $del, 0{keyValues})";
            lib.Parameters.AddWithValue("$loc", locationId);
            lib.Parameters.AddWithValue("$bpm", (object?)row.Bpm ?? DBNull.Value);
            lib.Parameters.AddWithValue("$key", row.Key);
            lib.Parameters.AddWithValue("$del", row.Deleted);
            if (schemaRevision >= 19) lib.Parameters.AddWithValue("$kid", row.KeyId);
            lib.ExecuteNonQuery();
        }
        return path;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
