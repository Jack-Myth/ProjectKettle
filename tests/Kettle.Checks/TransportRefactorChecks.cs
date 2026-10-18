using System.Text.Json;
using System.Threading.Channels;
using Kettle.Core;

internal static class TransportRefactorChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string publicInfo = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "steam-app-info-2806050.txt"));
        using var recorded = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recorded-status-response.json")));
        foreach (int chunk in new[]
        {
            1,
            2,
            7,
            4096,
            -1
        }

        )
        {
            var terminal = new Terminal(chunk)
            {
                Response = publicInfo
            };
            await using var session = new SteamSession(() => terminal, false);
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            var text = await session.QueryMetadataAsync(["app_info_print 2806050"], TimeSpan.FromSeconds(3));
            check(text.Contains("AppID : 2806050, change number"), "transport preserves the complete real informational header: " + chunk);
            check(AppInfoCommand.Parse(2806050, text).Data["2806050"] is not null && terminal.Blanks == 1, "business adapter accepts recorded public metadata after prompt framing: " + chunk);
            terminal.WholeResponse = recorded.RootElement.GetProperty("Raw").GetString();
            text = await session.QueryMetadataAsync(["app_status 2806050"], TimeSpan.FromSeconds(3));
            check(text.Contains("<unclassified chars=") && text.Contains("size on disk: 79550108673"), "recorded status diagnostic, including unknown content, is delivered without content-based waiting");
            terminal.WholeResponse = null;
            terminal.Response = "left\r\nSteam>Game process removed: AppID 42 x, ProcID 99 \r\nright\r\n";
            int events = 0;
            session.GameEventReceived += _ => events++;
            text = await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(3));
            check(text.Contains("left") && text.Contains("right") && !text.Contains("Game process") && events == 1, "event demultiplexing preserves all non-event text across arbitrary blocks");
            terminal.Response = "Password:\r\nInvalid Password\r\nLogged in OK\r\nSteam Guard code:";
            text = await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(3));
            check(text.Contains("Invalid Password") && session.IsAuthenticated && session.Current.State == LoginState.Authenticated, "ordinary complete output never re-enters the interactive authentication parser");
        }

        var held = new Terminal(1)
        {
            Hold = true
        };
        await using (var session = new SteamSession(() => held, false))
        {
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            var read = session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(3));
            await SessionChecks.Until(() => held.Blanks == 1);
            check(!read.IsCompleted && held.Writes.SequenceEqual(new[] { "login fixture", "info", "" }), "one extra CR is sent immediately before any response, never after content validation");
            held.Release("unrecognized complete payload\r\nSteam>");
            check((await read).Contains("unrecognized complete payload"), "transport returns arbitrary complete text without a business grammar");
        }

        foreach (string command in new[]
        {
            "app_info_print 42",
            "app_status 42",
            "apps_running",
            "apps_installed",
            "library_folder_list"
        }

        )
        {
            var terminal = new Terminal(3)
            {
                Response = "unsupported complete response"
            };
            await using var session = new SteamSession(() => terminal, false);
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            bool rejected = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (command.StartsWith("app_info"))
                    await AppInfoCommand.ReadAsync(42, session);
                else if (command.StartsWith("app_status"))
                    await AppStatusReader.ReadAsync(42, session);
                else if (command == "apps_running")
                    await AppsRunningCommand.ReadAsync(session, TimeSpan.FromSeconds(3));
                else if (command == "apps_installed")
                    await InstalledAppsCommand.ReadAsync(session);
                else
                    await LibraryFoldersCommand.ReadAsync(session);
            }
            catch (InvalidDataException error)
            {
                rejected = !string.IsNullOrWhiteSpace(error.Message);
            }

            check(rejected && clock.Elapsed < TimeSpan.FromSeconds(2) && session.CommandChannel.CanRead, "complete malformed " + command + " reports a parse error immediately without blocking transport");
            terminal.Response = "next response";
            check((await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(3))).Contains("next response"), "the next command succeeds after business parse failure: " + command);
        }

        // Deliberate protocol-contract counterexample, NOT a recorded real SteamCMD stream.
        var late = new Terminal(7)
        {
            Response = "Steam>\r\nAppID 42\r\n - user config: \"UserConfig\"\r\n{\r\n"
        };
        await using (var session = new SteamSession(() => late, false))
        {
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            bool rejected = false;
            try
            {
                await AppStatusReader.ReadAsync(42, session);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            check(rejected && session.CommandChannel.CanRead, "an incomplete object inside a completed prompt frame fails in business, never waits for business fields");
            late.Release("}\r\n - size on disk: 4200 bytes\r\n");
            await Task.Delay(250);
            int sent = late.Writes.Count;
            bool blocked = false;
            try
            {
                await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(2));
            }
            catch (SteamCommandChannelException)
            {
                blocked = true;
            }

            check(blocked && late.Writes.Count == sent, "data arriving after the completed prompt frame remains unclaimed and prevents contamination of the next request");
        }
    }

    private sealed class Terminal(int chunk) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        private readonly Random random = new(8123);
        public string Response = "fixture";
        public string? WholeResponse;
        public bool Hold;
        public int Blanks;
        public List<string> Writes = [];
        private void Emit(string text)
        {
            for (int at = 0; at < text.Length;)
            {
                int size = Math.Min(text.Length - at, chunk < 0 ? random.Next(1, 32) : chunk);
                output.Writer.TryWrite(text.Substring(at, size));
                at += size;
            }
        }

        public void Release(string text) => Emit(text);
        public Task StartAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken token)
        {
            string command = value.ToString();
            Writes.Add(command);
            if (command.StartsWith("login "))
                Emit("Logged in OK\r\nSteam>");
            else if (command.Length == 0)
            {
                Blanks++;
                Emit("\r\nSteam>Steam>");
            }
            else if (WholeResponse is not null)
                Emit(WholeResponse);
            else
            {
                string echo = "\x1b[97m" + command[0] + "\r\nSteam>" + command[1..] + "\x1b[m\r\n";
                Emit(echo);
                if (!Hold)
                    Emit(Response + "\r\nSteam>Steam>");
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
