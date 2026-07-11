using ValheimSync.Core;
using Xunit;

namespace ValheimSync.Tests;

/// <summary>
/// AppSettings persists to a FIXED path (settings.json next to the test host), so all
/// tests that touch the file live in this one class — xUnit runs same-class tests
/// sequentially, which prevents them racing on the shared file.
/// </summary>
public sealed class AppSettingsTests : IDisposable
{
    private static string SettingsFile => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public AppSettingsTests() => File.Delete(SettingsFile);

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var s = new AppSettings
        {
            PlayerName = "olaf",
            DriveFolderId = "folder123",
            WorldsPathOverride = @"C:\worlds",
            PollIntervalMinutes = 7,
            DebounceSeconds = 30,
            InGameUploadMinutes = 2,
        };
        s.SelectedWorlds.Add("Midgard");
        s.Save();

        var loaded = AppSettings.Load();

        Assert.Equal("olaf", loaded.PlayerName);
        Assert.Equal("folder123", loaded.DriveFolderId);
        Assert.Equal(@"C:\worlds", loaded.WorldsPathOverride);
        Assert.Equal(7, loaded.PollIntervalMinutes);
        Assert.Equal(30, loaded.DebounceSeconds);
        Assert.Equal(2, loaded.InGameUploadMinutes);
        Assert.Contains("Midgard", loaded.SelectedWorlds);
    }

    [Fact]
    public void SelectedWorlds_StaysCaseInsensitive_AfterReload()
    {
        // Regression guard: JSON deserialization replaces the HashSet and used to drop the
        // OrdinalIgnoreCase comparer, so "midgard" from a watcher event no longer matched
        // the ticked "Midgard" after an app restart.
        var s = new AppSettings();
        s.SelectedWorlds.Add("Midgard");
        s.Save();

        var loaded = AppSettings.Load();

        Assert.Contains("MIDGARD", loaded.SelectedWorlds);
        Assert.Contains("midgard", loaded.SelectedWorlds);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(SettingsFile, "{{{ not json");

        var loaded = AppSettings.Load();

        Assert.Equal("", loaded.PlayerName);
        Assert.Equal("", loaded.DriveFolderId);
        Assert.Empty(loaded.SelectedWorlds);
    }

    [Fact]
    public void MissingFile_GivesDefaults()
    {
        File.Delete(SettingsFile);
        var loaded = AppSettings.Load();
        Assert.Equal(5, loaded.PollIntervalMinutes);
        Assert.Equal(60, loaded.DebounceSeconds);
        Assert.Equal(5, loaded.InGameUploadMinutes);
    }

    [Fact]
    public void WorldsPath_PrefersExplicitOverride()
    {
        var s = new AppSettings { WorldsPathOverride = @"C:\custom\worlds" };
        Assert.Equal(@"C:\custom\worlds", s.WorldsPath);
    }

    public void Dispose() => File.Delete(SettingsFile);
}
