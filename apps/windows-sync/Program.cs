using Platform.WindowsSync.Device;
using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Provider;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;
using Platform.WindowsSync.UI;

namespace Platform.WindowsSync;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is not [{ } command]) return Usage();
            var options = ProviderOptions.Load();
            var credentials = new CredentialStore(Path.Combine(options.StateDirectory, "credential.dat"));
            if (command == "setup")
            {
                var token = Environment.GetEnvironmentVariable("PLATFORM_SYNC_TOKEN");
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("PLATFORM_SYNC_TOKEN is required for setup.");
                credentials.Save(token);
            }

            await using var journal = new SyncJournal(Path.Combine(options.StateDirectory, "journal.db"));
            await journal.InitializeAsync();
            var diagnostics = new SyncDiagnostics();
            using var http = new HttpClient { BaseAddress = options.Api };
            var remote = new RemoteSyncClient(http, options.DeviceId, credentials.Load, diagnostics.Record);
            if (command == "status")
            {
                var device = await journal.GetDeviceAsync();
                var items = await journal.ListItemsAsync();
                Console.WriteLine(device is null ? "Not configured" : $"Device {device.DeviceId}: {device.Health}; branch {device.Branch}; {items.Count} items; {items.Count(item => item.State == SyncItemState.Conflicted)} conflicts.");
                foreach (var group in items.GroupBy(item => item.State).OrderBy(group => group.Key)) Console.WriteLine($"{CompanionStatus.For(group.Key).AccessibleName}: {group.Count()}");
                return 0;
            }
            if (command == "revoke") { await new DeviceService(journal, remote).RevokeAsync(); return 0; }
            if (command == "export") { await new DeviceService(journal).ExportLocalAsync(options.Root, options.ExportDirectory); return 0; }

            var provider = new NamespaceProvider(options.Root, journal, new CloudFilesAdapter(), remote);
            if (command == "setup")
            {
                await provider.RegisterAsync(new(options.DeviceId, options.LibraryId, options.Branch, null, DeviceHealth.Online, false));
                Console.WriteLine($"Registered {options.DeviceId} on branch {options.Branch}.");
                return 0;
            }
            if (command != "run") return Usage();
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            await using var host = new SyncProviderHost(options.Root, journal, remote, new CloudFilesAdapter());
            await host.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: Platform.WindowsSync <setup|run|status|revoke|export>; configure PLATFORM_SYNC_* environment variables.");
        return 2;
    }
}

internal sealed record ProviderOptions(Uri Api, string Root, string StateDirectory, string LibraryId, string DeviceId, string Branch, string ExportDirectory)
{
    public static ProviderOptions Load()
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException(name + " is required.");
        var api = new Uri(Required("PLATFORM_SYNC_API").TrimEnd('/') + "/", UriKind.Absolute);
        var root = Path.GetFullPath(Required("PLATFORM_SYNC_ROOT"));
        var state = Path.GetFullPath(Required("PLATFORM_SYNC_STATE"));
        return new(api, root, state, Required("PLATFORM_SYNC_LIBRARY"), Required("PLATFORM_SYNC_DEVICE"), Environment.GetEnvironmentVariable("PLATFORM_SYNC_BRANCH") ?? "main", Path.GetFullPath(Environment.GetEnvironmentVariable("PLATFORM_SYNC_EXPORT") ?? Path.Combine(state, "export")));
    }
}
