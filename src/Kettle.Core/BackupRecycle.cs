using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kettle.Core;

public interface IBackupRecycle
{
    void Recycle(string directory);
}

// There is deliberately no permanent-delete fallback. Windows 8+ RECYCLEONDELETE
// requests recycling explicitly; aborted/unsupported operations leave cleanup pending.
public sealed partial class WindowsBackupRecycle : IBackupRecycle
{
    public void Recycle(string directory)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            throw new IOException("此系统不支持安全回收，旧备份清理待执行。");
        directory = Path.GetFullPath(directory);
        var drive = new DriveInfo(Path.GetPathRoot(directory)!);
        if (drive.DriveType != DriveType.Fixed)
            throw new IOException("备份不在本地固定磁盘，未尝试永久删除。");
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException();
                RecycleCore(directory);
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        if (failure is not null)
            throw new IOException("无法回收旧备份，未改用永久删除：" + failure.Message, failure);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RecycleCore(string path)
    {
        ComObject? instance = null;
        nint item = 0, pointer = 0;
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2)); // COINIT_APARTMENTTHREADED
        try
        {
            Guid clsid = new("3AD05575-8857-4850-9277-11B85BDB8E09");
            Guid operationId = typeof(IFileOperation).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 1, ref operationId, out pointer));
            instance = (ComObject)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            var operation = (IFileOperation)(object)instance;
            // SILENT | NOERRORUI | WANTNUKEWARNING | RECYCLEONDELETE | EARLYFAILURE.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x00184414));
            Guid iid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out item));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, 0));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out bool aborted));
            if (aborted || Directory.Exists(path) || File.Exists(path))
                throw new IOException("回收操作未完成，备份仍保留。");
        }
        finally
        {
            if (item != 0)
                Marshal.Release(item);
            instance?.FinalRelease();
            if (pointer != 0)
                Marshal.Release(pointer);
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bind, ref Guid iid, out nint item);
    [GeneratedComInterface, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    internal partial interface IFileOperation
    {
        [PreserveSig]
        int Advise(nint sink, out uint cookie);
        [PreserveSig]
        int Unadvise(uint cookie);
        [PreserveSig]
        int SetOperationFlags(uint flags);
        [PreserveSig]
        int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig]
        int SetProgressDialog(nint dialog);
        [PreserveSig]
        int SetProperties(nint properties);
        [PreserveSig]
        int SetOwnerWindow(nint owner);
        [PreserveSig]
        int ApplyPropertiesToItem(nint item);
        [PreserveSig]
        int ApplyPropertiesToItems(nint items);
        [PreserveSig]
        int RenameItem(nint item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        [PreserveSig]
        int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig]
        int MoveItem(nint item, nint target, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        [PreserveSig]
        int MoveItems(nint items, nint target);
        [PreserveSig]
        int CopyItem(nint item, nint target, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        [PreserveSig]
        int CopyItems(nint items, nint target);
        [PreserveSig]
        int DeleteItem(nint item, nint sink);
        [PreserveSig]
        int DeleteItems(nint items);
        [PreserveSig]
        int NewItem(nint target, uint attrs, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, nint sink);
        [PreserveSig]
        int PerformOperations();
        [PreserveSig]
        int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
