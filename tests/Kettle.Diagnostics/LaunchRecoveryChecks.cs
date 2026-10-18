using System.Reflection;
using System.Threading.Channels;
using Kettle;
using Kettle.Core;

internal static class LaunchRecoveryChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var native = new Terminal(true);
        await using var session = new SteamSession(() => native, false);
        await session.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var launch = session.LaunchAppCommandAsync(12, responseTimeout: TimeSpan.FromMilliseconds(60), waitForNativeLifetime: true);
        await SessionChecks.Until(() => native.LaunchSent);
        await Task.Delay(220);
        check(!launch.IsCompleted && session.CommandChannel.CanRead && native.Commands.All(c => c != "apps_running"), "explicit legacy native-lifetime observation retains launch command; normal production backend is tested separately");
        native.Emit($"Game process removed: AppID 12 x, ProcID {Environment.ProcessId} \r\nSteam>");
        await launch.WaitAsync(TimeSpan.FromSeconds(2));
        check(session.CommandChannel.CanRead && !native.Stopped, "real prompt drains native launch after game event instead of inventing a timeout");
        check(!session.InspectDisconnectSafety().CanDisconnect, "removed notification alone cannot permit disconnect while observed PID remains alive");
        var absent = new Terminal(false);
        await using var lost = new SteamSession(() => absent, false, recoveryTimeout: TimeSpan.FromMilliseconds(80));
        await lost.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => lost.IsAuthenticated);
        try
        {
            await lost.LaunchAppCommandAsync(12, responseTimeout: TimeSpan.FromMilliseconds(60));
        }
        catch (TimeoutException)
        {
        }

        await SessionChecks.Until(() => lost.CommandChannel.NeedsReconnect);
        check(lost.CommandChannel.LaunchResponseTail?.Contains("app_launch 12") == true && lost.CommandChannel.LaunchResponseTail.Contains("<unclassified chars=") && !lost.CommandChannel.LaunchResponseTail.Contains("native pending response"), "failed native launch preserves command and unknown-output fingerprint without copying private unknown text");
        check(!lost.InspectDisconnectSafety().CanDisconnect && !absent.Stopped, "unknown or active owned process snapshot forbids disconnect without terminating anything");
        var home = Path.Combine(root, "launch-recovery-ui");
        Directory.CreateDirectory(home);
        var exe = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(exe, "fixture");
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--diagnose-launch", "--steamcmd", exe]), new(lost, "fixture", exe), a => a(), home, new EmptyHeaders());
        // Seed the durable failure shape without executing real launch or save backup APIs.
        typeof(MainViewModel).GetField("diagnosticHold", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
        typeof(MainViewModel).GetField("diagnosticReport", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, Path.Combine(home, "report.json"));
        check(vm.EndDiagnosticCommand.CanExecute(null) && !vm.ReconnectCommand.CanExecute(null) && !vm.ShowReconnect && !vm.ConnectionIssue.Contains("点击重新登录"), "lost diagnostic exposes an enabled safe recovery action and never instructs a disabled relogin");
        int logouts = 0, confirmations = 0;
        vm.ConfirmEndDiagnostic = () =>
        {
            confirmations++;
            return Task.FromResult(true);
        };
        vm.LogoutRequested += () =>
        {
            logouts++;
            return Task.CompletedTask;
        };
        vm.EndDiagnosticCommand.Execute(null);
        await Task.Delay(30);
        check(logouts == 0 && confirmations == 0 && !absent.Stopped, "unsafe diagnostic recovery explains process state without confirming or disconnecting");
        absent.Safe = true;
        vm.EndDiagnosticCommand.Execute(null);
        await SessionChecks.Until(() => logouts == 1);
        check(confirmations == 1 && File.ReadAllText(Path.Combine(home, "report.json")).Contains("\"GameEndConfirmed\":false"), "explicit safe manual recovery preserves failure rather than fabricating game end or upload");
    }

    private sealed class Terminal(bool active) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool LaunchSent, Safe, Stopped;
        public List<string> Commands = [];
        public SessionProcessSafety InspectProcesses() => new(Safe, Safe ? "Only fixture SteamCMD remains" : "本会话仍有未确认进程，未断开连接。");
        public void Emit(string text) => output.Writer.TryWrite(text);
        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            var command = line.ToString();
            Commands.Add(command);
            if (command.StartsWith("login "))
                Emit("Logged in OK\r\nSteam>");
            else if (command.StartsWith("app_launch "))
            {
                LaunchSent = true;
                Emit(command + (active ? "\r\n" : "\r\nnative pending response\r\n") + (active ? $"Game process added: AppID 12 x, ProcID {Environment.ProcessId} \r\n" : ""));
            }
            else if (command.Length > 0 || !LaunchSent)
                Emit(command + "\r\nSteam>");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            output.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            StopAsync();
            return ValueTask.CompletedTask;
        }
    }
}
