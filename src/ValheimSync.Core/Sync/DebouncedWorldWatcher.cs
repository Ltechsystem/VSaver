namespace ValheimSync.Core.Sync;

/// <summary>
/// Watches the worlds folder and raises <see cref="WorldChanged"/> only after a
/// world's files have been quiet for the debounce window. This is what makes
/// "sync every 15 minutes" safe: we never upload a save Valheim is still writing.
/// </summary>
public sealed class DebouncedWorldWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _debounce;
    private readonly Dictionary<string, CancellationTokenSource> _pending = new();
    private readonly object _gate = new();

    /// <summary>Fired with the world name once its files have settled.</summary>
    public event Action<string>? WorldChanged;

    public DebouncedWorldWatcher(string worldsPath, TimeSpan debounce)
    {
        _debounce = debounce;
        _watcher = new FileSystemWatcher(worldsPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.Name);
        if (!string.Equals(ext, ".db", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".fwl", StringComparison.OrdinalIgnoreCase))
            return;

        var world = Path.GetFileNameWithoutExtension(e.Name!);

        lock (_gate)
        {
            // Restart the timer for this world on every write.
            if (_pending.TryGetValue(world, out var old))
                old.Cancel();

            var cts = new CancellationTokenSource();
            _pending[world] = cts;

            _ = Task.Delay(_debounce, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                lock (_gate) _pending.Remove(world);
                WorldChanged?.Invoke(world);
            }, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_gate)
        {
            foreach (var cts in _pending.Values) cts.Cancel();
            _pending.Clear();
        }
    }
}
