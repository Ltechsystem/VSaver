using ValheimSync.Core.Sync;
using Xunit;

namespace ValheimSync.Tests;

public sealed class DebouncedWorldWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstests-watch-" + Guid.NewGuid().ToString("N"));

    public DebouncedWorldWatcherTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Fires_WithWorldName_AfterQuietPeriod()
    {
        using var watcher = new DebouncedWorldWatcher(_dir, TimeSpan.FromMilliseconds(250));
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.WorldChanged += name => fired.TrySetResult(name);

        File.WriteAllText(Path.Combine(_dir, "Alpha.db"), "save data");

        var winner = await Task.WhenAny(fired.Task, Task.Delay(10_000));
        Assert.Same(fired.Task, winner);
        Assert.Equal("Alpha", await fired.Task);
    }

    [Fact]
    public async Task RepeatedWrites_ResetTheTimer_SingleEvent()
    {
        using var watcher = new DebouncedWorldWatcher(_dir, TimeSpan.FromMilliseconds(300));
        int count = 0;
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.WorldChanged += _ => { Interlocked.Increment(ref count); fired.TrySetResult(true); };

        var path = Path.Combine(_dir, "Beta.db");
        for (int i = 0; i < 4; i++)
        {
            File.WriteAllText(path, "write " + i);
            await Task.Delay(100); // keep poking inside the debounce window
        }

        await Task.WhenAny(fired.Task, Task.Delay(10_000));
        await Task.Delay(500); // let any stray duplicate land before asserting
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NonWorldFiles_AreIgnored()
    {
        using var watcher = new DebouncedWorldWatcher(_dir, TimeSpan.FromMilliseconds(150));
        var fired = false;
        watcher.WorldChanged += _ => fired = true;

        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "hi");
        File.WriteAllText(Path.Combine(_dir, "World.db.synbak"), "backup");

        await Task.Delay(700);
        Assert.False(fired);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
