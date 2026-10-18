using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Kettle.Core;

internal static class PidWrapChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pty-wrapped-pid.json")));
        var raw = file.RootElement.GetProperty("Raw").GetString()!;
        var normalized = raw.Replace("\x1b[K", "").Replace("\r", "");
        // The native generated fixture yields 4215 + 2. The preserved real diagnostic
        // showed 421 + orphan 2; the old recording did not preserve its raw controls.
        var observed = normalized.Replace("4215\n", "421\n");
        using var wideFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pty-wide-notice.json")));
        var wideRaw = wideFile.RootElement.GetProperty("Raw").GetString()!;
        var wideNormalized = wideRaw.Replace("\x1b[K", "").Replace("\r", "");
        foreach (var sample in new[]
        {
            (Text: normalized, Columns: 240),
            (Text: observed, Columns: 240),
            (Text: wideNormalized, Columns: (int)ConPtyTerminal.Columns)
        }

        )
        {
            var text = sample.Text;
            var clean = GameNoticeFilter.Process(text + "Steam>", terminalColumns: sample.Columns);
            check(clean.Notices.Length == 1 && !clean.Notices[0].IdentityReliable && clean.Notices[0].Event.ProcessId == 0 && !Regex.IsMatch(clean.Cleaned, @"(?m)^\s*2\s*$"), "recorded-width generated/incident-shaped PID split is consumed as one notice without guessing a process identity");
            var parser = new GameEventParser(sample.Columns);
            var events = new List<GameRuntimeEvent>();
            foreach (char c in text + "Steam>")
                events.AddRange(parser.Feed(c.ToString()));
            check(events.Count == 0, "character chunks never emit a short/wrong PID before numeric continuation arrives; polling remains authoritative");
            foreach (var executable in new[]
            {
                "C:\\Fixture\\game.exe",
                Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe")
            }

            )
            {
                var row = HybridRunChecks.Row(1030300, 42152, executable);
                var snapshot = AppsRunningSnapshot.Parse(row + clean.Cleaned.Replace("Steam>", ""));
                check(snapshot.Apps.Length == 1 && snapshot.Apps[0].AssociatedProcesses.Contains(222222) && snapshot.HasGame(1030300) == !executable.Contains("Kettle.CloudHost.exe"), "active game/child and auxiliary occupancy survive filtered notification without false empty snapshot");
            }
        }

        var partial = new GameEventParser();
        check(partial.Feed("Game process removed: AppID 42 x, ProcID 421\r\n").Length == 0, "PID at a physical line end is not emitted as a complete identity without a logical terminator");
        check(partial.Feed("Steam>").Single().ProcessId == 421, "known subsequent prompt completes an unwrapped PID without losing valid event hints");
        var opaque = GameNoticeFilter.Process("Game process removed: AppID 42 x, ProcID 42152 \n2\nSteam>");
        bool rejected = false;
        try
        {
            AppsRunningSnapshot.Parse(opaque.Cleaned.Replace("Steam>", ""));
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        check(rejected && opaque.Cleaned.Contains("2"), "integer following a genuinely terminated notice remains unknown data, never broadly discarded");
        var shortLine = GameNoticeFilter.Process("Game process removed: AppID 42 x, ProcID 421\n2\nSteam>");
        check(shortLine.Cleaned.Contains("2"), "numeric-only line is not presumed a wrap away from the physical terminal column edge");
        using var shortFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pty-4096-short.json")));
        var shortRaw = shortFile.RootElement.GetProperty("Raw").GetString()!;
        var shortNormalized = shortRaw.Replace("\x1b[K", "").Replace("\r", "");
        check(ConPtyTerminal.Columns == 4096 && GameNoticeFilter.Process(shortNormalized + "Steam>").Notices.Single() is { IdentityReliable: true, Event.ProcessId: 42152 }, "native former 240-column edge remains one complete reliable PID at the production width");
        check(GameNoticeFilter.Process(normalized + "Steam>").Cleaned.Contains("2"), "old 240-column shape is not silently accepted as the current 4096-column boundary");
        var terminal = new Terminal(wideRaw);
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        foreach (var chunkSize in new[]
        {
            1,
            7,
            4096
        }

        )
        {
            terminal.ChunkSize = chunkSize;
            terminal.Busy = true;
            var result = await session.QueryMetadataAsync(["apps_running"], TimeSpan.FromSeconds(3));
            check(AppsRunningSnapshot.Parse(result).HasGame(1030300), "production capture/fence/normalizer accepts generated raw VT sample with chunk size " + chunkSize);
            var evidence = session.RecentCommandDiagnostics.Last();
            check(evidence.TerminalColumns == 4096 && evidence.UncertainNoticeIdentities == 1 && evidence.Raw.Contains("ProcID 4215") && evidence.RawChunkLayouts.Any(c => c.Layout.Contains('\r') || c.Layout.Contains('\n')), "diagnostic preserves identity fragments, chunk offsets and controls");
        }

        terminal.Busy = false;
        var before = terminal.Reads;
        await new SteamGameBackend(session, HybridRunChecks.Fast).WaitIdleAsync(1030300, default);
        check(terminal.Reads == before + 3, "empty framed snapshots with wrapped notifications still require three fresh confirmations");
    }

    private sealed class Terminal(string notice) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public int ChunkSize = 7, Reads;
        public bool Busy;
        public bool SupportsPromptBoundaries => true;

        private void Emit(string value)
        {
            for (int i = 0; i < value.Length; i += ChunkSize)
                output.Writer.TryWrite(value.Substring(i, Math.Min(ChunkSize, value.Length - i)));
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
            else if (command == "apps_running")
            {
                Reads++;
                Emit(command + "\r\n" + (Busy ? HybridRunChecks.Row(1030300, 42152) : "") + notice + "Steam>");
            }
            else if (command.StartsWith("find __kettle_boundary_"))
                Emit(command + "\r\nConVars:\r\nCommands:\r\nSteam>");
            else
                Emit("\r\nSteam>");
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
