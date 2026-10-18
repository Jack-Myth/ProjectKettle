using Kettle.Core;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

internal static class SessionChecks
{
    public static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    public static async Task Run(Action<bool, string> check)
    {
        var parser = new SteamProtocol();
        var parsed = new List<SteamSignal>();
        foreach (var c in "\x1b[32mPass\x1b[0mword:")
            parsed.AddRange(parser.Feed(c.ToString()));
        check(parsed.Contains(SteamSignal.Password), "chunked ANSI password prompt without newline");
        parser.Reset();
        parsed.Clear();
        foreach (var part in new[]
        {
            "\x1b]0;private title",
            "\aSteam Gu",
            "ard code:"
        }

        )
            parsed.AddRange(parser.Feed(part));
        check(parsed.Contains(SteamSignal.GuardCode), "chunked guard prompt and OSC title ignored");
        parser.SuppressInputEcho();
        check(!parser.Feed("Logged in successfully").Contains(SteamSignal.Success), "input echo cannot forge successful login");
        check(parser.Feed("\r\nLogged in successfully").Contains(SteamSignal.Success), "server success recognized after input line");
        foreach (var scenario in new[]
        {
            "success",
            "guard",
            "mobile",
            "failure",
            "exit",
            "unknown",
            "timeout",
            "throw"
        }

        )
        {
            var fake = new FakeTerminal(scenario);
            await using var session = new SteamSession(() => fake, false, TimeSpan.FromMilliseconds(scenario == "timeout" ? 150 : 3000));
            var events = new List<LoginUpdate>();
            session.Updated += update =>
            {
                lock (events)
                    events.Add(update);
            };
            await session.StartAsync("fixture.exe", "fixture_user");
            if (scenario is "success" or "guard" or "mobile" or "failure" or "throw")
            {
                await Until(() => session.Current.State == LoginState.Password);
                await session.SubmitAsync("fixture-sensitive-input".AsMemory());
                if (scenario == "guard")
                {
                    await Until(() => session.Current.State == LoginState.GuardCode);
                    await session.SubmitAsync("fixture-code".AsMemory());
                }
            }

            var expected = scenario switch
            {
                "success" or "guard" or "mobile" => LoginState.Authenticated,
                "failure" or "throw" => LoginState.Failed,
                "exit" => LoginState.Exited,
                "unknown" => LoginState.Unknown,
                _ => LoginState.TimedOut
            };
            await Until(() => session.Current.State == expected);
            check(session.Current.State == expected, "fake process scenario: " + scenario);
            lock (events)
                check(events.All(e => !e.Message.Contains("fixture-sensitive-input") && !e.Message.Contains("fixture-code") && !e.Message.Contains("脱敏") && !e.Message.Contains("原文") && !e.Message.Contains("REDACTED")), "player-friendly messages without input leaks: " + scenario);
            if (scenario == "success")
            {
                await Until(() => fake.Writes > 1);
                await Task.Delay(20);
                await Task.WhenAll(session.ExecuteCommandAsync("app_status 42", TimeSpan.FromSeconds(2)), session.ExecuteCommandAsync("app_status 43", TimeSpan.FromSeconds(2)));
                check(fake.MaxConcurrentWrites == 1, "future commands serialize on one authenticated session");
            }

            if (scenario == "unknown")
            {
                check(!session.IsAuthenticated, "unknown and Steam prompt cannot authenticate");
                check(string.IsNullOrEmpty(session.Current.Message), "unhelpful unknown prompt has no player message");
                await session.CancelAsync();
                check(fake.Stopped, "cancel stops owned terminal");
            }
        }
    }
}

internal sealed class FakeTerminal(string scenario) : ISteamTerminal
{
    private readonly Channel<string> output = Channel.CreateUnbounded<string>();
    public int Writes, MaxConcurrentWrites;
    private int concurrent;
    public bool Stopped;
    public Task StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        output.Writer.TryWrite("Steam>");
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var value in output.Reader.ReadAllAsync(token))
            yield return value;
    }

    public async Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
    {
        MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, Interlocked.Increment(ref concurrent));
        try
        {
            if (line.IsEmpty)
            {
                output.Writer.TryWrite("\r\nSteam>");
                return;
            }

            await Task.Delay(5, token);
            Writes++;
            if (Writes == 1)
            {
                if (scenario == "username")
                {
                    output.Writer.TryWrite("Username:");
                    return;
                }

                if (scenario == "timeout")
                    return;
                if (scenario == "exit")
                {
                    output.Writer.TryComplete();
                    return;
                }

                if (scenario == "unknown")
                {
                    output.Writer.TryWrite("Unrecognized choice? Steam>");
                    return;
                }

                output.Writer.TryWrite("Logging in using username/password.\r\nPass");
                output.Writer.TryWrite("word:");
            }
            else if (line.Span.StartsWith("app_status "))
            {
                var id = uint.Parse(line.Span[11..]);
                output.Writer.TryWrite(line.ToString() + "\r\n" + StatusFixture.Body(id, id * 100) + "\r\nSteam>");
            }
            else if (scenario == "throw")
                throw new IOException("fixture-sensitive-input");
            else if (scenario == "failure")
                output.Writer.TryWrite("\r\nFAILED (Invalid Password)");
            else if (scenario == "guard" && Writes == 2)
            {
                output.Writer.TryWrite("fixture-sensitive-input\r\nSteam Gu");
                output.Writer.TryWrite("ard code:");
            }
            else if (scenario == "mobile" && Writes == 2)
            {
                output.Writer.TryWrite("\r\nPlease confirm the login in your Steam app.\r\n");
                await Task.Delay(30, token);
                output.Writer.TryWrite("Logged in OK\r\nSteam>");
            }
            else if (Writes <= 3)
                output.Writer.TryWrite("fixture-code\r\nLogging in user 'fixture_user' to Steam Public...OK\r\nWaiting for user info...OK\r\nSteam>");
            else
                output.Writer.TryWrite("Command complete\r\nSteam>");
        }
        finally
        {
            Interlocked.Decrement(ref concurrent);
        }
    }

    public Task StopAsync()
    {
        Stopped = true;
        output.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
