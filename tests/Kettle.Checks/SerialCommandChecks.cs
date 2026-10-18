using System.Threading.Channels;
using Kettle.Core;

internal static class SerialCommandChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (int chunk in new[]
        {
            1,
            7,
            4096
        }

        )
        {
            var terminal = new Terminal(chunk);
            await using var session = new SteamSession(() => terminal, false);
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            var running = AppsRunningSnapshot.Parse(await session.QueryMetadataAsync(["apps_running"], TimeSpan.FromSeconds(3)));
            check(running.HasGame(2806050) && running.Apps[0].AssociatedProcesses.Contains(222222), "split command echo/ANSI retains active table and child with chunk " + chunk);
            check(terminal.Blanks == 1 && terminal.Commands.All(c => !c.StartsWith("find __")), "one ordinary command gets exactly one trailing blank and no generated fence");
            var batch = await session.QueryMetadataAsync(["app_info_print 42", "app_info_print 43"], TimeSpan.FromSeconds(3));
            check(batch.Contains("\"42\"") && batch.Contains("\"43\"") && terminal.MaxInFlight == 1 && terminal.Blanks == 3, "metadata batch runs one command at a time including complete trailing-blank cleanup");
            var status = await AppStatusReader.ReadAsync(42, session);
            check(status.SizeOnDiskBytes == 4200 && terminal.Blanks == 4, "delayed response without completion marker retains the full status until its prompt");
            terminal.Empty = true;
            var reads = terminal.Reads;
            await new SteamGameBackend(session, HybridRunChecks.Fast with
            {
                CommandTimeout = TimeSpan.FromSeconds(3),
                ReleaseTimeout = TimeSpan.FromSeconds(5)
            }).WaitIdleAsync(2806050, default);
            check(terminal.Reads == reads + 3, "three fresh empty snapshots remain required without nonce");
        }

        var challenge = new Terminal(3)
        {
            Confirm = true
        };
        await using var blocked = new SteamSession(() => challenge, false, recoveryTimeout: TimeSpan.FromMilliseconds(100));
        await blocked.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => blocked.IsAuthenticated);
        bool rejected = false;
        try
        {
            await AppInfoCommand.ReadAsync(42, blocked);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        check(rejected && challenge.Blanks == 1 && blocked.CommandChannel.CanRead, "complete unsupported response is a business parse error, not a transport timeout or blocked queue");
        var unknown = new Terminal(7);
        await using var unknownSession = new SteamSession(() => unknown, false);
        await unknownSession.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => unknownSession.IsAuthenticated);
        await unknownSession.ExecuteCommandAsync("custom_unknown", TimeSpan.FromSeconds(3));
        check(unknown.Blanks == 1, "explicitly requested ordinary command gets one CR without inspecting body text");
        var historical = new Terminal(1)
        {
            OldFind = true
        };
        await using var historicalSession = new SteamSession(() => historical, false, recoveryTimeout: TimeSpan.FromMilliseconds(100));
        await historicalSession.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => historicalSession.IsAuthenticated);
        bool oldRejected = false;
        try
        {
            await historicalSession.QueryMetadataAsync(["apps_running"], TimeSpan.FromMilliseconds(600));
        }
        catch (TimeoutException)
        {
            oldRejected = true;
        }

        check(oldRejected && historical.Blanks == 1, "historical f/CR/LF/Steam>ind redraw reply is unknown data, cannot confirm an unrelated current command");
        var idle = new Terminal(3);
        await using var idleSession = new SteamSession(() => idle, false);
        await idleSession.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => idleSession.IsAuthenticated);
        await idleSession.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(3));
        idle.EmitUnknown();
        var before = idle.Commands.Count;
        bool refused = false;
        try
        {
            await idleSession.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(2));
        }
        catch (SteamCommandChannelException)
        {
            refused = true;
        }

        check(refused && idle.Commands.Count == before, "unclaimed late body is retained and blocks the next send instead of being flushed");
    }

    private sealed class Terminal(int chunk) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool SupportsPromptBoundaries => true;

        public readonly List<string> Commands = [];
        public int Blanks, Reads, InFlight, MaxInFlight;
        public bool Empty, Confirm, OldFind;
        private void Emit(string value)
        {
            lock (output)
                for (int i = 0; i < value.Length; i += chunk)
                    output.Writer.TryWrite(value.Substring(i, Math.Min(chunk, value.Length - i)));
        }

        public void EmitUnknown() => Emit("unclaimed late result\r\nSteam>");
        public Task StartAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        private bool statusPending;
        public Task WriteLineAsync(ReadOnlyMemory<char> input, CancellationToken token)
        {
            string command = input.ToString();
            Commands.Add(command);
            if (command.StartsWith("login "))
            {
                Emit("Logged in OK\r\nSteam>");
                return Task.CompletedTask;
            }

            if (command.Length == 0)
            {
                Blanks++;
                InFlight--;
                if (!statusPending)
                    Emit("\r\nSteam>Steam>");
                // Already-completed command's delayed extra prompts must be consumed
                // in idle cleanup, never become the next command's result.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    if (!statusPending)
                        Emit("\r\nSteam>Steam>");
                });
                return Task.CompletedTask;
            }

            InFlight++;
            MaxInFlight = Math.Max(MaxInFlight, InFlight);
            string echo = "\x1b[97m" + command[0] + "\r\x1b[m\nSteam>" + command[1..] + "\r\n";
            if (command == "apps_running")
            {
                Reads++;
                Emit((OldFind ? "f\r\x1b[m\nSteam>ind __kettle_boundary_historical\r\nConVars:\r\nCommands:\r\n" : "") + echo + (Empty ? "" : HybridRunChecks.Row(2806050, 38140)) + "Steam>");
            }
            else if (command.StartsWith("app_info_print "))
            {
                if (Confirm)
                    Emit(echo + "Continue? [y/n]");
                else
                    Emit(echo + "\"" + command.Split(' ')[1] + "\" { \"common\" { \"name\" \"fixture\" } }\r\nSteam>");
            }
            else if (command.StartsWith("app_status "))
            {
                statusPending = true;
                Emit(echo + "AppID 42\r\n - user config: \"UserConfig\"\r\n{");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(350);
                    statusPending = false;
                    Emit("\r\n}\r\n - size on disk: 4200 bytes\r\nSteam>");
                });
            }
            else
                Emit(echo + "fixture-info\r\nSteam>");
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
