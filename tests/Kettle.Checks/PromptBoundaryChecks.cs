using System.Threading.Channels;
using Kettle.Core;

internal static class PromptBoundaryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var terminal = new Terminal();
        await using var session = new SteamSession(() => terminal, false, recoveryTimeout: TimeSpan.FromMilliseconds(100));
        await session.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => session.Current.NeedsInput);
        check(terminal.Writes.SequenceEqual(new[] { "login fixture" }), "prompt boundary never appends blank to login command or unknown password prompt");
        await session.SubmitAsync("fixture-secret".AsMemory());
        await SessionChecks.Until(() => session.IsAuthenticated);
        check(terminal.Writes.SequenceEqual(new[] { "login fixture", "fixture-secret" }), "password response is sent exactly once without blank or fence");
        var one = AppStatusReader.ReadAsync(42, session);
        await SessionChecks.Until(() => terminal.StatusReads == 1);
        var two = AppStatusReader.ReadAsync(43, session);
        await Task.Delay(50);
        check(!one.IsCompleted && terminal.StatusReads == 1, "extra Steam prompts cannot complete partial UserConfig or release queued request");
        var first = await one;
        var second = await two;
        check(first.SizeOnDiskBytes == 4200 && second.SizeOnDiskBytes == 4300 && terminal.Fences == 0, "two sequential asynchronous statuses each require matching AppID and complete response plus trailing blank");
        check(!terminal.Writes.Any(s => s.Contains("fixture-secret\r")), "normal-command blank path never changes submitted credential text");
        var running = await session.QueryMetadataAsync(["apps_running"], TimeSpan.FromSeconds(3));
        check(running.Contains("ProcessID 99") && !running.Contains("Steam>") && !running.Contains("__kettle_boundary_"), "late busy table during fence is retained while duplicate prompts and own nonce are removed");
        var empty = await session.QueryMetadataAsync(["apps_running"], TimeSpan.FromSeconds(3));
        check(string.IsNullOrWhiteSpace(empty) && terminal.Fences == 0, "no-output command requires its own command echo and prompt and returns truly empty payload");
        var before = terminal.Blanks;
        bool failed = false;
        try
        {
            await AppInfoCommand.ReadAsync(99, session);
        }
        catch (InvalidDataException)
        {
            failed = true;
        }

        check(failed && terminal.Blanks == before + 1, "declared noninteractive command receives its requested CR at send; complete unsupported response fails in the business adapter");
    }

    private sealed class Terminal : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool SupportsPromptBoundaries => true;

        public List<string> Writes = [];
        public int StatusReads, Fences, Blanks, RunningReads;
        private bool lateBusy, statusPending, blankPending;
        private void Emit(string text)
        {
            for (int at = 0; at < text.Length; at += 3)
                output.Writer.TryWrite(text.Substring(at, Math.Min(3, text.Length - at)));
        }

        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            var command = line.ToString();
            Writes.Add(command);
            if (command.StartsWith("login "))
                Emit("Password:");
            else if (command == "fixture-secret")
                Emit("\r\nLogged in OK\r\nSteam>");
            else if (command.Length == 0)
            {
                Blanks++;
                if (statusPending)
                    blankPending = true;
                else
                    Emit("\r\nSteam>Steam>");
            }
            else if (command.StartsWith("find __kettle_boundary_"))
            {
                Fences++;
                Emit(command + "\r\n" + (lateBusy ? HybridRunChecks.Row(12, 99) : "") + (Fences == 2 ? "Steam>Game process removed: AppID 43 \"long-path\r\ncontinued\", ProcID 888888 \r\n" : "") + "ConVars:\r\nCommands:\r\nSteam>");
                lateBusy = false;
            }
            else if (command.StartsWith("app_status "))
            {
                statusPending = true;
                StatusReads++;
                var id = uint.Parse(command[11..]);
                Emit("Steam>" + command + "\r\nSteam>\r\nAppID " + id + "\r\n - user config: \"UserConfig\"\r\n{");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    Emit("\r\n}\r\n" + " - size on disk: " + id * 100 + " bytes\r\n");
                    statusPending = false;
                    if (blankPending)
                    {
                        blankPending = false;
                        Emit("\r\nSteam>");
                    }
                });
            }
            else if (command == "apps_running")
            {
                RunningReads++;
                Emit(command + "\r\n" + (RunningReads == 1 ? HybridRunChecks.Row(12, 99) : "") + "Steam>");
            }
            else if (command == "app_info_print 99")
                Emit(command + "\r\nContinue? [y/n]");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
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
