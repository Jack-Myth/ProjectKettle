using Kettle.Core;
using System.Threading.Channels;

internal static class RuntimeInterleaveChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var parser = new GameEventParser();
        var parsed = new List<GameRuntimeEvent>();
        var wrapped = "\x1b[97mSteam>Game process added : AppID 42 \"C:\\some long\\\r\npath\\game.exe\", ProcID 101, IP 0.0.0.0:0\x1b[K\r\nGame process updated : AppID 42 x, ProcID 102 \r\nGame process removed: AppID 42 x, ProcID 101 \r\n";
        foreach (var c in wrapped)
            parsed.AddRange(parser.Feed(c.ToString()));
        check(parsed.Count == 3 && parsed[0].ProcessId == 101 && parsed[1].Kind == GameEventKind.Updated && parsed[2].Kind == GameEventKind.Removed, "runtime events survive character chunks, ANSI, folded paths and multiple records");
        var helper = Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe");
        var auxiliary = parser.Feed($"Game process added : AppID 42 \"{helper}\", ProcID 999 \r\n");
        check(auxiliary.Single().IsAuxiliary, "own CloudHost events are identified separately from game process hints");
        foreach (var position in new[]
        {
            "before-echo",
            "inside-status",
            "after-status"
        }

        )
        {
            var terminal = new MixedTerminal(position);
            await using var session = new SteamSession(() => terminal, false);
            var observed = new System.Collections.Concurrent.ConcurrentBag<GameRuntimeEvent>();
            session.GameEventReceived += observed.Add;
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            var value = await AppStatusReader.ReadAsync(42, session);
            check(value.SizeOnDiskBytes == 4200 && observed.Count == 2, position + " retains complete app_status and independent events in shared chunks");
            var next = await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(2));
            check(next.Contains("next response") && !next.Contains("Game process"), position + " does not leak prior notification fragments into next response");
        }
    }

    private sealed class MixedTerminal(string position) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool SupportsPromptBoundaries => true;

        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            output.Writer.TryWrite("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            var command = line.ToString().Trim();
            if (command.StartsWith("login "))
                output.Writer.TryWrite("Logged in OK\r\nSteam>");
            else if (command.StartsWith("app_status "))
            {
                var notices = "Steam>Game process added : AppID 42 \"C:\\folded\\\r\npath\\game.exe\", ProcID 101 \r\nGame process removed: AppID 42 x, ProcID 101 \r\n";
                var body = StatusFixture.Body(42, 4200);
                var text = position switch
                {
                    "before-echo" => notices + command + "\r\nSteam>\r\n" + body,
                    "inside-status" => command + "\r\nSteam>\r\n" + body.Replace(" - user config:", notices + " - user config:"),
                    _ => command + "\r\nSteam>\r\n" + body + notices
                };
                for (int i = 0; i < text.Length; i += 7)
                    output.Writer.TryWrite(text.Substring(i, Math.Min(7, text.Length - i)));
            }
            else
                output.Writer.TryWrite(command.Length == 0 ? "\r\nSteam>" : command + "\r\nnext response\r\nSteam>");
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
