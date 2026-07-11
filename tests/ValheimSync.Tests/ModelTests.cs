using ValheimSync.Core.Models;
using ValheimSync.Core.Util;
using Xunit;

namespace ValheimSync.Tests;

public sealed class WorldLockTests
{
    [Fact]
    public void FreshLock_IsNotStale() =>
        Assert.False(new WorldLock("me", DateTimeOffset.UtcNow).IsStale);

    [Fact]
    public void LockOlderThan12Hours_IsStale() =>
        Assert.True(new WorldLock("me", DateTimeOffset.UtcNow.AddHours(-13)).IsStale);

    [Fact]
    public void StaleThreshold_Is12Hours() =>
        Assert.Equal(TimeSpan.FromHours(12), WorldLock.StaleAfter);
}

public sealed class WorldSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstests-ws-" + Guid.NewGuid().ToString("N"));

    public WorldSaveTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void FileNames_ComeFromPaths()
    {
        var w = new WorldSave("Midgard", Path.Combine(_dir, "Midgard.db"), Path.Combine(_dir, "Midgard.fwl"));
        Assert.Equal("Midgard.db", w.DbFileName);
        Assert.Equal("Midgard.fwl", w.FwlFileName);
    }

    [Fact]
    public void SizeBytes_SumsBothFiles_MissingCountsAsZero()
    {
        var db = Path.Combine(_dir, "W.db");
        var fwl = Path.Combine(_dir, "W.fwl");
        File.WriteAllBytes(db, new byte[10]);
        File.WriteAllBytes(fwl, new byte[3]);

        Assert.Equal(13, new WorldSave("W", db, fwl).SizeBytes);
        Assert.Equal(10, new WorldSave("W", db, Path.Combine(_dir, "missing.fwl")).SizeBytes);
    }

    [Fact]
    public void LastWriteUtc_IsMinValue_WhenDbMissing() =>
        Assert.Equal(DateTime.MinValue,
            new WorldSave("W", Path.Combine(_dir, "nope.db"), Path.Combine(_dir, "nope.fwl")).LastWriteUtc);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

public sealed class HashingTests
{
    [Fact]
    public async Task Md5_MatchesKnownVector_LowercaseHex()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "vstests-md5-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(tmp, "hello world");
            // Well-known MD5 of "hello world" — and lowercase, exactly as Drive reports it.
            Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", await Hashing.Md5Async(tmp));

            await File.WriteAllBytesAsync(tmp, Array.Empty<byte>());
            Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", await Hashing.Md5Async(tmp));
        }
        finally { File.Delete(tmp); }
    }
}
