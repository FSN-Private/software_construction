using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Platform.WindowsSync.Platform;

public sealed class CloudFilesConnection : IDisposable
{
    private readonly Func<PlaceholderIdentity, CancellationToken, Task<byte[]>> download;
    private readonly Action<PlaceholderIdentity, bool>? completed;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> requests = new();
    private readonly Callback callback;
    private GCHandle context;
    private long connectionKey;
    private bool disposed;

    public CloudFilesConnection(string root, Func<PlaceholderIdentity, CancellationToken, Task<byte[]>> download, Action<PlaceholderIdentity, bool>? completed = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) throw new PlatformNotSupportedException("Cloud Files requires supported Windows 11.");
        this.download = download;
        this.completed = completed;
        callback = Dispatch;
        context = GCHandle.Alloc(this);
        var registrations = new[]
        {
            new CallbackRegistration(0, Marshal.GetFunctionPointerForDelegate(callback)),
            new CallbackRegistration(1, Marshal.GetFunctionPointerForDelegate(callback)),
            new CallbackRegistration(-1, IntPtr.Zero)
        };
        var size = Marshal.SizeOf<CallbackRegistration>();
        var table = Marshal.AllocCoTaskMem(size * registrations.Length);
        try
        {
            for (var index = 0; index < registrations.Length; index++) Marshal.StructureToPtr(registrations[index], IntPtr.Add(table, index * size), false);
            ThrowIfFailed(CfConnectSyncRoot(Path.GetFullPath(root), table, GCHandle.ToIntPtr(context), 0, out connectionKey));
        }
        catch
        {
            context.Free();
            throw;
        }
        finally { Marshal.FreeCoTaskMem(table); }
    }

    private static void Dispatch(IntPtr infoPointer, IntPtr parametersPointer)
    {
        var info = Marshal.PtrToStructure<CallbackInfo>(infoPointer);
        if (!GCHandle.FromIntPtr(info.CallbackContext).IsAllocated || GCHandle.FromIntPtr(info.CallbackContext).Target is not CloudFilesConnection owner) return;
        var size = Marshal.ReadInt32(parametersPointer);
        if (size >= Marshal.SizeOf<FetchParameters>())
        {
            var parameters = Marshal.PtrToStructure<FetchParameters>(parametersPointer);
            if (info.FileIdentity != IntPtr.Zero && info.FileIdentityLength > 0)
            {
                var bytes = new byte[info.FileIdentityLength];
                Marshal.Copy(info.FileIdentity, bytes, 0, bytes.Length);
                var identity = PlaceholderIdentity.Decode(bytes);
                _ = owner.TransferAsync(info, parameters.RequiredOffset, parameters.RequiredLength, identity);
                return;
            }
        }
        if (size >= Marshal.SizeOf<CancelParameters>()) owner.Cancel(info.RequestKey);
    }

    private async Task TransferAsync(CallbackInfo info, long requestedOffset, long requestedLength, PlaceholderIdentity identity)
    {
        var cancellation = requests.GetOrAdd(info.RequestKey, static _ => new());
        try
        {
            var bytes = await download(identity, cancellation.Token);
            if (bytes.LongLength != identity.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(identity.Sha256))) throw new InvalidDataException("checksum_mismatch");
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                Execute(info, buffer, 0, bytes.LongLength, 0);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            completed?.Invoke(identity, true);
        }
        catch
        {
            Execute(info, IntPtr.Zero, requestedOffset, requestedLength, unchecked((int)0xC0000001));
            completed?.Invoke(identity, false);
        }
        finally
        {
            if (requests.TryRemove(info.RequestKey, out var source)) source.Dispose();
        }
    }

    private static void Execute(CallbackInfo callback, IntPtr buffer, long offset, long length, int completionStatus)
    {
        var info = new OperationInfo
        {
            StructSize = (uint)Marshal.SizeOf<OperationInfo>(), Type = 0, ConnectionKey = callback.ConnectionKey,
            TransferKey = callback.TransferKey, CorrelationVector = callback.CorrelationVector, RequestKey = callback.RequestKey
        };
        var parameters = new OperationParameters { ParamSize = (uint)Marshal.SizeOf<OperationParameters>(), CompletionStatus = completionStatus, Buffer = buffer, Offset = offset, Length = length };
        ThrowIfFailed(CfExecute(ref info, ref parameters));
    }

    private void Cancel(long requestKey)
    {
        if (requests.TryGetValue(requestKey, out var source)) source.Cancel();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var request in requests.Values) request.Cancel();
        if (connectionKey != 0) _ = CfDisconnectSyncRoot(connectionKey);
        if (context.IsAllocated) context.Free();
        GC.KeepAlive(callback);
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0) throw new Win32Exception(hresult, Marshal.GetExceptionForHR(hresult)?.Message);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Callback(IntPtr callbackInfo, IntPtr callbackParameters);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct CallbackRegistration(int Type, IntPtr Callback);
    [StructLayout(LayoutKind.Sequential)] private struct FetchParameters { public uint ParamSize; public uint Flags; public long RequiredOffset; public long RequiredLength; public long OptionalOffset; public long OptionalLength; public long LastDehydrationTime; public uint LastDehydrationReason; }
    [StructLayout(LayoutKind.Sequential)] private struct CancelParameters { public uint ParamSize; public uint Flags; public long Offset; public long Length; }
    [StructLayout(LayoutKind.Sequential)] private struct CallbackInfo
    {
        public uint StructSize; public long ConnectionKey; public IntPtr CallbackContext; public IntPtr VolumeGuidName; public IntPtr VolumeDosName; public uint VolumeSerialNumber;
        public long SyncRootFileId; public IntPtr SyncRootIdentity; public uint SyncRootIdentityLength; public long FileId; public long FileSize; public IntPtr FileIdentity; public int FileIdentityLength;
        public IntPtr NormalizedPath; public long TransferKey; public byte PriorityHint; public IntPtr CorrelationVector; public IntPtr ProcessInfo; public long RequestKey;
    }
    [StructLayout(LayoutKind.Sequential)] private struct OperationInfo { public uint StructSize; public int Type; public long ConnectionKey; public long TransferKey; public IntPtr CorrelationVector; public IntPtr SyncStatus; public long RequestKey; }
    [StructLayout(LayoutKind.Sequential)] private struct OperationParameters { public uint ParamSize; private uint Padding; public uint Flags; public int CompletionStatus; public IntPtr Buffer; public long Offset; public long Length; }

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfConnectSyncRoot(string root, IntPtr callbackTable, IntPtr callbackContext, uint flags, out long connectionKey);
    [DllImport("cldapi.dll")] private static extern int CfDisconnectSyncRoot(long connectionKey);
    [DllImport("cldapi.dll")] private static extern int CfExecute(ref OperationInfo operationInfo, ref OperationParameters operationParameters);
}
