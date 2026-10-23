using System.Text.Json;
using System.Threading.Channels;
using Kettle.Core;

internal static class LaunchErrorChecks
{
    public static async Task Run(Action<bool, string> check, string root)
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
            bool rejected = false;
            try
            {
                await session.LaunchAppCommandAsync(2806050, responseTimeout: TimeSpan.FromSeconds(3), launchOptionIndex: 7);
            }
            catch (GameLaunchRejectedException e)
            {
                rejected = e.Message.Contains("Dependency failure");
            }

            check(rejected && session.IsAuthenticated && session.CommandChannel.CanRead, "single-prompt Dependency failure ends launch without disconnect: " + chunk);
            check(terminal.LastLaunch == "app_launch 2806050 7", "original launch index is sent to SteamCMD");
            check((await session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(3))).Contains("next response"), "next request works after launch rejection");
            terminal.Body = "unsupported complete launch body";
            bool malformed = false;
            try
            {
                await session.LaunchAppCommandAsync(2806050, responseTimeout: TimeSpan.FromSeconds(3));
            }
            catch (InvalidDataException)
            {
                malformed = true;
            }

            check(malformed && session.CommandChannel.CanRead, "complete unknown launch response reports parse failure and releases request");
            using var recorded = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "early-prompt-app-status-2806050.json")));
            terminal.Raw = recorded.RootElement.GetProperty("Raw").GetString();
            var response = await session.QueryMetadataAsync(["app_status 2806050"], TimeSpan.FromSeconds(3));
            check(response.Contains("size on disk: 79550108673") && session.CommandChannel.CanRead, "recorded early prompt with body after it completes without trailing marker: " + chunk);
        }

    }

    private sealed class Terminal(int chunk) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public string Body = "ERROR! AppId 2806050 launch failed (Dependency failure)";
        public string? Raw;
        public string? LastLaunch;
        private void Emit(string text)
        {
            for (int i = 0; i < text.Length; i += chunk)
                output.Writer.TryWrite(text.Substring(i, Math.Min(chunk, text.Length - i)));
        }

        public Task StartAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            string c = line.ToString();
            if (c.StartsWith("login "))
                Emit("Logged in OK\r\nSteam>");
            else if (c.StartsWith("app_launch"))
            {
                LastLaunch = c;
                Emit(c + "\r\n" + Body + "\r\nSteam>");
            }
            else if (c.Length > 0)
                Emit(Raw ?? c + "\r\nnext response\r\nSteam>");
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
