using System.Diagnostics;
using System.Text;
using Kettle.Core;

internal static class StartupUpdateNativeChecks
{
    public static void Fixture(string mode)
    {
        if (mode is "bootstrap" or "bootstrap-fail" or "bootstrap-hold")
        {
            var startup = new Startup
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<Startup>(),
                Flags = 0x100,
                Input = GetStdHandle(-10),
                Output = GetStdHandle(-11),
                Error = GetStdHandle(-12)
            };
            var command = new StringBuilder("\"" + Environment.ProcessPath! + "\" --update-fixture " + (mode == "bootstrap-hold" ? "hold" : mode == "bootstrap" ? "child" : "fail-child"));
            if (!CreateProcessW(Environment.ProcessPath!, command, 0, 0, true, GetConsoleMode(GetStdHandle(-11), out _) ? 0u : 0x08000000u, 0, null, ref startup, out var child))
                throw new System.ComponentModel.Win32Exception();
            CloseHandle(child.Process);
            CloseHandle(child.Thread);
            return;
        }

        if (mode == "control-hold")
        {
            Thread.Sleep(6000);
            return;
        }

        if (mode == "fail")
        {
            Environment.ExitCode = 7;
            return;
        }

        Thread.Sleep(150);
        void Emit(Stream stream, string text)
        {
            foreach (byte value in Encoding.UTF8.GetBytes(text))
            {
                stream.WriteByte(value);
                stream.Flush();
                Thread.Sleep(1);
            }
        }

        Emit(Console.OpenStandardOutput(), "\x1b[32m[ 37%] 正在下载中文文件\x1b[0m\r");
        if (mode == "hold")
            Thread.Sleep(15000);
        if (mode == "live")
            Thread.Sleep(2500);
        Emit(Console.OpenStandardError(), "[----] 正在验证安装…\r\n");
        Thread.Sleep(300);
        Emit(Console.OpenStandardOutput(), "[100%] 更新完成\n");
        if (mode == "fail-child")
            Environment.ExitCode = 9;
    }

    public static async Task Cancel(Action<bool, string> check)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--update-fixture");
        start.ArgumentList.Add("control-hold");
        using var unrelated = Process.Start(start)!;
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new SteamUpdateProgressParser(p =>
        {
            if (p.Percent == 37)
                started.TrySetResult();
        });
        var task = WindowsUpdateProcess.RunAsync(Environment.ProcessPath!, ["--update-fixture", "bootstrap-hold"], (_, text, end) => parser.Feed(text, end), true, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();
        cancel.Cancel();
        bool canceled = false;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        long cancelMilliseconds = clock.ElapsedMilliseconds;
        check(canceled && cancelMilliseconds < 2000, "cancel terminates private bootstrap tree promptly rather than waiting 15 seconds");
        check(!unrelated.HasExited, "unrelated process outside the update Job is not terminated");
        var restart = await WindowsUpdateProcess.RunAsync(Environment.ProcessPath!, ["--update-fixture", "child"], (_, _, _) =>
        {
        }, true);
        check(restart.Success, "a new update can run normally immediately after cancellation");
        using var beforeStart = new CancellationTokenSource();
        beforeStart.Cancel();
        bool prevented = false;
        try
        {
            await WindowsUpdateProcess.RunAsync(Environment.ProcessPath!, [], (_, _, _) =>
            {
            }, true, beforeStart.Token);
        }
        catch (OperationCanceledException)
        {
            prevented = true;
        }

        check(prevented, "already-cancelled update starts no process");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Canceled = canceled,
            CancelMilliseconds = cancelMilliseconds,
            Restart = restart.Success,
            UnrelatedSurvived = true
        }));
        await unrelated.WaitForExitAsync();
    }

    public static async Task Live(Action<bool, string> check)
    {
        var clock = Stopwatch.StartNew();
        var events = new System.Collections.Concurrent.ConcurrentQueue<object>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new SteamUpdateProgressParser(p =>
        {
            events.Enqueue(new
            {
                Milliseconds = clock.ElapsedMilliseconds,
                p.Percent,
                p.Text
            });
            first.TrySetResult();
        });
        var task = WindowsUpdateProcess.RunAsync(Environment.ProcessPath!, ["--update-fixture", "live"], (stream, text, finished) =>
        {
            if (stream == 0)
                parser.Feed(text, finished);
        });
        await Task.WhenAny(first.Task, Task.Delay(2000));
        bool live = first.Task.IsCompleted && !task.IsCompleted;
        var result = await task.WaitAsync(TimeSpan.FromSeconds(12));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            LiveBeforeExit = live,
            result,
            Events = events.ToArray()
        }));
        check(live && result.Success, "progress is delivered before updater exit without waiting for 1024 characters");
    }

    public static async Task Run(Action<bool, string> check, bool pseudoConsole = false)
    {
        foreach (var mode in new[]
        {
            "child",
            "bootstrap",
            "fail",
            "bootstrap-fail"
        }

        )
        {
            var updates = new List<SteamUpdateProgress>();
            var gate = new object();
            var parsers = new[]
            {
                new SteamUpdateProgressParser(p =>
                {
                    lock (gate)
                        updates.Add(p);
                }),
                new SteamUpdateProgressParser(p =>
                {
                    lock (gate)
                        updates.Add(p);
                })
            };
            var raw = new System.Text.StringBuilder();
            var clock = Stopwatch.StartNew();
            var task = WindowsUpdateProcess.RunAsync(Environment.ProcessPath!, ["--update-fixture", mode], (stream, text, finished) =>
            {
                lock (gate)
                    raw.Append(text);
                parsers[stream].Feed(text, finished);
            }, pseudoConsole: pseudoConsole);
            if (mode != "fail")
            {
                await Task.Delay(180);
                check(!task.IsCompleted, "bootstrap root exit does not finish while child updates: " + mode);
            }

            var result = await task.WaitAsync(TimeSpan.FromSeconds(12));
            check(result.Success == (mode is "bootstrap" or "child"), "root and child nonzero exit fail closed: " + mode);
            Console.WriteLine(mode + " " + System.Text.Json.JsonSerializer.Serialize(new
            {
                result,
                Updates = updates,
                Raw = raw.ToString()
            }));
            check(result.Processes >= (mode is "fail" or "child" ? 1 : 2) && result.ExitCodes.Length == result.Processes, "all owned processes tracked before completion: " + mode);
            if (mode != "fail")
            {
                check(clock.ElapsedMilliseconds >= 450 && updates.Any(u => u.Percent == 37 && u.Text == "正在下载中文文件") && updates.Any(u => u.Percent is null && u.Text == "正在验证安装…"), "independent UTF8 stream decoders preserve split Chinese and CR refresh: " + mode);
                check(updates.Any(u => u.Percent == 100 && u.Text == "更新完成"), "all output drained before completion: " + mode);
            }
        }

        bool failed = false;
        try
        {
            failed = !(await WindowsUpdateProcess.RunAsync("Z:\\absent\\steamcmd.exe", ["+quit"], (_, _, _) =>
            {
            }, pseudoConsole: pseudoConsole)).Success;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            failed = true;
        }

        check(failed, "cannot-start failure is reported without entering login");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct Startup
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public short Show, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process, Thread;
        public uint Id, ThreadId;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint handle, out uint mode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int kind);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string executable, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref Startup startup, out ProcessInfo process);
}
