using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace Kettle.Core;
/// <summary>Windows console API compatible hidden terminal. stdout/stderr are merged by ConPTY, decoded as UTF-8.</summary>
public sealed class ConPtyTerminal : ISteamTerminal
{
    public const short Columns = 4096;
    private readonly Channel<string> output = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim writes = new(1, 1);
    private FileStream? input, reader;
    private nint pseudo, process, job;
    private Task? pump;
    private Task? stopping;
    private readonly object stopLock = new();
    public int ProcessId
    {
        get; private set;
    }
    public uint ExitCode { get; private set; } = 259;
    public bool SupportsPromptBoundaries => true;

    private string? configuredExecutable;
    public SessionProcessSafety InspectProcesses()
    {
        lock (stopLock)
        {
            if (job == 0)
                return new(true, L.Text(TextId.TheSessionHasEnded));
            var memory = Marshal.AllocHGlobal(8 + 1024 * IntPtr.Size);
            try
            {
                if (!QueryInformationJobObject(job, 3, memory, (uint)(8 + 1024 * IntPtr.Size), out _))
                    return new(false, L.Text(TextId.CouldNotReadSessionProcessesStayConnectedAndExit));
                var assigned = Marshal.ReadInt32(memory);
                var count = Marshal.ReadInt32(memory, 4);
                if (assigned != count || count < 0 || count > 1024)
                    return new(false, L.Text(TextId.TheSessionProcessListIsIncompleteTheSessionWas));
                for (int i = 0; i < count; i++)
                {
                    int pid = checked((int)Marshal.ReadIntPtr(memory, 8 + i * IntPtr.Size));
                    try
                    {
                        using var child = System.Diagnostics.Process.GetProcessById(pid);
                        var path = child.MainModule?.FileName;
                        if (child.HasExited)
                            continue;
                        if (pid == ProcessId && string.Equals(path, Path.Combine(AppContext.BaseDirectory, "Kettle.CoreSession.exe"), StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.Equals(path, configuredExecutable, StringComparison.OrdinalIgnoreCase))
                            continue;
                        return new(false, L.Text(TextId.GameOrHelperProcessesAreStillRunningInThis) + pid + L.Text(TextId.ExitNormallyAndTryAgain));
                    }
                    catch (ArgumentException)
                    {
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        return new(false, L.Text(TextId.CouldNotConfirmSessionProcessesHaveExitedTheSession));
                    }
                }

                return new(true, L.Text(TextId.OnlySteamCMDAndCoreSessionRemainInThis));
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }
    }

    public Task StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        configuredExecutable = Path.GetFullPath(executable);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException(L.Text(TextId.ConPTYRequiresWindowsVersionOrLater));
        if (process != 0)
            throw new InvalidOperationException(L.Text(TextId.TerminalHasAlreadyStarted));
        nint inRead = 0, inWrite = 0, outRead = 0, outWrite = 0, attributes = 0;
        bool attributesReady = false;
        try
        {
            Win(CreatePipe(out inRead, out inWrite, 0, 0));
            Win(CreatePipe(out outRead, out outWrite, 0, 0));
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord { X = Columns, Y = 80 }, inRead, outWrite, 0, out pseudo));
            nuint bytes = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal((nint)bytes);
            Win(InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            attributesReady = true;
            Win(UpdateProcThreadAttribute(attributes, 0, (nuint)0x00020016, pseudo, (nuint)IntPtr.Size, 0, 0));
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>()
                },
                Attributes = attributes
            };
            job = CreateJobObjectW(0, null);
            Win(job != 0);
            var limits = new JobLimits
            {
                Basic = new BasicLimits
                {
                    LimitFlags = 0x2000
                }
            }; // KILL_ON_JOB_CLOSE, owned children only
            Win(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
            var host = Path.Combine(AppContext.BaseDirectory, "Kettle.CoreSession.exe");
            if (!File.Exists(host)) throw new IOException("Kettle.CoreSession.exe is missing.");
            var commandLine = new StringBuilder(string.Join(" ", new[] { host, "--terminal", executable }.Concat(arguments).Select(Quote)));
            Win(CreateProcessW(host, commandLine, 0, 0, false, 0x00080004, 0, Path.GetDirectoryName(executable), ref startup, out var info));
            process = info.Process;
            ProcessId = (int)info.ProcessId;
            try
            {
                Win(AssignProcessToJobObject(job, process));
                if (ResumeThread(info.Thread) == uint.MaxValue)
                    Win(false);
            }
            finally
            {
                CloseHandle(info.Thread);
            }

            input = new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write);
            inWrite = 0;
            reader = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read);
            outRead = 0;
            // Synchronous ConPTY pipes must be serviced independently of the UI/input thread.
            pump = Task.Run(async () =>
            {
                var buffer = new byte[1024];
                var chars = new char[1024];
                var decoder = Encoding.UTF8.GetDecoder();
                try
                {
                    while (true)
                    {
                        var length = reader.Read(buffer);
                        if (length == 0)
                            break;
                        var size = decoder.GetChars(buffer, 0, length, chars, 0, false);
                        try
                        {
                            await output.Writer.WriteAsync(new string(chars, 0, size));
                        }
                        catch (ChannelClosedException)
                        { /* keep draining the native pipe during shutdown */
                        }

                        Array.Clear(buffer);
                        Array.Clear(chars);
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or ChannelClosedException)
                {
                }
                finally
                {
                    Array.Clear(buffer);
                    Array.Clear(chars);
                    output.Writer.TryComplete();
                }
            });
            // Close the pseudoconsole after the owned root exits, while its output is still drained.
            _ = Task.Run(() =>
            {
                WaitForSingleObject(process, uint.MaxValue);
                if (GetExitCodeProcess(process, out var code))
                    ExitCode = code;
                _ = StopAsync();
            });
            return Task.CompletedTask;
        }
        catch (Exception error)
        {
            if (process != 0)
                TerminateProcess(process, 1);
            if (job != 0)
            {
                CloseHandle(job);
                job = 0;
            }

            if (pseudo != 0)
            {
                ClosePseudoConsole(pseudo);
                pseudo = 0;
            }

            if (process != 0)
            {
                CloseHandle(process);
                process = 0;
            }

            throw new InvalidOperationException(L.Text(TextId.CouldNotCreateTheHiddenSteamCMDTerminalSystemCode) + (error is Win32Exception native ? native.NativeErrorCode : error.HResult) + "）。");
        }
        finally
        {
            foreach (var handle in new[]
            {
                inRead,
                inWrite,
                outRead,
                outWrite
            }

            )
                if (handle != 0)
                    CloseHandle(handle);
            if (attributes != 0)
            {
                if (attributesReady)
                    DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    public IAsyncEnumerable<string> ReadAsync(CancellationToken cancellationToken) => output.Reader.ReadAllAsync(cancellationToken);
    public async Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken cancellationToken)
    {
        await writes.WaitAsync(cancellationToken);
        byte[]? bytes = null;
        try
        {
            if (input is null)
                throw new IOException(L.Text(TextId.TerminalIsNotConnected));
            bytes = new byte[Encoding.UTF8.GetByteCount(line.Span) + 1];
            var count = Encoding.UTF8.GetBytes(line.Span, bytes);
            bytes[count] = 13;
            await input.WriteAsync(bytes, cancellationToken);
            await input.FlushAsync(cancellationToken);
        }
        finally
        {
            if (bytes is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            writes.Release();
        }
    }

    public Task StopAsync()
    {
        lock (stopLock)
            return stopping ??= StopCore();
    }

    private async Task StopCore()
    {
        // Discard queued raw output during teardown so the native drain can never block on an abandoned consumer.
        output.Writer.TryComplete();
        // Closing our Job handle would terminate every descendant, including a game.
        // Keep draining the console and wait for non-control children to exit naturally.
        while (job != 0 && !InspectProcesses().CanDisconnect)
            await Task.Delay(250);
        if (job != 0)
        {
            CloseHandle(job);
            job = 0;
        }

        var ownedPseudo = Interlocked.Exchange(ref pseudo, 0);
        if (ownedPseudo != 0)
            await Task.Run(() => ClosePseudoConsole(ownedPseudo));
        input?.Dispose();
        if (pump is not null)
        {
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
            }
        }

        reader?.Dispose();
        if (process != 0)
        {
            if (WaitForSingleObject(process, 5000) != 0)
                throw new IOException(L.Text(TextId.TheSessionHasNotEndedRetryLogout));
            CloseHandle(process);
            process = 0;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
    private static void Win(bool success)
    {
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static string Quote(string value) => "\"" + System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"").TrimEnd('\\') + new string('\\', value.Reverse().TakeWhile(c => c == '\\').Count() * 2) + "\"";
    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public nint reserved, desktop, title;
        public uint x, y, xSize, ySize, xCountChars, yCountChars, fill, dwFlags;
        public short wShowWindow, reserved2;
        public nint reservedBytes, stdin, stdout, stderr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out nint read, out nint write, nint attributes, uint size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint pseudo);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint pseudo);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(nint handle, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int type, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(nint job, int type, nint data, uint size, out uint returned);
}
