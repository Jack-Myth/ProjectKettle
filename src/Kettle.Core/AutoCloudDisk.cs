using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kettle.Core;
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
            throw new PlatformNotSupportedException("AutoCloud 文件操作当前仅支持 Windows。");
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
                var handle = Open(parent, 0x80, 3, 0x02200000);
                if (handle.IsInvalid)
                {
                    int code = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (code is not (2 or 3))
                        throw new IOException("无法锁定 AutoCloud 路径：" + parent + " (Win32 " + code + ")", new Win32Exception(code));
                    if (!create)
                        break;
                    Directory.CreateDirectory(parent);
                    handle = Open(parent, 0x80, 3, 0x02200000);
                }

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new IOException("无法创建安全的存档目录。");
                }

                lease.Handles.Add(handle);
                var info = Info(handle);
                if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
                    throw new IOException("AutoCloud 目录是链接或无效路径。");
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
    public static async Task<byte[]> Read(string path, CancellationToken token)
    {
        using var lease = Parents(path, false);
        var handle = Open(path, 0x80000000, 1, 0x00200000);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("无法读取存档文件。", new Win32Exception(code));
        }

        await using var stream = new FileStream(handle, FileAccess.Read);
        var info = Info(handle);
        if ((info.Attributes & (0x400 | 0x10)) != 0 || info.Links != 1 || stream.Length > CloudTransfer.MaxFileBytes)
            throw new IOException("存档链接、硬链接或文件大小不受支持。");
        using var result = new MemoryStream();
        await stream.CopyToAsync(result, token);
        var after = Info(handle);
        if (result.Length != stream.Length || info.WriteLow != after.WriteLow || info.WriteHigh != after.WriteHigh)
            throw new IOException("读取时存档仍在变化。");
        return result.ToArray();
    }

    public static async Task Write(string path, byte[] bytes, long timestamp, CancellationToken token, string? temporaryPath = null)
    {
        using var lease = Parents(path, true);
        AutoCloudPathSafety.NoLinks(path);
        var temporary = temporaryPath ?? Path.Combine(Path.GetDirectoryName(path)!, ".kettle-" + Guid.NewGuid().ToString("N") + ".tmp");
        if (Path.GetDirectoryName(temporary) != Path.GetDirectoryName(path) || !IsTemporary(temporary))
            throw new IOException("本地事务临时路径无效。");
        bool created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
            {
                created = true;
                await stream.WriteAsync(bytes, token);
                stream.Flush(true);
            }

            File.SetLastWriteTimeUtc(temporary, DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime);
            AutoCloudPathSafety.NoLinks(path);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (created && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void DeleteRollbackFile(string path)
    {
        using var lease = Parents(path, false);
        AutoCloudPathSafety.NoLinks(path);
        File.Delete(path);
    }

    public static bool IsTemporary(string path) => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"\A\.kettle-[a-f0-9]{32}\.tmp\z");
    private static SafeFileHandle Open(string path, uint access, uint share, uint flags) => CreateFileW(path, access, share, 0, 3, flags, 0);
    private static FileInfoNative Info(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var info) ? info : throw new IOException("无法验证存档句柄。", new Win32Exception(Marshal.GetLastWin32Error()));
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
