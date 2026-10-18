using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kettle.Core;

public sealed record UpdateProcessResult(bool Success, int Processes, int[] ExitCodes);
// A private non-breakaway Job owns the suspended root BEFORE it can bootstrap.
// Explicit cancellation terminates this private Job only; normal completion waits all descendants.
public static class WindowsUpdateProcess
{
    public static Task<UpdateProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<int, string, bool> output, bool pseudoConsole = false, CancellationToken cancellationToken = default) => Task.Run(() => Run(executable, arguments, output, pseudoConsole, cancellationToken));
    private static UpdateProcessResult Run(string executable, IReadOnlyList<string> arguments, Action<int, string, bool> output, bool pseudoConsole = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        nint job = 0, port = 0, attributes = 0, handles = 0, stdin = 0, thread = 0, pseudo = 0, consoleInput = 0;
        nint[] reads = new nint[2], writes = new nint[2];
        var processes = new Dictionary<int, nint>();
        var pumps = new List<Task>();
        bool resumed = false, uncertain = false, attributesReady = false;
        int pumpFailed = 0;
        bool terminationRequested = false;
        try
        {
            job = CreateJobObjectW(0, null);
            Win(job != 0);
            port = CreateIoCompletionPort(new nint(-1), 0, 0, 1);
            Win(port != 0);
            var association = new Association
            {
                Key = new nint(1),
                Port = port
            };
            Win(SetInformationJobObject(job, 7, ref association, (uint)Marshal.SizeOf<Association>()));
            var security = new Security
            {
                Length = Marshal.SizeOf<Security>(),
                Inherit = 1
            };
            int streams = pseudoConsole ? 1 : 2;
            for (int i = 0; i < streams; i++)
            {
                Win(CreatePipe(out reads[i], out writes[i], ref security, 0));
                Win(SetHandleInformation(reads[i], 1, 0));
            }

            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal((nint)size);
            Win(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesReady = true;
            var startup = new StartupEx
            {
                Info = new Startup
                {
                    Size = Marshal.SizeOf<StartupEx>()
                },
                Attributes = attributes
            };
            string launcher = executable;
            IEnumerable<string> launchArguments = arguments;
            if (pseudoConsole)
            {
                Win(CreatePipe(out consoleInput, out stdin, ref security, 0));
                Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord { X = (short)ConPtyTerminal.Columns, Y = 80 }, consoleInput, writes[0], 0, out pseudo));
                Win(UpdateProcThreadAttribute(attributes, 0, 0x20016, pseudo, (nuint)IntPtr.Size, 0, 0));
                launcher = Path.Combine(AppContext.BaseDirectory, "Kettle.TerminalHost.exe");
                if (!File.Exists(launcher))
                    throw new IOException(L.Text(TextId.TheUpdateComponentIsMissingExtractTheApplicationAgain));
                launchArguments = new[]
                {
                    executable
                }.Concat(arguments);
                startup.Info.Flags = 1;
            }
            else
            {
                stdin = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, 0);
                Win(stdin != new nint(-1));
                handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
                Marshal.WriteIntPtr(handles, 0, stdin);
                Marshal.WriteIntPtr(handles, IntPtr.Size, writes[0]);
                Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, writes[1]);
                Win(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(3 * IntPtr.Size), 0, 0));
                startup.Info.Flags = 0x100;
                startup.Info.Input = stdin;
                startup.Info.Output = writes[0];
                startup.Info.Error = writes[1];
            }

            var command = new StringBuilder(string.Join(" ", new[] { launcher }.Concat(launchArguments).Select(Quote)));
            Win(CreateProcessW(launcher, command, 0, 0, !pseudoConsole, pseudoConsole ? 0x00080004u : 0x08080004u, 0, Path.GetDirectoryName(executable), ref startup, out var root));
            thread = root.Thread;
            processes.Add((int)root.ProcessId, root.Process);
            if (!AssignProcessToJobObject(job, root.Process))
            {
                TerminateProcess(root.Process, 1);
                Win(false);
            }

            for (int i = 0; i < streams; i++)
            {
                CloseHandle(writes[i]);
                writes[i] = 0;
                int stream = i;
                var pipe = new FileStream(new SafeFileHandle(reads[i], true), FileAccess.Read);
                reads[i] = 0;
                pumps.Add(Task.Run(async () =>
                {
                    using (pipe)
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), true, 1024))
                    {
                        var chars = new char[1024];
                        try
                        {
                            int count;
                            while ((count = await reader.ReadAsync(chars)) > 0)
                            {
                                try
                                {
                                    output(stream, new string(chars, 0, count), false);
                                }
                                catch
                                {
                                    Interlocked.Exchange(ref pumpFailed, 1);
                                }
                            }

                            output(stream, "", true);
                        }
                        catch
                        {
                            Interlocked.Exchange(ref pumpFailed, 1);
                        }
                    }
                }));
            }

            cancellationToken.ThrowIfCancellationRequested();
            Win(ResumeThread(thread) != uint.MaxValue);
            resumed = true;
            CloseHandle(thread);
            thread = 0;
            while (true)
            {
                if (cancellationToken.IsCancellationRequested && !terminationRequested)
                {
                    Win(TerminateJobObject(job, 1223));
                    terminationRequested = true;
                }

                if (!GetQueuedCompletionStatus(port, out uint message, out _, out var processId, 200))
                {
                    if (Marshal.GetLastWin32Error() == 258)
                        continue;
                    Win(false);
                }

                if (message == 6)
                {
                    int pid = checked((int)processId);
                    if (!processes.ContainsKey(pid))
                    {
                        var handle = OpenProcess(0x101000, false, (uint)pid);
                        if (handle == 0)
                            uncertain = true;
                        else
                            processes.Add(pid, handle);
                    }
                }

                if (message == 8)
                    uncertain = true;
                if (message == 4)
                    break; // ALL descendants gone, not merely the launcher.
            }

            if (pseudo != 0)
            {
                ClosePseudoConsole(pseudo);
                pseudo = 0;
            }

            Task.WaitAll(pumps.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            var codes = new List<int>();
            foreach (var handle in processes.Values)
            {
                if (!GetExitCodeProcess(handle, out uint code) || code == 259)
                    uncertain = true;
                else
                    codes.Add(unchecked((int)code));
            }

            var accounting = new Accounting();
            if (!QueryInformationJobObject(job, 1, ref accounting, (uint)Marshal.SizeOf<Accounting>(), out _) || accounting.Total != processes.Count)
                uncertain = true;
            return new(!uncertain && pumpFailed == 0 && codes.Count > 0 && codes.All(c => c == 0), (int)accounting.Total, codes.ToArray());
        }
        finally
        {
            if (!resumed && processes.Count > 0)
                foreach (var handle in processes.Values)
                    TerminateProcess(handle, 1); // Our never-resumed root only.
            // After explicit cancellation, reap the terminated Job; other failures still wait safely.
            if (resumed && job != 0)
            {
                var accounting = new Accounting();
                while (QueryInformationJobObject(job, 1, ref accounting, (uint)Marshal.SizeOf<Accounting>(), out _) && accounting.Active > 0)
                    Thread.Sleep(200);
            }

            foreach (var handle in writes.Concat(reads).Concat(new[] { stdin, thread, consoleInput }).Concat(processes.Values))
                if (handle != 0 && handle != new nint(-1))
                    CloseHandle(handle);
            if (pseudo != 0)
            {
                ClosePseudoConsole(pseudo);
                pseudo = 0;
            }

            if (pumps.Count > 0)
                Task.WaitAll(pumps.ToArray());
            if (attributes != 0)
            {
                if (attributesReady)
                    DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (handles != 0)
                Marshal.FreeHGlobal(handles);
            if (job != 0)
                CloseHandle(job);
            if (port != 0)
                CloseHandle(port);
        }
    }

    private static string Quote(string value) => "\"" + System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"").TrimEnd('\\') + new string('\\', value.Reverse().TakeWhile(c => c == '\\').Count() * 2) + "\"";
    private static void Win(bool success)
    {
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X, Y;
    }

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint pseudo);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint pseudo);
    [StructLayout(LayoutKind.Sequential)]
    private struct Security
    {
        public int Length;
        public nint Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Association
    {
        public nint Key, Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long User, Kernel, PeriodUser, PeriodKernel;
        public uint Faults, Total, Active, Terminated;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public short Show, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupEx
    {
        public Startup Info;
        public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out nint read, out nint write, ref Security security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(string name, uint access, uint share, ref Security security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateIoCompletionPort(nint file, nint port, nuint key, uint concurrency);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int type, ref Association association, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(nint job, int type, ref Accounting accounting, uint size, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatus(nint port, out uint bytes, out nuint key, out nint overlapped, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string executable, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(nint job, uint code);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
