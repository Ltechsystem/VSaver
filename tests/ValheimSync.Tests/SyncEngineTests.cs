using ValheimSync.Core;
using ValheimSync.Core.Models;
using ValheimSync.Core.Sync;
using ValheimSync.Core.Util;
using ValheimSync.Tests.Fakes;
using Xunit;

namespace ValheimSync.Tests;

/// <summary>
/// Exercises the whole sync decision logic through the ICloudStorageProvider seam.
///
/// Notes:
///  - Each test uses a unique world name so the engine's best-effort mirror into the real
///    Valheim LocalLow folder (if this machine has one) can be cleaned up safely in Dispose.
///  - Tests early-return if Valheim is actually running on this machine, because the engine
///    deliberately refuses to transfer while the game is up (static ValheimProcess seam).
/// </summary>
public sealed class SyncEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstests-" + Guid.NewGuid().ToString("N"));
    private readonly string _world = "VSTestW" + Guid.NewGuid().ToString("N")[..8];
    private readonly FakeCloudProvider _cloud = new();
    private readonly AppSettings _settings;
    private readonly SyncEngine _engine;
    private readonly List<SyncStatus> _statuses = new();

    private string DbName => _world + ".db";
    private string FwlName => _world + ".fwl";
    private string CommitName => _world + ".commit";
    private string LocalDb => Path.Combine(_dir, DbName);
    private string LocalFwl => Path.Combine(_dir, FwlName);
    private string SessionStatePath => Path.Combine(_dir, "sessionstate.json");

    public SyncEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new AppSettings { WorldsPathOverride = _dir, PlayerName = "me" };
        _settings.SelectedWorlds.Add(_world);
        _engine = new SyncEngine(_settings, _cloud, null, SessionStatePath);
        _engine.WorldStatusChanged += (_, s) => _statuses.Add(s);
    }

    private static bool GameIsRunning => ValheimProcess.IsRunning();

    private void WriteLocal(string dbContent, string fwlContent)
    {
        File.WriteAllText(LocalDb, dbContent);
        File.WriteAllText(LocalFwl, fwlContent);
    }

    private static string CommitFor(string dbContent, string fwlContent) =>
        CommitMarker.Content(Hashing.Md5Text(dbContent), Hashing.Md5Text(fwlContent));

    /// <summary>Seeds a complete, internally consistent remote world (db + fwl + commit marker).</summary>
    private void SeedRemoteWorld(string dbContent, string fwlContent,
        DateTimeOffset? modified = null, bool withCommit = true)
    {
        _cloud.Seed(DbName, dbContent, modified);
        _cloud.Seed(FwlName, fwlContent, modified);
        if (withCommit) _cloud.Seed(CommitName, CommitFor(dbContent, fwlContent), modified);
    }

    private void AgeLocalFiles()
    {
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(LocalDb, old);
        File.SetLastWriteTimeUtc(LocalFwl, old);
    }

    // ---- download paths --------------------------------------------------

    [Fact]
    public async Task FirstTimeDownload_WhenLocalMissingAndRemoteComplete()
    {
        if (GameIsRunning) return;
        SeedRemoteWorld("dbcontent", "fwlcontent");

        await _engine.SyncNowAsync();

        Assert.Equal("dbcontent", File.ReadAllText(LocalDb));
        Assert.Equal("fwlcontent", File.ReadAllText(LocalFwl));
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task LegacyRemote_WithoutCommitMarker_StillDownloads()
    {
        if (GameIsRunning) return;
        // Remotes uploaded before the marker existed have no .commit — they're trusted.
        SeedRemoteWorld("dbcontent", "fwlcontent", withCommit: false);

        await _engine.SyncNowAsync();

        Assert.Equal("dbcontent", File.ReadAllText(LocalDb));
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task TornRemote_MarkerMismatch_IsNotDownloaded()
    {
        if (GameIsRunning) return;
        // The marker certifies a v1 pair, but the .db in the folder is v2 — i.e. an upload
        // died between the .db and the marker. Must not be treated as downloadable.
        _cloud.Seed(DbName, "v2");
        _cloud.Seed(FwlName, "old-fwl");
        _cloud.Seed(CommitName, CommitFor("v1", "old-fwl"));

        await _engine.SyncNowAsync();

        Assert.False(File.Exists(LocalDb));
        Assert.Equal(SyncStatus.Error, _statuses.Last());
        Assert.DoesNotContain(_cloud.Calls, c => c.StartsWith("download:"));
    }

    [Fact]
    public async Task NoDownload_WhenRemoteFwlMissing()
    {
        if (GameIsRunning) return;
        // Only the .db exists remotely — an interrupted first upload. The .fwl commit
        // marker is absent, so this must NOT be treated as a downloadable world.
        _cloud.Seed(DbName, "halfuploaded");

        await _engine.SyncNowAsync();

        Assert.False(File.Exists(LocalDb));
        Assert.Equal(SyncStatus.Unknown, _statuses.Last());
    }

    [Fact]
    public async Task Download_WhenOtherHoldsLock_AndKeepsSynbak()
    {
        if (GameIsRunning) return;
        WriteLocal("localdb", "localfwl");
        AgeLocalFiles();
        SeedRemoteWorld("remotedb", "remotefwl");
        _cloud.Locks[_world] = new WorldLock("Bob", DateTimeOffset.UtcNow);

        await _engine.SyncNowAsync();

        Assert.Equal("remotedb", File.ReadAllText(LocalDb));
        Assert.Equal("remotefwl", File.ReadAllText(LocalFwl));
        // The replaced local save must survive as a .synbak safety copy.
        Assert.Equal("localdb", File.ReadAllText(LocalDb + ".synbak"));
        Assert.Equal("localfwl", File.ReadAllText(LocalFwl + ".synbak"));
    }

    [Fact]
    public async Task Download_WhenRemoteNewer_AndNoOneHoldsLock()
    {
        if (GameIsRunning) return;
        WriteLocal("localdb", "localfwl");
        AgeLocalFiles();
        SeedRemoteWorld("remotedb", "remotefwl");

        await _engine.SyncNowAsync();

        Assert.Equal("remotedb", File.ReadAllText(LocalDb));
    }

    [Fact]
    public async Task TornRemote_BlocksDivergenceDownload_Too()
    {
        if (GameIsRunning) return;
        WriteLocal("localdb", "localfwl");
        AgeLocalFiles();
        // Remote is newer but its marker certifies a different .db — refuse to download.
        _cloud.Seed(DbName, "remotedb");
        _cloud.Seed(FwlName, "remotefwl");
        _cloud.Seed(CommitName, CommitFor("some-other-db", "remotefwl"));

        await _engine.SyncNowAsync();

        Assert.Equal("localdb", File.ReadAllText(LocalDb)); // untouched
        Assert.Equal(SyncStatus.Error, _statuses.Last());
    }

    [Fact]
    public async Task CorruptDownload_Aborts_LeavesLocalUntouched()
    {
        if (GameIsRunning) return;
        SeedRemoteWorld("realcontent", "realfwl");
        _cloud.CorruptDownloads = true;

        await _engine.SyncNowAsync();

        // Hash verification must reject the corrupt download before touching the worlds dir.
        Assert.Equal(SyncStatus.Error, _statuses.Last());
        Assert.False(File.Exists(LocalDb));
        Assert.False(File.Exists(LocalFwl));
    }

    // ---- upload paths ----------------------------------------------------

    [Fact]
    public async Task Upload_WhenRemoteMissing_DbThenFwlThenCommit_NoBackup()
    {
        if (GameIsRunning) return;
        WriteLocal("mydb", "myfwl");

        await _engine.SyncNowAsync();

        Assert.Equal("mydb", _cloud.ContentOf(DbName));
        Assert.Equal("myfwl", _cloud.ContentOf(FwlName));
        // The marker goes up LAST and must certify exactly the pair just uploaded.
        Assert.Equal(CommitFor("mydb", "myfwl"), _cloud.ContentOf(CommitName));
        Assert.Equal(new[] { $"upload:{DbName}", $"upload:{FwlName}", $"upload:{CommitName}" },
            _cloud.Calls);
        Assert.False(_cloud.Files.ContainsKey(DbName + ".bak"));
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task Upload_WithExistingRemote_BacksUpBeforeOverwriting()
    {
        if (GameIsRunning) return;
        WriteLocal("newdb", "newfwl");
        SeedRemoteWorld("olddb", "oldfwl", DateTimeOffset.UtcNow.AddDays(-1));

        await _engine.SyncNowAsync();

        Assert.Equal("newdb", _cloud.ContentOf(DbName));
        Assert.Equal("olddb", _cloud.ContentOf(DbName + ".bak"));
        Assert.Equal("oldfwl", _cloud.ContentOf(FwlName + ".bak"));
        Assert.Equal(CommitFor("olddb", "oldfwl"), _cloud.ContentOf(CommitName + ".bak"));
        // Backup strictly precedes the overwrite; .db → .fwl → commit marker.
        Assert.Equal(new[]
        {
            $"copy:{DbName}->{DbName}.bak",
            $"copy:{FwlName}->{FwlName}.bak",
            $"copy:{CommitName}->{CommitName}.bak",
            $"upload:{DbName}",
            $"upload:{FwlName}",
            $"upload:{CommitName}",
        }, _cloud.Calls);
    }

    [Fact]
    public async Task Backup_OnlyOncePerSession()
    {
        if (GameIsRunning) return;
        WriteLocal("v2", "fwl2");
        SeedRemoteWorld("v1", "fwl1", DateTimeOffset.UtcNow.AddDays(-1));

        await _engine.SyncNowAsync();          // first upload of the session → backs up v1
        File.WriteAllText(LocalDb, "v3");      // played some more…
        await _engine.SyncNowAsync();          // second upload → must NOT back up again

        Assert.Equal("v3", _cloud.ContentOf(DbName));
        // The backup still holds the PRE-SESSION state, not the mid-session one.
        Assert.Equal("v1", _cloud.ContentOf(DbName + ".bak"));
        Assert.Equal(3, _cloud.Calls.Count(c => c.StartsWith("copy:")));
        Assert.Equal(6, _cloud.Calls.Count(c => c.StartsWith("upload:")));
    }

    [Fact]
    public async Task Backup_NotRepeated_AfterAppRestart_MidSession()
    {
        if (GameIsRunning) return;
        WriteLocal("v2", "fwl2");
        SeedRemoteWorld("v1", "fwl1", DateTimeOffset.UtcNow.AddDays(-1));

        await _engine.SyncNowAsync();          // backs up the pre-session v1
        Assert.Equal("v1", _cloud.ContentOf(DbName + ".bak"));

        // Simulate the app restarting mid-session: a brand-new engine, same persisted
        // session state file. Before persistence this re-backed-up and destroyed the
        // pre-session snapshot with mid-session state.
        await _engine.DisposeAsync();
        var engine2 = new SyncEngine(_settings, _cloud, null, SessionStatePath);
        File.WriteAllText(LocalDb, "v3");
        await engine2.SyncNowAsync();
        await engine2.DisposeAsync();

        Assert.Equal("v3", _cloud.ContentOf(DbName));
        Assert.Equal("v1", _cloud.ContentOf(DbName + ".bak")); // snapshot survived the restart
        Assert.Equal(3, _cloud.Calls.Count(c => c.StartsWith("copy:")));
    }

    [Fact]
    public async Task Upload_WhenIHoldLock_EvenIfRemoteIsNewer()
    {
        if (GameIsRunning) return;
        WriteLocal("mydb", "myfwl");
        AgeLocalFiles(); // remote looks newer by timestamp…
        SeedRemoteWorld("remotedb", "remotefwl");
        _cloud.Locks[_world] = new WorldLock("me", DateTimeOffset.UtcNow); // …but the lock is mine

        await _engine.SyncNowAsync();

        Assert.Equal("mydb", _cloud.ContentOf(DbName));
    }

    // ---- no-op paths -------------------------------------------------------

    [Fact]
    public async Task NoTransfer_WhenDbHashesMatch()
    {
        if (GameIsRunning) return;
        WriteLocal("same", "localfwl");
        SeedRemoteWorld("same", "remotefwl");

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls);
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task FwlOnlyChange_IsIgnored_KnownLimitation()
    {
        if (GameIsRunning) return;
        // Documents current behavior: sync direction is decided by the .db hash alone, so a
        // .fwl that diverges while the .db matches is never re-synced. Acceptable because
        // Valheim rewrites both on save, but worth knowing.
        WriteLocal("same", "DIFFERENT-local-fwl");
        SeedRemoteWorld("same", "remote-fwl");

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls);
        Assert.Equal("remote-fwl", _cloud.ContentOf(FwlName)); // untouched
    }

    // ---- self-heal paths ---------------------------------------------------

    [Fact]
    public async Task LegacyRemote_GetsCommitMarker_WhenLocalMatches()
    {
        if (GameIsRunning) return;
        WriteLocal("same", "fwl");
        SeedRemoteWorld("same", "fwl", withCommit: false); // pre-marker remote

        await _engine.SyncNowAsync();

        // Migrated in place: exactly one marker upload, describing the existing pair.
        Assert.Equal(new[] { $"upload:{CommitName}" }, _cloud.Calls);
        Assert.Equal(CommitFor("same", "fwl"), _cloud.ContentOf(CommitName));
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task StaleMarker_IsRepaired_WhenLocalMatches()
    {
        if (GameIsRunning) return;
        // Pair is fully uploaded but the marker upload failed last time (marker still
        // describes the previous version). A matching client must rewrite it.
        WriteLocal("v2", "fwl2");
        _cloud.Seed(DbName, "v2");
        _cloud.Seed(FwlName, "fwl2");
        _cloud.Seed(CommitName, CommitFor("v1", "fwl1"));

        await _engine.SyncNowAsync();

        Assert.Equal(new[] { $"upload:{CommitName}" }, _cloud.Calls);
        Assert.Equal(CommitFor("v2", "fwl2"), _cloud.ContentOf(CommitName));
    }

    [Fact]
    public async Task MissingRemoteFwl_IsCompleted_FromMatchingLocal()
    {
        if (GameIsRunning) return;
        // The .db made it up but the .fwl upload died. Our local .db is identical, so our
        // local .fwl is the right other half — finish the pair and certify it.
        WriteLocal("same", "myfwl");
        _cloud.Seed(DbName, "same");

        await _engine.SyncNowAsync();

        Assert.Equal("myfwl", _cloud.ContentOf(FwlName));
        Assert.Equal(CommitFor("same", "myfwl"), _cloud.ContentOf(CommitName));
        Assert.Equal(new[] { $"upload:{FwlName}", $"upload:{CommitName}" }, _cloud.Calls);
    }

    [Fact]
    public async Task NoRepair_WhileSomeoneElseHoldsTheLock()
    {
        if (GameIsRunning) return;
        WriteLocal("same", "fwl");
        SeedRemoteWorld("same", "fwl", withCommit: false);
        _cloud.Locks[_world] = new WorldLock("Bob", DateTimeOffset.UtcNow);

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls); // Bob is mid-session — don't touch his world's files
        Assert.Equal(SyncStatus.LockedByOther, _statuses.Last());
    }

    [Fact]
    public async Task UnselectedWorld_IsNeverTouched()
    {
        if (GameIsRunning) return;
        _settings.SelectedWorlds.Clear();
        WriteLocal("localdb", "localfwl");
        _cloud.Seed(DbName, "remotedb");

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls);
        Assert.Empty(_statuses);
    }

    [Fact]
    public async Task BakAndLockFiles_InRemoteFolder_AreInert()
    {
        if (GameIsRunning) return;
        WriteLocal("same", "fwl");
        SeedRemoteWorld("same", "fwl");
        _cloud.Seed(DbName + ".bak", "old");
        _cloud.Seed(FwlName + ".bak", "old");
        _cloud.Seed(CommitName + ".bak", "old");
        _cloud.Seed(_world + ".lock", "{}");

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls); // nothing mistaken for a world file
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    // -----------------------------------------------------------------------

    public void Dispose()
    {
        _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_dir, recursive: true); } catch { }

        // Downloads best-effort mirror into the machine's real Valheim LocalLow folder;
        // remove this test's uniquely-named world files if they landed there.
        try
        {
            var localLow = ValheimSaveLocations.ResolveLocalLowWorldsFolder();
            if (localLow is not null)
                foreach (var suffix in new[] { ".db", ".fwl", ".db.synbak", ".fwl.synbak" })
                {
                    var p = Path.Combine(localLow, _world + suffix);
                    if (File.Exists(p)) File.Delete(p);
                }
        }
        catch { }
    }
}
