using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Platform.WindowsSync.Platform;

public sealed record PlaceholderDefinition(string RelativeName, PlaceholderIdentity Identity, DateTimeOffset ModifiedAt, FileAttributes Attributes = FileAttributes.Normal);

public interface ICloudFilesAdapter
{
    string Version { get; }
    void RegisterRoot(string root, ReadOnlySpan<byte> rootIdentity);
    void CreatePlaceholders(string directory, IReadOnlyList<PlaceholderDefinition> placeholders);
    void UpdatePlaceholder(string path, PlaceholderIdentity identity);
    void SetPinState(string path, bool pinned);
    void Hydrate(string path);
    void Dehydrate(string path);
}

public sealed class CloudFilesAdapter : ICloudFilesAdapter
{
    private static readonly Guid ProviderId = Guid.Parse("e59eb32a-2751-48f0-b4e8-3932955f04df");
    public string Version => "cloud-files-v1";

    public void RegisterRoot(string root, ReadOnlySpan<byte> rootIdentity)
    {
        RequireWindows();
        if (rootIdentity.Length > 64 * 1024) throw new ArgumentException("invalid_root_identity", nameof(rootIdentity));
        Directory.CreateDirectory(root);
        var name = Marshal.StringToCoTaskMemUni("Platform Windows Sync");
        var version = Marshal.StringToCoTaskMemUni("1");
        var identity = Marshal.AllocCoTaskMem(rootIdentity.Length);
        try
        {
            Marshal.Copy(rootIdentity.ToArray(), 0, identity, rootIdentity.Length);
            var registration = new SyncRegistration
            {
                StructSize = (uint)Marshal.SizeOf<SyncRegistration>(), ProviderName = name, ProviderVersion = version,
                ProviderId = ProviderId, SyncRootIdentity = identity, SyncRootIdentityLength = (uint)rootIdentity.Length
            };
            var policies = new SyncPolicies
            {
                StructSize = (uint)Marshal.SizeOf<SyncPolicies>(), Hydration = new(2, 1), Population = new(2, 0),
                InSync = 0x00000100, HardLink = 0, PlaceholderManagement = 0
            };
            ThrowIfFailed(CfRegisterSyncRoot(Path.GetFullPath(root), ref registration, ref policies, 0x00000001));
        }
        finally
        {
            Marshal.FreeCoTaskMem(identity);
            Marshal.FreeCoTaskMem(version);
            Marshal.FreeCoTaskMem(name);
        }
    }

    public void CreatePlaceholders(string directory, IReadOnlyList<PlaceholderDefinition> placeholders)
    {
        RequireWindows();
        if (placeholders.Count == 0) return;
        var size = Marshal.SizeOf<PlaceholderCreateInfo>();
        var native = Marshal.AllocCoTaskMem(size * placeholders.Count);
        var allocations = new List<IntPtr>(placeholders.Count * 2);
        try
        {
            for (var index = 0; index < placeholders.Count; index++)
            {
                var placeholder = placeholders[index];
                if (Path.GetFileName(placeholder.RelativeName) != placeholder.RelativeName) throw new ArgumentException("placeholder_name_must_be_relative");
                var name = Marshal.StringToCoTaskMemUni(placeholder.RelativeName);
                var identityBytes = placeholder.Identity.Encode();
                var identity = Marshal.AllocCoTaskMem(identityBytes.Length);
                allocations.Add(name);
                allocations.Add(identity);
                Marshal.Copy(identityBytes, 0, identity, identityBytes.Length);
                var created = new PlaceholderCreateInfo
                {
                    RelativeFileName = name,
                    FsMetadata = new(new(placeholder.ModifiedAt.UtcDateTime.ToFileTimeUtc(), (uint)placeholder.Attributes), placeholder.Identity.Length),
                    FileIdentity = identity,
                    FileIdentityLength = (uint)identityBytes.Length
                };
                Marshal.StructureToPtr(created, IntPtr.Add(native, index * size), false);
            }
            ThrowIfFailed(CfCreatePlaceholders(directory, native, (uint)placeholders.Count, 1, out var processed));
            if (processed != placeholders.Count) throw new IOException("placeholder_not_processed");
            for (var index = 0; index < processed; index++)
            {
                var result = Marshal.PtrToStructure<PlaceholderCreateInfo>(IntPtr.Add(native, index * size));
                ThrowIfFailed(result.Result);
            }
        }
        finally
        {
            foreach (var allocation in allocations) Marshal.FreeCoTaskMem(allocation);
            Marshal.FreeCoTaskMem(native);
        }
    }

    public void SetPinState(string path, bool pinned)
    {
        using var handle = Open(path);
        ThrowIfFailed(CfSetPinState(handle, pinned ? 1u : 2u, 0, IntPtr.Zero));
    }

    public void UpdatePlaceholder(string path, PlaceholderIdentity identity)
    {
        using var handle = Open(path);
        var bytes = identity.Encode();
        var native = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, native, bytes.Length);
            ThrowIfFailed(CfUpdatePlaceholder(handle, IntPtr.Zero, native, (uint)bytes.Length, IntPtr.Zero, 0, 2, IntPtr.Zero, IntPtr.Zero));
        }
        finally { Marshal.FreeCoTaskMem(native); }
    }

    public void Hydrate(string path)
    {
        using var handle = Open(path);
        ThrowIfFailed(CfHydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero));
    }

    public void Dehydrate(string path)
    {
        using var handle = Open(path);
        ThrowIfFailed(CfDehydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero));
    }

    private static SafeFileHandle Open(string path)
    {
        RequireWindows();
        return File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) throw new PlatformNotSupportedException("Cloud Files requires supported Windows 11.");
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0) throw new Win32Exception(hresult, Marshal.GetExceptionForHR(hresult)?.Message);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Policy(ushort Primary, ushort Modifier);
    [StructLayout(LayoutKind.Sequential)] private struct SyncPolicies { public uint StructSize; public Policy Hydration; public Policy Population; public uint InSync; public uint HardLink; public uint PlaceholderManagement; }
    [StructLayout(LayoutKind.Sequential)] private struct SyncRegistration { public uint StructSize; public IntPtr ProviderName; public IntPtr ProviderVersion; public Guid ProviderId; public IntPtr SyncRootIdentity; public uint SyncRootIdentityLength; public IntPtr FileIdentity; public uint FileIdentityLength; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct FileBasicInfo(long CreationTime, long LastAccessTime, long LastWriteTime, long ChangeTime, uint FileAttributes)
    {
        public FileBasicInfo(long modified, uint attributes) : this(modified, modified, modified, modified, attributes) { }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct FsMetadata(FileBasicInfo BasicInfo, long FileSize);
    [StructLayout(LayoutKind.Sequential)] private struct PlaceholderCreateInfo { public IntPtr RelativeFileName; public FsMetadata FsMetadata; public IntPtr FileIdentity; public uint FileIdentityLength; public uint Flags; public int Result; public long CreateUsn; }

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfRegisterSyncRoot(string syncRootPath, ref SyncRegistration registration, ref SyncPolicies policies, uint flags);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfCreatePlaceholders(string baseDirectoryPath, IntPtr placeholderArray, uint placeholderCount, uint createFlags, out uint entriesProcessed);
    [DllImport("cldapi.dll")] private static extern int CfSetPinState(SafeFileHandle fileHandle, uint pinState, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern int CfUpdatePlaceholder(SafeFileHandle fileHandle, IntPtr fsMetadata, IntPtr fileIdentity, uint fileIdentityLength, IntPtr dehydrateRangeArray, uint dehydrateRangeCount, uint updateFlags, IntPtr updateUsn, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern int CfHydratePlaceholder(SafeFileHandle fileHandle, long startingOffset, long length, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern int CfDehydratePlaceholder(SafeFileHandle fileHandle, long startingOffset, long length, uint flags, IntPtr overlapped);
}
