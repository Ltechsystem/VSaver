using ValheimSync.Core.Sync;
using Xunit;

namespace ValheimSync.Tests;

public sealed class ValheimProcessTests
{
    [Fact]
    public void SteamAppId_IsValheims() => Assert.Equal("892970", ValheimProcess.SteamAppId);

    [Fact]
    public async Task WaitForStart_TimesOutQuickly_WhenGameNotRunning()
    {
        if (ValheimProcess.IsRunning()) return; // someone is actually playing — skip

        var started = await ValheimProcess.WaitForStartAsync(TimeSpan.FromMilliseconds(50));
        Assert.False(started);
    }

    [Fact]
    public async Task WaitUntilExit_ReturnsImmediately_WhenGameNotRunning()
    {
        if (ValheimProcess.IsRunning()) return;

        var task = ValheimProcess.WaitUntilExitAsync();
        var winner = await Task.WhenAny(task, Task.Delay(2_000));
        Assert.Same(task, winner);
    }
}

public sealed class ValheimSaveLocationsTests
{
    // These probe the real machine, so only invariants that hold everywhere are asserted:
    // enumeration never throws, and every candidate ends in a Valheim worlds folder name.

    [Fact]
    public void Candidates_EnumerateWithoutThrowing()
    {
        var all = ValheimSaveLocations.Candidates().ToList();
        Assert.All(all, p =>
            Assert.True(p.EndsWith("worlds") || p.EndsWith("worlds_local"), p));
    }

    [Fact]
    public void ResolveLocalLow_NeverThrows()
    {
        var folder = ValheimSaveLocations.ResolveLocalLowWorldsFolder();
        if (folder is not null)
            Assert.Contains("Valheim", folder);
    }
}
