using Platform.WindowsSync.State;

namespace Platform.WindowsSync.UI;

public sealed record CompanionStatus(string Icon, string AccessibleName, string Guidance, string KeyboardAction)
{
    public static CompanionStatus For(DeviceHealth health) => health switch
    {
        DeviceHealth.Online => new("online", "Device online", "Synchronization is active.", "Alt+S"),
        DeviceHealth.Offline => new("paused", "Device offline", "Reconnect, then press Alt+S to retry.", "Alt+S"),
        DeviceHealth.AuthenticationRequired => new("auth", "Authentication required", "Sign in again; local content is retained.", "Alt+A"),
        DeviceHealth.Revoked => new("revoked", "Device revoked", "Export or retain local content before cleanup.", "Alt+E"),
        DeviceHealth.Error => new("error", "Device error", "Open diagnostics for an operation identifier.", "Alt+D"),
        _ => throw new ArgumentOutOfRangeException(nameof(health))
    };

    public static CompanionStatus For(SyncItemState state) => state switch
    {
        SyncItemState.OnlineOnly => new("☁", "Online only", "Open to download or press Alt+P to pin.", "Alt+P"),
        SyncItemState.Hydrating => new("↓", "Downloading", "Wait or press Escape to cancel.", "Escape"),
        SyncItemState.Available => new("✓", "Available locally", "Content is available while this device is offline.", "Alt+P"),
        SyncItemState.Pinned => new("●", "Pinned for offline use", "Press Alt+U to unpin.", "Alt+U"),
        SyncItemState.Dirty => new("✎", "Local changes pending", "Changes remain local until synchronization succeeds.", "Alt+S"),
        SyncItemState.Uploading => new("↑", "Uploading changes", "Wait or press Escape to pause.", "Escape"),
        SyncItemState.Synchronized => new("✓", "Synchronized", "Local and selected branch working state agree.", "Alt+S"),
        SyncItemState.Paused => new("‖", "Synchronization paused or offline", "Reconnect or press Alt+S to retry.", "Alt+S"),
        SyncItemState.QuotaError => new("!", "Disk or remote quota error", "Free space, then press Alt+S to retry.", "Alt+S"),
        SyncItemState.Error => new("!", "Synchronization error", "Open diagnostics for an operation identifier.", "Alt+D"),
        SyncItemState.Conflicted => new("⚠", "Conflicting local and remote changes", "Open conflict resolution; both versions are preserved.", "Alt+C"),
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
