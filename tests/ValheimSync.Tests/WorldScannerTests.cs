using ValheimSync.Core.Sync;
using Xunit;

namespace ValheimSync.Tests;

public sealed class WorldScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstests-scan-" + Guid.NewGuid().ToString("N"));

    public WorldScannerTests() => Directory.CreateDirectory(_dir);

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "x");

    [Fact]
    public void FindsDbFwlPairs_SortedCaseInsensitively()
    {
        Touch("beta.fwl"); Touch("beta.db");
        Touch("Alpha.fwl"); Touch("Alpha.db");

        var worlds = WorldScanner.Scan(_dir);

        Assert.Equal(new[] { "Alpha", "beta" }, worlds.Select(w => w.Name));
        Assert.All(worlds, w => Assert.True(File.Exists(w.DbPath) && File.Exists(w.FwlPath)));
    }

    [Fact]
    public void IgnoresValheimBackupFiles_AndOldExtension()
    {
        Touch("Midgard.fwl"); Touch("Midgard.db");
        Touch("Midgard_backup_auto-20240101.fwl");
        Touch("Midgard_backup_auto-20240101.db");
        Touch("Midgard.fwl.old");
        Touch("Midgard.db.old");

        var worlds = WorldScanner.Scan(_dir);

        Assert.Single(worlds);
        Assert.Equal("Midgard", worlds[0].Name);
    }

    [Fact]
    public void FwlWithoutDb_IsStillListed_KnownLimitation()
    {
        // Documents current behavior: the scanner keys off .fwl alone, so a world whose .db
        // is missing (e.g. mid-first-write) is returned anyway — an upload attempt on it
        // would throw when opening the .db. See flaw summary.
        Touch("Lonely.fwl");

        var worlds = WorldScanner.Scan(_dir);

        Assert.Single(worlds);
        Assert.False(File.Exists(worlds[0].DbPath));
    }

    [Fact]
    public void MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(WorldScanner.Scan(Path.Combine(_dir, "does-not-exist")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
