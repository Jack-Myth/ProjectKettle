using System.Text.Json;
using System.Threading.Channels;
using Kettle.Core;

internal static class OutputPreprocessingChecks
{
    private static string Normalize(string value) => value.Replace("\r", "");
    private static string Notice => $"Steam>Steam>Game process removed: AppID 1030300 \"\"{Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe")}\" 1030300 C:\\fixture\\\r\nselected 220538249\", ProcID 49324 \r\n";

    public static async Task Run(Action<bool, string> check, string root)
    {
        var row = HybridRunChecks.Row(1030300, 49324, Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe")).Replace("parent: 5", "parent: 53944").Replace("12345", "132822");
        var historical = AppsRunningSnapshot.Parse(row);
        check(historical.Apps.Length == 1 && !historical.HasGame(1030300), "historical 1030300 host row shape parses without claiming it is a game; not a reproduction of lost original snapshot");
        foreach (var position in new[]
        {
            "before",
            "middle",
            "after"
        }

        )
        {
            var text = position switch
            {
                "before" => Notice + row,
                "middle" => row.Replace("   extra info", Notice + "   extra info"),
                _ => row + Notice
            };
            var clean = GameNoticeFilter.Process(Normalize(text));
            var parsed = AppsRunningSnapshot.Parse(clean.Cleaned);
            check(clean.Notices.Length == 1 && clean.Notices[0].Event.IsAuxiliary && !clean.Incomplete && parsed.Apps.Length == 1 && parsed.Apps[0].AssociatedProcesses.Contains(222222), position + " complete wrapped host exit notice is separated without deleting row or child PIDs");
        }

        var parser = new GameEventParser();
        var events = new List<GameRuntimeEvent>();
        foreach (var c in "\x1b[97m" + Notice + "\x1b[0m" + Notice)
            events.AddRange(parser.Feed(c.ToString()));
        check(events.Count == 2 && events.All(e => e.Kind == GameEventKind.Removed && e.ProcessId == 49324), "shared grammar emits each complete repeated notice once across single-character chunks and ANSI; existing monitor deduplicates hints");
        foreach (var text in new[]
        {
            "Game process removed: AppID 1030300 \"unfinished\n" + row,
            "Game process removed: AppID 1030300 \"unfinished\n   associated PIDs ( 7 {parent: 1}, )\n, ProcID 7\n",
            "Game process removed: AppID 1030300 \"unfinished\nSteam>\n",
            "Game process removed: AppID 1030300 x, ProcID 0\n"
        }

        )
        {
            var clean = GameNoticeFilter.Process(Normalize(text));
            bool rejected = false;
            try
            {
                AppsRunningSnapshot.Parse(clean.Cleaned);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            check(clean.Incomplete && clean.Notices.Length == 0 && rejected, "partial or malformed typed notice cannot swallow table fields or become empty snapshot");
        }

        var opaque = "unexpected AppID 1030300 PID 7 Game output\n";
        check(GameNoticeFilter.Process(opaque).Cleaned == opaque, "unrecognized app/game/PID words remain command data");
        var bare = GameNoticeFilter.Process(Normalize(Notice));
        check(string.IsNullOrWhiteSpace(bare.Cleaned) && bare.Notices.Length == 1, "complete notification alone has no snapshot data; command boundary still required");
        var folder = Path.Combine(root, "output-preprocessing");
        var terminal = new Terminal();
        await using var session = new SteamSession(() => terminal, false, diagnosticDirectory: folder);
        var observed = new List<GameRuntimeEvent>();
        session.GameEventReceived += e =>
        {
            lock (observed)
                observed.Add(e);
        };
        await session.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        for (int round = 1; round <= 2; round++)
        {
            terminal.Mode = "running";
            var output = await session.QueryMetadataAsync(["apps_running"], TimeSpan.FromSeconds(3));
            check(AppsRunningSnapshot.Parse(output).HasGame(42) && !output.Contains("Game process"), "round " + round + " framed busy snapshot survives middle-of-table CloudHost exit event and duplicate prompts");
            var status = await AppStatusReader.ReadAsync(42, session);
            check(status.SizeOnDiskBytes == 4200, "round " + round + " app_status retains fields around interleaved wrapped events");
            terminal.Mode = "empty";
            await new SteamGameBackend(session, HybridRunChecks.Fast).WaitIdleAsync(42, default);
            check(terminal.EmptyReads == round * 3, "round " + round + " empty state still requires three new framed snapshots despite duplicate exit notices");
        }

        check(observed.Count >= 12 && session.RecentCommandDiagnostics.Length <= 8, "notifications reach existing event stream; bounded diagnostics retain classification and boundary state");
        var evidence = session.RecentCommandDiagnostics.Last();
        check(evidence.Raw.Contains("Game process") && !evidence.Cleaned.Contains("Game process") && evidence.Notices.Length > 0 && evidence.ExtraBlankRequested && evidence.Payload is not null, "diagnostic exposes distinct raw, cleaned and returned payload with notice classification and trailing blank");
        terminal.Mode = "unknown";
        bool unknown = false;
        try
        {
            await AppsRunningCommand.ReadAsync(session, TimeSpan.FromSeconds(2));
        }
        catch (InvalidDataException)
        {
            unknown = true;
        }

        check(unknown && session.CommandChannel.CanRead && File.Exists(session.CommandDiagnosticsPath), "unrecognized complete body fails in business parser and persists evidence without poisoning transport queue");
        var report = await File.ReadAllTextAsync(session.CommandDiagnosticsPath);
        using var json = JsonDocument.Parse(report);
        check(json.RootElement.GetProperty("Stage").GetString() == "response-parse-failed" && !report.Contains("fixture-password") && !report.Contains("fixture-token") && !report.Contains("fixture-cookie") && report.Length < 200000, "failure report is bounded and excludes credential-like echoes and unknown private values");
        var partial = new Terminal
        {
            Mode = "partial"
        };
        await using var partialSession = new SteamSession(() => partial, false, diagnosticDirectory: folder);
        await partialSession.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => partialSession.IsAuthenticated);
        bool timeout = false;
        try
        {
            await AppsRunningCommand.ReadAsync(partialSession, TimeSpan.FromSeconds(2));
        }
        catch (InvalidDataException)
        {
            timeout = true;
        }

        var failure = await File.ReadAllTextAsync(session.CommandDiagnosticsPath);
        check(timeout && failure.Contains("response-parse-failed") && failure.Contains("\"IncompleteNotice\":true"), "unfinished notice in a complete prompt frame remains raw unknown business data, with an accurate parse failure and incomplete-notice diagnosis");
    }

    private sealed class Terminal : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool SupportsPromptBoundaries => true;

        public string Mode = "running";
        public int EmptyReads;
        private void Emit(string text)
        {
            for (int i = 0; i < text.Length; i += 5)
                output.Writer.TryWrite(text.Substring(i, Math.Min(5, text.Length - i)));
        }

        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> input, CancellationToken token)
        {
            var command = input.ToString();
            if (command.StartsWith("login "))
                Emit("Logged in OK\r\nSteam>");
            else if (command.Length == 0)
                Emit("\r\nSteam>Steam>");
            else if (command.StartsWith("find __kettle_boundary_"))
                Emit(command + "\r\nConVars:\r\nCommands:\r\nSteam>" + Notice);
            else if (command.StartsWith("app_status "))
                Emit(command + "\r\nSteam>" + StatusFixture.Body(42, 4200).Replace(" - user config:", Notice + " - user config:"));
            else if (command == "apps_running")
            {
                string body = Mode switch
                {
                    "running" => HybridRunChecks.Row(42, 111111).Replace("   extra info", Notice + "   extra info"),
                    "unknown" => "unknown AppID 42 PID 7 Game\r\nopaque fixture-password\r\nToken: fixture-token\r\nfixture-cookie\r\n",
                    "partial" => "Game process removed: AppID 1030300 \"unfinished\r\n",
                    _ => Notice + Notice
                };
                if (Mode == "empty")
                    EmptyReads++;
                Emit(command + "\r\n" + body + "Steam>Steam>");
            }

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
