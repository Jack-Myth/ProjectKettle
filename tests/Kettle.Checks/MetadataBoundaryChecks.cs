using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kettle.Core;

internal static class MetadataBoundaryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var terminal = new BoundaryFixture();
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var read = session.QueryMetadataAsync(["apps_running"], TimeSpan.FromSeconds(3));
        await SessionChecks.Until(() => terminal.Reads == 1);
        await Task.Delay(190);
        check(!read.IsCompleted, "partial wrapped runtime notice after prompt is not command-ready evidence");
        terminal.CompleteNotice();
        var result = await read;
        check(result.Contains("ProcessID 77") && !result.Contains("Game process removed") && !result.Contains("cloud-host"), "complete asynchronous notice is isolated without discarding busy apps_running result");
        await SteamIdleWait.WaitAsync(session, 12, default);
        check(terminal.Reads == 4, "idle requires three fresh empty results after earlier native context occupancy");
        var next = await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(2));
        check(next.Contains("fixture-info") && !next.Contains("ProcessID"), "metadata channel remains reusable after asynchronous notice boundary");
        foreach (var mode in new[]
        {
            "unknown-tail",
            "notification-only-prompt"
        }

        )
        {
            var negative = new BoundaryFixture(mode);
            await using var unknown = new SteamSession(() => negative, false);
            await unknown.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => unknown.IsAuthenticated);
            bool refused = false;
            try
            {
                var snapshot = await AppsRunningCommand.ReadAsync(unknown, TimeSpan.FromMilliseconds(700));
                refused = mode == "notification-only-prompt" && snapshot.Apps.Length == 0;
            }
            catch (InvalidDataException)
            {
                refused = true;
            }
            catch (TimeoutException)
            {
                refused = true;
            }

            check(refused, mode + " follows prompt-only framing; unknown content is a business error and complete events are isolated");
        }
    }

    private sealed class BoundaryFixture(string mode = "wrapped") : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public int Reads;
        public bool SupportsPromptBoundaries => true;

        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            output.Writer.TryWrite("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public void CompleteNotice()
        {
            output.Writer.TryWrite("\\steamcmd-test 123\", ProcID 77\x1b[K\r\n");
            if (blankPending)
            {
                blankPending = false;
                output.Writer.TryWrite("\r\nSteam>");
            }
        }

        private bool blankPending;
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            var command = line.ToString().Trim();
            if (command.Length == 0 && mode == "wrapped" && Reads == 1)
            {
                blankPending = true;
                return Task.CompletedTask;
            }

            if (command.StartsWith("login "))
                output.Writer.TryWrite("Logged in OK\r\nSteam>");
            else if (command == "apps_running")
            {
                Reads++;
                if (mode == "unknown-tail")
                    output.Writer.TryWrite("apps_running\r\nSteam>\r\nunknown asynchronous text\r\n");
                else if (mode == "notification-only-prompt")
                    output.Writer.TryWrite("apps_running\r\nSteam>Game process removed: AppID 12 x, ProcID 77 \r\n");
                else if (Reads == 1)
                    output.Writer.TryWrite("apps_running\r\n" + RunningAppFixture.Row(12, 77) + "Steam>\rSteam>Game process removed: AppID 12 \"\"C:\\long path\\cloud-host\\Kettle.CloudHost.exe\" 12 C:\\selected\\\r\n");
                else
                    output.Writer.TryWrite("apps_running\r\nSteam>");
            }
            else
                output.Writer.TryWrite(command.Length == 0 ? "\r\nSteam>" : command + "\r\nfixture-info\r\nSteam>");
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
