using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kettle.Core;

// Steam enumerates with FindFirstFileExW: Windows matches both long names and
// existing 8.3 aliases. A regex or .NET directory filter loses the alias matches.
internal static unsafe class AutoCloudFileSearch
{
    public static HashSet<string> MatchingNames(string directory, string pattern)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var handle = FindFirstFileExW("\\\\?\\" + Path.Combine(directory, pattern), 1, out var data, 0, 0, 2);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3)
                return names;
            throw new IOException("Could not enumerate AutoCloud saves.", new Win32Exception(error));
        }
        do
        {
            var name = new string(data.FileName);
            if (name is not ("." or ".."))
                names.Add(name);
            if (names.Count > 4096)
                throw new NotSupportedException(L.Text(TextId.TooManyEntriesInTheSaveDirectory));
        } while (FindNextFileW(handle, out data));
        var lastError = Marshal.GetLastWin32Error();
        if (lastError != 18)
            throw new IOException("Could not finish enumerating AutoCloud saves.", new Win32Exception(lastError));
        return names;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        public fixed char FileName[260];
        public fixed char AlternateName[14];
    }

    private sealed class FindHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern FindHandle FindFirstFileExW(string pattern, int level, out FindData data, int search, nint filter, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(FindHandle handle, out FindData data);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint handle);
}
