using System.Threading.Channels;
using Kettle.Core;

internal static class LoginPhaseChecks
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
            foreach (string separator in new[]
            {
                "\r\n",
                ""
            }

            )
                foreach (string dots in new[]
                {
                    "...",
                    "…"
                }

                )
                {
                    var terminal = new Terminal(chunk, separator, dots);
                    await using var session = new SteamSession(() => terminal, false);
                    var updates = new List<LoginUpdate>();
                    session.Updated += updates.Add;
                    await session.StartAsync("fixture.exe", "fixture");
                    await SessionChecks.Until(() => session.Current.State == LoginState.MobileConfirmation);
                    terminal.Config();
                    await SessionChecks.Until(() => session.Current.Message == "正在获取账户配置…");
                    check(!session.IsAuthenticated && !session.Current.NeedsInput, "mobile interruption does not gate config behind contiguous Public...OK: " + chunk + "/" + dots + "/" + separator.Length);
                    terminal.Emit("Please confirm the login: Waiting for confirmation...");
                    await Task.Delay(20);
                    check(session.Current.Message == "正在获取账户配置…", "old mobile prompt and punctuation cannot overwrite config");
                    terminal.Emit("ERROR! Package Info request returned error Timeout");
                    await SessionChecks.Until(() => session.Current.Message == "获取账户配置较慢，请稍候…");
                    check(!session.IsAuthenticated, "slow config warning remains pending, not success or rejection");
                    terminal.Info();
                    await SessionChecks.Until(() => session.Current.Message == "正在获取用户信息…");
                    terminal.History();
                    await Task.Delay(20);
                    check(session.Current.Message == "正在获取用户信息…" && !session.IsAuthenticated, "replayed old connecting/mobile/config and warning cannot regress user-info stage");
                    terminal.Complete();
                    await SessionChecks.Until(() => session.IsAuthenticated);
                    check(session.Current.State == LoginState.Authenticated && updates.Count(x => x.State == LoginState.Authenticated) == 1, "final user-info OK publishes authenticated exactly once");
                    terminal.History();
                    await Task.Delay(20);
                    check(session.IsAuthenticated && session.Current.State == LoginState.Authenticated && terminal.Logins == 1 && terminal.Blanks == 0, "historical auth text after success never regresses or repeats authentication");
                }

        var batched = new Terminal(3);
        await using (var session = new SteamSession(() => batched, false))
        {
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.Current.State == LoginState.MobileConfirmation);
            batched.Emit("OKWaiting for client config...OKWaiting for user info...OKSteam>");
            await SessionChecks.Until(() => session.IsAuthenticated);
            check(session.Current.Message == "登录成功", "multiple stages without newline in one stream reach final success");
        }

        var failure = new Terminal(1);
        await using (var session = new SteamSession(() => failure, false))
        {
            await session.StartAsync("fixture.exe", "fixture");
            await SessionChecks.Until(() => session.Current.State == LoginState.MobileConfirmation);
            failure.Config();
            failure.Info();
            failure.Emit("ERROR! Invalid Password\r\nWaiting for user info...OKSteam>");
            await SessionChecks.Until(() => session.Current.State == LoginState.Failed);
            check(!session.IsAuthenticated && session.Current.Message == "登录失败，请重试。", "actual authentication rejection stays failed even if old success follows");
        }
    }

    internal sealed class Terminal(int chunk = 7, string separator = "\r\n", string dots = "...") : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public int Logins, Blanks;
        public List<string> Inputs = [];
        public void Emit(string value)
        {
            lock (output)
                for (int i = 0; i < value.Length; i += chunk)
                    output.Writer.TryWrite(value.Substring(i, Math.Min(chunk, value.Length - i)));
        }

        public void Config() => Emit("OK" + separator + "wAiTiNg FoR cLiEnT cOnFiG" + dots);
        public void Info() => Emit("OK" + separator + "WAITING FOR USER INFO" + dots);
        public void Complete() => Emit((separator.Length == 0 ? "Waiting for user info" + dots : "") + "OK" + separator + "Steam>");
        public void History() => Emit(separator + "Logging in user 'fixture' to Steam Public...OK" + separator + "Please confirm the login: Waiting for confirmation..." + separator + "Waiting for client config..." + separator + "ERROR! Package Info request returned error Timeout" + separator);
        public Task StartAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken token)
        {
            string c = value.ToString();
            if (c.StartsWith("login "))
            {
                Logins++;
                Emit("Logging in user 'fixture' to Steam Public..." + separator + "Please confirm the login in the Steam mobile app." + separator + "Waiting for confirmation...");
            }
            else if (c.Length == 0)
                Blanks++;
            else
            {
                Inputs.Add(c);
                Emit(c + separator + (c switch
                {
                    "info" => "Persona name: Fixture\nSteamID: [U:1:123]\n",
                    "library_folder_list" => "Index 0, ContentID 1, Path \"C:\\Fixture\", Label \"\", Apps 0, Mounted yes\n",
                    "apps_installed" => "0 apps installed :\n",
                    _ => ""
                }) + "Steam>");
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
