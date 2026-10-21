using System.Threading.Channels;
using Kettle.Core;

internal static class CommandRetryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var terminal = new Terminal();
        await using var session = new SteamSession(() => terminal, false, recoveryTimeout: TimeSpan.FromMilliseconds(100));
        await session.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var result = await session.QueryMetadataAsync(["apps_running"], TimeSpan.FromMilliseconds(700));
        check(string.IsNullOrWhiteSpace(result) && terminal.Reads == 2 && terminal.Blanks == 3,
            "broken read echo recovers with an extra blank then retries once");
        terminal.Reads = 0;
        terminal.AlwaysBroken = true;
        bool timedOut = false;
        try { await session.QueryMetadataAsync(["apps_running"], TimeSpan.FromMilliseconds(700)); }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut && terminal.Reads == 3, "read retries stop after two retries");

        var silent = new Terminal { SuppressBlanks = true };
        await using var silentSession = new SteamSession(() => silent, false, recoveryTimeout: TimeSpan.FromMilliseconds(100));
        await silentSession.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => silentSession.IsAuthenticated);
        timedOut = false;
        try { await silentSession.QueryMetadataAsync(["apps_running"], TimeSpan.FromMilliseconds(700)); }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut && silent.Reads == 1, "no fresh prompt after recovery blank means no retry");

        var launchTerminal = new Terminal();
        await using var launchSession = new SteamSession(() => launchTerminal, false);
        await launchSession.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => launchSession.IsAuthenticated);
        await launchSession.LaunchAppCommandAsync(42, responseTimeout: TimeSpan.FromMilliseconds(700));
        check(launchTerminal.Launches == 1 && launchSession.CommandChannel.CanRead,
            "launch with missing echo accepts prompt without resending");
        launchTerminal.Reads = 1;
        await launchSession.QueryMetadataAsync(["apps_running"], TimeSpan.FromMilliseconds(700));
        check(launchTerminal.Reads == 2, "running-state query proceeds after launch without echo");
    }

    private sealed class Terminal : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool SupportsPromptBoundaries => true;
        public int Reads, Blanks, Launches;
        public bool AlwaysBroken, SuppressBlanks;
        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            output.Writer.TryWrite("Steam>");
            return Task.CompletedTask;
        }
        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> input, CancellationToken token)
        {
            var command = input.ToString();
            if (command.StartsWith("login "))
                output.Writer.TryWrite("Logged in OK\r\nSteam>");
            else if (command.Length == 0)
            {
                Blanks++;
                if (!SuppressBlanks)
                    output.Writer.TryWrite("\r\nSteam>");
            }
            else if (command == "apps_running")
            {
                Reads++;
                output.Writer.TryWrite((AlwaysBroken || Reads == 1
                    ? "aIPC client is in my process, could/should be using an in-process pipe.ppsIPC server is in my process - could/should be using an in process pipe\r\n_running"
                    : command) + "\r\nSteam>");
            }
            else if (command.StartsWith("app_launch "))
            {
                Launches++;
                output.Writer.TryWrite("\r\nSteam>");
            }
            return Task.CompletedTask;
        }
        public Task StopAsync() { output.Writer.TryComplete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { output.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
