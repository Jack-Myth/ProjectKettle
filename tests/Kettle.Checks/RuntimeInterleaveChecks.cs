using Kettle.Core;
using System.Threading.Channels;

internal static class RuntimeInterleaveChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (int chunkSize in new[] { 1, 7, 4096 })
        {
            var terminal = new InterruptedEchoTerminal(chunkSize);
            await using var session = new SteamSession(() => terminal, false);
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            var snapshot = await AppsRunningCommand.ReadAsync(session, TimeSpan.FromSeconds(2));
            check(snapshot.Apps.Length == 0 && session.CommandChannel.CanRead, "exit notice interrupting command echo completes without timeout: " + chunkSize);
            check((await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(2))).Contains("next response"), "interrupted echo does not poison next command: " + chunkSize);
        }
        var inlineData = "description apps_rGame process removed: AppID 42 x, ProcID 101\nunning\n";
        check(GameNoticeFilter.Process(inlineData).Cleaned == "description apps_running\n", "complete events are removed independently of command prefixes");
        var malformed = "apps_rGame process removed: AppID 42 missing pid\nunning\nSteam>";
        check(GameNoticeFilter.Process(malformed).Cleaned == malformed, "malformed inline notice cannot manufacture a command echo");
        const string response = "apps_running\n  \"path\" \"C:\\Games\\game.exe\"\n12345\nSteam>";
        const string notice = "Game process removed: AppID 42 x, ProcID 101\n";
        for (int position = 0; position <= response.Length; position++)
        {
            var interrupted = response.Insert(position, notice);
            check(GameNoticeFilter.Process(interrupted, preserveNoticePrompts: true).Cleaned == response, "event at arbitrary response offset restores original content: " + position);
            var eventParser = new GameEventParser();
            var delivered = new List<GameRuntimeEvent>();
            foreach (char c in interrupted) delivered.AddRange(eventParser.Feed(c.ToString()));
            check(delivered.Count == 1 && delivered[0].ProcessId == 101, "event at arbitrary offset is delivered exactly once: " + position);
        }
        var repeated = "ap" + notice + "ps_r" + notice + "unning\nSteam>";
        check(GameNoticeFilter.Process(repeated).Cleaned == "apps_running\nSteam>", "multiple events interrupting one echo are independently removed");
        var inlineParser = new GameEventParser();
        var inlineEvents = new List<GameRuntimeEvent>();
        foreach (char c in repeated) inlineEvents.AddRange(inlineParser.Feed(c.ToString()));
        check(inlineEvents.Count == 2 && inlineEvents.All(e => e is { AppId: 42, ProcessId: 101, Kind: GameEventKind.Removed }), "inline events are delivered once each across character chunks");
        var ordinary = "\"description\" \"Game process removed: AppID 42 x, ProcID 101\"\n";
        check(GameNoticeFilter.Process(ordinary).Cleaned == ordinary, "quoted ordinary text without complete event termination is preserved");
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
            "inside-field",
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

    private sealed class InterruptedEchoTerminal(int chunkSize) : ISteamTerminal
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
            // Recorded Winlator layout: asynchronous exit notice between r and u,
            // complete empty response and prompts already present before timeout.
            var text = command switch
            {
                "apps_running" => "apps_r\x1b[?25lGame process removed: AppID 635940 \"\"D:\\Steam\\game.exe\"\", ProcID 1340\x1b[K\r\n\x1b[?25hunning\r\n\r\nSteam>\r\n\r\nSteam>",
                "" => "\r\nSteam>",
                _ when command.StartsWith("login ") => "Logged in OK\r\nSteam>",
                _ => command + "\r\nnext response\r\nSteam>"
            };
            for (int i = 0; i < text.Length; i += chunkSize)
                output.Writer.TryWrite(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
            return Task.CompletedTask;
        }
        public Task StopAsync() { output.Writer.TryComplete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { output.Writer.TryComplete(); return ValueTask.CompletedTask; }
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
                    "inside-field" => command + "\r\nSteam>\r\n" + body.Replace("4200", "42" + notices.Replace("Steam>", "") + "00"),
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
