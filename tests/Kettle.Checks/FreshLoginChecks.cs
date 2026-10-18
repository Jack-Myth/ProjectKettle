using System.Threading.Channels;
using Kettle.Core;

internal static class FreshLoginChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var parser = new SteamProtocol();
        parser.SuppressInputEcho("Logged in OK".AsSpan());
        check(!parser.Feed("Logged in OK\r\n").Contains(SteamSignal.Success), "exact secret echo cannot forge login success");
        parser.Reset();
        parser.SuppressInputEcho("fixture-secret".AsSpan());
        check(parser.Feed("Logging in user 'fixture' to Steam Public...OK\r\n").Contains(SteamSignal.Accepted), "non-echoed secret does not discard first server status line");
        parser.Reset();
        check(!parser.Feed("ERROR! Package Info request returned error Timeout\r\n").Contains(SteamSignal.Failure), "metadata error is not an authentication rejection");
        foreach (int chunk in new[]
        {
            1,
            7,
            4096
        }

        )
            foreach (string mode in new[]
            {
                "cached",
                "fresh"
            }

            )
            {
                var terminal = new Terminal(mode, chunk);
                await using var session = new SteamSession(() => terminal, false);
                var updates = new List<LoginUpdate>();
                session.Updated += updates.Add;
                await session.StartAsync("fixture.exe", "fixture");
                if (mode == "fresh")
                {
                    await SessionChecks.Until(() => session.Current.State == LoginState.Password);
                    await session.SubmitAsync("fixture-secret".AsMemory());
                    await SessionChecks.Until(() => session.Current.State == LoginState.GuardCode);
                    await session.SubmitAsync("fixture-code".AsMemory());
                }

                await SessionChecks.Until(() => session.Current.Message == "获取账户配置较慢，请稍候…");
                check(!session.IsAuthenticated && !terminal.Stopped, "accepted login survives config warning without fabricating success: " + mode + "/" + chunk);
                check(terminal.Blanks == 0 && terminal.LoginWrites == 1 && terminal.InputWrites == (mode == "fresh" ? 2 : 0), "no extra blank, repeated login, or remember-password answer during authentication");
                check(updates.Any(u => u.Message == "正在获取账户配置…"), "config acquisition is visible before its warning");
                terminal.BeginInfo();
                await SessionChecks.Until(() => session.Current.Message.Contains("正在获取用户信息"));
                terminal.Warn();
                await Task.Delay(30);
                check(session.Current.Message.Contains("正在获取用户信息"), "next phase replaces slow config message and late warning cannot restore it");
                terminal.CompleteInfo();
                await SessionChecks.Until(() => session.IsAuthenticated);
                check(session.Current.Message == "登录成功", "success replaces configuration status");
                check(session.IsAuthenticated, "userinfo OK completes fresh/cached authentication after warning: " + mode + "/" + chunk);
            }

        var configFailure = new Terminal("cached", 7);
        await using var failing = new SteamSession(() => configFailure, false);
        await failing.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => failing.Current.Message == "获取账户配置较慢，请稍候…");
        configFailure.Reject();
        await SessionChecks.Until(() => failing.Current.State == LoginState.Failed);
        check(failing.Current.Message == "登录失败，请重试。", "true failure replaces configuration status");
        var bad = new Terminal("bad", 3);
        await using var rejected = new SteamSession(() => bad, false);
        await rejected.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => rejected.Current.State == LoginState.Failed);
        check(!rejected.IsAuthenticated, "explicit invalid password remains a failure");
        var late = new Terminal("cached", 7);
        await using var timeout = new SteamSession(() => late, false, TimeSpan.FromMilliseconds(250));
        await timeout.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => timeout.Current.State == LoginState.TimedOut);
        check(timeout.Current.Message.Contains("账户信息等待超时") && late.LoginWrites == 1, "account data timeout is distinguished from password rejection with no automatic retry");
    }

    internal sealed class Terminal(string mode, int chunk) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool Stopped;
        public int Blanks, LoginWrites, InputWrites;
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
        public Task WriteLineAsync(ReadOnlyMemory<char> text, CancellationToken token)
        {
            if (text.IsEmpty)
            {
                Blanks++;
                return Task.CompletedTask;
            }

            if (text.Span.StartsWith("login "))
            {
                LoginWrites++;
                if (mode == "fresh")
                    Emit("Password:");
                else if (mode == "bad")
                    Emit("ERROR! Invalid Password\r\n");
                else
                    Accepted();
            }
            else
            {
                InputWrites++;
                if (InputWrites == 1)
                    Emit("Steam Guard code:");
                else
                    Accepted();
            }

            return Task.CompletedTask;
        }

        private void Accepted() => Emit("Logging in user 'fixture' to Steam Public...OK\r\nWaiting for client config...\r\nERROR! Package Info request returned error Timeout\r\nOK\r\n");
        public void BeginInfo() => Emit("Waiting for user info...\r\n");
        public void Warn() => Emit("ERROR! Package Info request returned error Timeout\r\n");
        public void Reject() => Emit("ERROR! Invalid Password\r\n");
        public void CompleteInfo() => Emit("Waiting for user info...OK\r\nSteam>");
        public Task StopAsync()
        {
            Stopped = true;
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
