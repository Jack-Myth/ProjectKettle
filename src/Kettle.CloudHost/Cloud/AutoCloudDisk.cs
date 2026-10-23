using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kettle.Cloud;
// Hold each ancestor directory without delete sharing during filesystem operations.
// Reads open the file itself without following reparse points; hard links are refused.
internal static class AutoCloudDisk
{
    private sealed class Lease : IDisposable
    {
        public List<SafeFileHandle> Handles = [];
        public void Dispose()
        {
            for (int i = Handles.Count - 1; i >= 0; i--)
                Handles[i].Dispose();
        }
    }

    private static Lease Parents(string path, bool create)
    {
        if (!OperatingSystem.IsWindows())
            throw SaveErrors.Tag(new PlatformNotSupportedException(nameof(SaveErrorCode.AutoCloudFileOperationsCurrentlySupportWindowsOnly)), SaveErrorCode.AutoCloudFileOperationsCurrentlySupportWindowsOnly);
        path = AutoCloudPathSafety.Absolute(path);
        var lease = new Lease();
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            var current = Path.GetPathRoot(directory)!;
            var paths = new List<string>
            {
                current
            };
            foreach (var segment in directory[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                paths.Add(current);
            }

            foreach (var parent in paths)
            {
                DebugTrace.Write("disk parent CreateFile begin " + parent);
                var handle = Open(parent, 0x80, 3, 0x02200000);
                if (handle.IsInvalid)
                {
                    int code = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (code is not (2 or 3))
                        throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CouldNotLockAutoCloudPath), new Win32Exception(code)), SaveErrorCode.CouldNotLockAutoCloudPath, detail: parent + " (Win32 " + code + ")");
                    if (!create)
                        break;
                    Directory.CreateDirectory(parent);
                    handle = Open(parent, 0x80, 3, 0x02200000);
                }

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CouldNotCreateASafeSaveDirectory)), SaveErrorCode.CouldNotCreateASafeSaveDirectory);
                }

                lease.Handles.Add(handle);
                var info = Info(handle);
                if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.AutoCloudDirectoryIsALinkOrInvalidPath)), SaveErrorCode.AutoCloudDirectoryIsALinkOrInvalidPath);
            }

            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public static IDisposable HoldDirectory(string directory) => Parents(Path.Combine(directory, "__kettle_directory_lease__"), false);
    internal sealed class ReadLease : IDisposable
    {
        private readonly IDisposable parents;
        private readonly FileInfoNative before;
        public FileStream Stream { get; }
        internal ReadLease(IDisposable parents, FileStream stream)
        {
            this.parents = parents;
            Stream = stream;
            before = Info(stream.SafeFileHandle);
        }
        public void Verify()
        {
            var after = Info(Stream.SafeFileHandle);
            if (before.WriteLow != after.WriteLow || before.WriteHigh != after.WriteHigh ||
                before.SizeLow != after.SizeLow || before.SizeHigh != after.SizeHigh)
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.SavesAreStillChangingWhileBeingRead)), SaveErrorCode.SavesAreStillChangingWhileBeingRead);
        }
        public void Dispose() { Stream.Dispose(); parents.Dispose(); }
    }

    public static ReadLease OpenRead(string path)
    {
        var parents = Parents(path, false);
        FileStream? stream = null;
        try
        {
            var handle = Open(path, 0x80000000, 1, 0x00200000);
            if (handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CouldNotReadTheSaveFile), new Win32Exception(code)), SaveErrorCode.CouldNotReadTheSaveFile);
            }
            stream = new FileStream(handle, FileAccess.Read);
            var info = Info(handle);
            if ((info.Attributes & (0x400 | 0x10)) != 0 || info.Links != 1 || stream.Length > CloudTransfer.MaxFileBytes)
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.SaveLinksHardLinksOrFileSizesAreUnsupported)), SaveErrorCode.SaveLinksHardLinksOrFileSizesAreUnsupported);
            return new ReadLease(parents, stream);
        }
        catch { stream?.Dispose(); parents.Dispose(); throw; }
    }

    public static async Task<SaveDigest> Digest(string path, CancellationToken token)
    {
        using var source = OpenRead(path);
        var result = await SaveStreams.Copy(source.Stream, null, CloudTransfer.MaxFileBytes, token);
        source.Verify();
        return result;
    }

    public static async Task<SaveDigest> Copy(string sourcePath, string target, CancellationToken token, string? expectedHash = null)
    {
        using var source = OpenRead(sourcePath);
        SaveDigest digest;
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            digest = await SaveStreams.Copy(source.Stream, output, CloudTransfer.MaxFileBytes, token);
            source.Verify();
            if (expectedHash is not null && digest.Sha1 != expectedHash) throw new IOException("Save changed while copying.");
            output.Flush(true);
        }
        if ((await Digest(target, token)).Sha256 != digest.Sha256) throw new IOException("Save copy verification failed.");
        return digest;
    }

    public static async Task Write(string path, string sourcePath, string expectedHash, long timestamp, CancellationToken token, string? temporaryPath = null)
    {
        using var lease = Parents(path, true);
        AutoCloudPathSafety.NoLinks(path);
        var temporary = temporaryPath ?? Path.Combine(Path.GetDirectoryName(path)!, ".kettle-" + Guid.NewGuid().ToString("N") + ".tmp");
        if (Path.GetDirectoryName(temporary) != Path.GetDirectoryName(path) || !IsTemporary(temporary))
            throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.InvalidLocalTransactionTemporaryPath)), SaveErrorCode.InvalidLocalTransactionTemporaryPath);
        bool created = false;
        try
        {
            using var source = OpenRead(sourcePath);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                created = true;
                var digest = await SaveStreams.Copy(source.Stream, output, CloudTransfer.MaxFileBytes, token);
                source.Verify();
                if (digest.Sha1 != expectedHash) throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.RecoveryCopyHashMismatch)), SaveErrorCode.RecoveryCopyHashMismatch);
                output.Flush(true);
            }
            File.SetLastWriteTimeUtc(temporary, DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime);
            AutoCloudPathSafety.NoLinks(path);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void DeleteRollbackFile(string path)
    {
        using var lease = Parents(path, false);
        AutoCloudPathSafety.NoLinks(path);
        File.Delete(path);
    }

    public static bool IsTemporary(string path) => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"\A\.kettle-[a-f0-9]{32}\.tmp\z");
    private static SafeFileHandle Open(string path, uint access, uint share, uint flags) =>
        CreateFileW(@"\\?\" + AutoCloudPathSafety.Absolute(path), access, share, 0, 3, flags, 0);
    private static FileInfoNative Info(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var info) ? info : throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CouldNotVerifyTheSaveHandle), new Win32Exception(Marshal.GetLastWin32Error())), SaveErrorCode.CouldNotVerifyTheSaveHandle);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
}
