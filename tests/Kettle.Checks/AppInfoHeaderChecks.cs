using System.Threading.Channels;
using Kettle.Core;

internal static class AppInfoHeaderChecks
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
            var terminal = new Terminal("normal", chunk);
            await using var session = new SteamSession(() => terminal, false);
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            string result = await session.QueryMetadataAsync(["app_info_print 2806050"], TimeSpan.FromSeconds(5));
            check(result.Contains("change number") && AppInfoCommand.Parse(2806050, result).Vdf.TrimStart().StartsWith("\"2806050\""), "observed AppID header is accepted and excluded from returned VDF: " + chunk);
            check(AppInfoCommand.Parse(2806050, result).Data["2806050"]?["common"]?["name"]?.Value == "Synthetic game", "full large app metadata remains parseable: " + chunk);
            check(terminal.Blanks == 1 && session.CommandChannel.CanRead, "app info retains ordinary trailing-blank acknowledgement: " + chunk);
            check((await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(3))).Contains("next-response"), "app info header does not contaminate the next command");
        }

        foreach (string mode in new[]
        {
            "wrong-header",
            "wrong-object",
            "incomplete",
            "unknown-prefix",
            "unknown-tail"
        }

        )
        {
            var terminal = new Terminal(mode, 4096);
            await using var session = new SteamSession(() => terminal, false, recoveryTimeout: TimeSpan.FromMilliseconds(150));
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.IsAuthenticated);
            bool rejected = false;
            try
            {
                await AppInfoCommand.ReadAsync(2806050, session);
            }
            catch (InvalidDataException error)
            {
                rejected = error.Message.Contains("app_info_print 2806050 响应解析失败");
            }

            check(rejected && terminal.Blanks == 1 && session.CommandChannel.CanRead, "business parse failure returns immediately with transport queue intact: " + mode);
        }
    }

    private sealed class Terminal(string mode, int chunk) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public int Blanks;
        public bool SupportsPromptBoundaries => true;

        private void Emit(string text)
        {
            for (int i = 0; i < text.Length; i += chunk)
                output.Writer.TryWrite(text.Substring(i, Math.Min(chunk, text.Length - i)));
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
            if (command.StartsWith("login "))
                Emit("Logged in OK\r\nSteam>");
            else if (command == "app_info_print 2806050")
            {
                string header = "AppID : " + (mode == "wrong-header" ? "42" : "2806050") + ", change number : 39947850/0, last change : Sun Oct 18 04:52:45 2026 \r\n";
                string body = "\"" + (mode == "wrong-object" ? "42" : "2806050") + "\"\r\n{\r\n\"common\" { \"name\" \"Synthetic game\" }\r\n\"padding\" \"" + new string('x', 27000) + "\"\r\n" + (mode == "incomplete" ? "" : "}\r\n");
                Emit(command + "\r\n" + (mode == "unknown-prefix" ? "unexpected\r\n" : "") + header + body + (mode == "unknown-tail" ? "unexpected\r\n" : "") + "Steam>");
            }
            else if (command == "")
            {
                Blanks++;
                Emit("\r\nSteam>");
            }
            else
                Emit(command + "\r\nnext-response\r\nSteam>");
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
