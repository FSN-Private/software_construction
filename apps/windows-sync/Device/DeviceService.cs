using Platform.WindowsSync.State;

using Platform.WindowsSync.Remote;

namespace Platform.WindowsSync.Device;

public sealed class DeviceService(SyncJournal journal, IRemoteSyncClient? remote = null)
{
    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        var device = await journal.GetDeviceAsync(cancellationToken) ?? throw new InvalidOperationException("device_not_registered");
        var registration = remote is null ? null : await remote.RevokeDeviceAsync(device.LibraryId, cancellationToken);
        await journal.SaveDeviceAsync(device with { Revoked = true, Health = DeviceHealth.Revoked, Version = registration?.CheckoutVersion ?? device.Version }, cancellationToken);
    }

    public async Task PauseForAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        var device = await journal.GetDeviceAsync(cancellationToken) ?? throw new InvalidOperationException("device_not_registered");
        await journal.SaveDeviceAsync(device with { Health = DeviceHealth.AuthenticationRequired }, cancellationToken);
    }

    public async Task ExportLocalAsync(string root, string destination, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        foreach (var item in await journal.ListItemsAsync(cancellationToken: cancellationToken))
        {
            var source = Path.Combine(root, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) continue;
            var target = Path.Combine(destination, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
        }
    }
}
