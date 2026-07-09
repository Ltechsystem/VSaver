using CommunityToolkit.Mvvm.ComponentModel;
using ValheimSync.Core.Models;

namespace ValheimSync.App.ViewModels;

public partial class WorldItemViewModel : ObservableObject
{
    public string Name { get; }
    public string SizeDisplay { get; }

    public event Action<WorldItemViewModel>? SelectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private SyncStatus _status = SyncStatus.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockDisplay))]
    private string? _lockHolder;

    /// <summary>True when the current user holds this world's lock — drives the Done button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDone))]
    private bool _isLockedByMe;

    /// <summary>Done is only shown on the world the user has locked (pressed Play on).</summary>
    public bool ShowDone => IsLockedByMe;

    public WorldItemViewModel(string name, long sizeBytes)
    {
        Name = name;
        SizeDisplay = sizeBytes switch
        {
            > 1024 * 1024 => $"{sizeBytes / (1024.0 * 1024):F1} MB",
            > 1024 => $"{sizeBytes / 1024.0:F0} KB",
            _ => $"{sizeBytes} B"
        };
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);

    public string StatusDisplay => Status switch
    {
        SyncStatus.InSync => "✓ In sync",
        SyncStatus.LocalNewer => "↑ Upload pending",
        SyncStatus.RemoteNewer => "↓ Update available",
        SyncStatus.Syncing => "⟳ Syncing...",
        SyncStatus.LockedByOther => "🔒 In use",
        SyncStatus.Error => "⚠ Error",
        SyncStatus.LocalOnly => "Local only",
        SyncStatus.RemoteOnly => "Cloud only",
        _ => "—"
    };

    public string LockDisplay => LockHolder is null ? "" : $"🔒 {LockHolder}";
}
