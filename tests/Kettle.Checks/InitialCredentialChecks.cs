using System.Threading.Channels;
using Kettle.Core;

internal static class InitialCredentialChecks
{
    internal const string Password = "fixture space \"quoted\"\\path;value";
    internal const string Code = "AB123";
    public static async Task Run(Action<bool, string> check)
    {
        foreach (string code in new[]
        {
            "",
            Code
        }

        )
            foreach (int chunk in new[]
            {
                1,
                7,
                4096
            }

            )
            {
                const string nativePassword = "Native123!";
                var terminal = new Terminal("full", chunk);
                var debug = new SteamDebugOutput();
                await using var session = new SteamSession(() => terminal, false, debugOutput: debug.CreateStream("native fixture"));
                await session.StartAsync("fixture.exe", "fixture_user", nativePassword.AsMemory(), code.AsMemory());
                if (code.Length == 0)
                {
                    await SessionChecks.Until(() => session.Current.State == LoginState.GuardCode);
                    check(terminal.Writes.Single() == "login fixture_user " + nativePassword, "nonempty password uses native login parameter with no empty token argument");
                    await session.SubmitAsync(Code.AsMemory());
                }
                else
                {
                    await SessionChecks.Until(() => session.Current.Message == "正在获取用户信息…");
                    check(terminal.Writes.Single() == "login fixture_user " + nativePassword + " " + Code, "nonempty password and token are submitted together in the native login command");
                }

                await SessionChecks.Until(() => session.Current.Message == "正在获取用户信息…");
                long seen = -1;
                string text = debug.ReadSince(ref seen)!;
                check(!text.Contains(nativePassword) && !text.Contains(Code) && terminal.Args.SequenceEqual(new[] { "-globaluser" }), "native command echo redacts password/token; process arguments contain neither");
                terminal.Complete();
                await SessionChecks.Until(() => session.IsAuthenticated);
            }

        var promptFallback = new Terminal("native-prompt");
        await using (var session = new SteamSession(() => promptFallback, false))
        {
            await session.StartAsync("fixture.exe", "fixture_user", "Native123!".AsMemory(), Code.AsMemory());
            await SessionChecks.Until(() => session.Current.Message == "正在获取用户信息…");
            check(promptFallback.Writes.SequenceEqual(new[] { "login fixture_user Native123! " + Code, "Native123!", Code }), "a provided but not rejected native argument is reused once if SteamCMD explicitly requests it, without retyping");
        }

        foreach (int chunk in new[]
        {
            1,
            7,
            4096
        }

        )
        {
            var terminal = new Terminal("full", chunk);
            var debug = new SteamDebugOutput();
            await using var session = new SteamSession(() => terminal, false, debugOutput: debug.CreateStream("fixture"));
            var updates = new List<LoginUpdate>();
            session.Updated += updates.Add;
            await session.StartAsync("fixture.exe", "fixture_user", Password.AsMemory(), Code.AsMemory());
            await SessionChecks.Until(() => session.Current.Message == "正在获取用户信息…");
            check(terminal.Writes.SequenceEqual(new[] { "login fixture_user", Password, Code }) && terminal.Args.SequenceEqual(new[] { "-globaluser" }), "one click submits each filled field only to its matching prompt; no credentials in process/login arguments: " + chunk);
            check(!session.IsAuthenticated && updates.Any(x => x.Message == "正在获取账户配置…"), "filled credentials do not skip config/user-info or fabricate success");
            long seen = -1;
            string text = debug.ReadSince(ref seen)!;
            check(!text.Contains(Password) && !text.Contains(Code) && updates.All(x => !x.Message.Contains(Password) && !x.Message.Contains(Code)), "split echoed credentials never reach Debug buffer or status messages");
            terminal.Complete();
            await SessionChecks.Until(() => session.IsAuthenticated);
            check(session.Current.Message == "登录成功" && terminal.Writes.Count == 3, "final success uses the same authenticated session without another submit");
        }

        foreach (var pair in new[]
        {
            ("", ""),
            (Password, ""),
            ("", Code)
        }

        )
        {
            var terminal = new Terminal("full");
            await using var session = new SteamSession(() => terminal, false);
            await session.StartAsync("fixture.exe", "fixture_user", pair.Item1.AsMemory(), pair.Item2.AsMemory());
            await SessionChecks.Until(() => pair.Item1.Length == 0 ? session.Current.State == LoginState.Password : session.Current.State == LoginState.GuardCode);
            if (pair.Item1.Length == 0)
            {
                check(terminal.Writes.SequenceEqual(new[] { "login fixture_user" }), "empty password sends no argument/blank and never shifts filled token into password");
                await session.SubmitAsync(Password.AsMemory());
            }

            if (pair.Item2.Length == 0)
            {
                await SessionChecks.Until(() => session.Current.State == LoginState.GuardCode);
                check(terminal.Writes.Count == 2, "empty token waits for later requested input without sending an empty line");
                await session.SubmitAsync(Code.AsMemory());
            }

            await SessionChecks.Until(() => session.Current.Message == "正在获取用户信息…");
            terminal.Complete();
            await SessionChecks.Until(() => session.IsAuthenticated);
            check(terminal.Writes.SequenceEqual(new[] { "login fixture_user", Password, Code }), "all optional combinations route exact values to correct interactive fields");
        }

        foreach (string mode in new[]
        {
            "cached",
            "mobile",
            "guard-first",
            "repeat-password",
            "repeat-guard",
            "failure"
        }

        )
        {
            var terminal = new Terminal(mode);
            await using var session = new SteamSession(() => terminal, false);
            await session.StartAsync("fixture.exe", "fixture_user", mode == "guard-first" ? ReadOnlyMemory<char>.Empty : Password.AsMemory(), Code.AsMemory());
            if (mode == "cached")
            {
                await SessionChecks.Until(() => session.IsAuthenticated);
                check(terminal.Writes.Count == 1, "saved login uses username only and discards unused filled credentials");
            }

            if (mode == "mobile")
            {
                await SessionChecks.Until(() => session.Current.State == LoginState.MobileConfirmation);
                check(terminal.Writes.Count == 1, "mobile confirmation remains interactive without blind credential writes");
                terminal.Complete();
                await SessionChecks.Until(() => session.IsAuthenticated);
            }

            if (mode == "guard-first")
            {
                await SessionChecks.Until(() => session.Current.Message == "正在获取用户信息…");
                check(terminal.Writes.SequenceEqual(new[] { "login fixture_user", Code }), "direct guard challenge consumes token without submitting unused password");
            }

            if (mode is "repeat-password" or "repeat-guard")
            {
                var state = mode == "repeat-password" ? LoginState.Password : LoginState.GuardCode;
                await SessionChecks.Until(() => session.Current.State == state);
                int count = terminal.Writes.Count;
                await Task.Delay(30);
                check(terminal.Writes.Count == count, "repeated challenge never resends initial secret automatically");
                await session.SubmitAsync((state == LoginState.Password ? Password : Code).AsMemory());
                await SessionChecks.Until(() => session.Current.Message == "正在获取用户信息…");
                check(terminal.Writes.Count == (state == LoginState.Password ? 4 : 4), "repeated password/guard prompt remains available for explicit follow-up input");
            }

            if (mode == "failure")
            {
                await SessionChecks.Until(() => session.Current.State == LoginState.Failed);
                check(!session.IsAuthenticated && terminal.Writes.Count == 2, "authentication rejection does not submit unused guard code");
            }
        }

        foreach (string invalid in new[]
        {
            "x\r\nquit",
            "x\nquit",
            "x\x1b",
            "x\0",
            "x\b",
            "x\u2028quit"
        }

        )
            foreach (bool password in new[]
            {
                true,
                false
            }

            )
            {
                var terminal = new Terminal("full");
                await using var session = new SteamSession(() => terminal, false);
                await session.StartAsync("fixture.exe", "fixture_user", (password ? invalid : "").AsMemory(), (password ? "" : invalid).AsMemory());
                check(session.Current.State == LoginState.Failed && !terminal.Started && terminal.Writes.Count == 0 && !session.Current.Message.Contains(invalid), "control-character injection is rejected before terminal launch without reflecting the secret");
            }

        var forged = new Terminal("full");
        await using (var session = new SteamSession(() => forged, false))
        {
            await session.StartAsync("fixture.exe", "fixture_user", "Logged in OK".AsMemory(), ReadOnlyMemory<char>.Empty);
            await SessionChecks.Until(() => session.Current.State == LoginState.GuardCode);
            check(!session.IsAuthenticated, "a secret equal to success text cannot forge authentication through its echo");
        }

        var newlineUser = new Terminal("full");
        await using (var session = new SteamSession(() => newlineUser, false))
        {
            await session.StartAsync("fixture.exe", "fixture_user\n", "Native123!".AsMemory(), Code.AsMemory());
            check(!newlineUser.Started && session.Current.State == LoginState.Failed, "strict username anchor rejects a trailing newline before native credentials could become a second command");
        }
    }

    internal sealed class Terminal(string mode, int chunk = 7) : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public List<string> Writes = [];
        public string[] Args = [];
        public bool Started;
        private int passwordCount, guardCount;
        private void Emit(string value)
        {
            for (int i = 0; i < value.Length; i += chunk)
                output.Writer.TryWrite(value.Substring(i, Math.Min(chunk, value.Length - i)));
        }

        public void Complete() => Emit("Waiting for user info...OK\r\nSteam>");
        public Task StartAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            Started = true;
            Args = args.ToArray();
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            string value = line.ToString();
            Writes.Add(value);
            if (Writes.Count == 1)
            {
                var args = value.Split(' ');
                if (args.Length < 2 || args[0] != "login" || args[1] != "fixture_user")
                    throw new Exception("unexpected login command");
                if (args.Length > 2)
                {
                    passwordCount = mode == "native-prompt" ? 0 : 1;
                    Emit(value + "\r\n");
                    Emit(mode == "native-prompt" ? "Password:" : args.Length == 4 ? "Waiting for client config...OK\r\nWaiting for user info..." : "Steam Guard code:");
                }
                else
                    Emit(mode switch
                    {
                        "cached" => "Logged in OK\r\nSteam>",
                        "mobile" => "Please confirm the login in the Steam mobile app.",
                        "guard-first" => "Steam Guard code:",
                        _ => "Password:"
                    });
            }
            else
            {
                Emit(value + "\r\n");
                bool password = mode != "guard-first" && (passwordCount == 0 || mode == "repeat-password" && passwordCount == 1);
                if (password)
                {
                    passwordCount++;
                    Emit(mode == "failure" ? "ERROR! Invalid Password\r\n" : mode == "repeat-password" && passwordCount == 1 ? "Password:" : "Steam Guard code:");
                }
                else
                {
                    guardCount++;
                    Emit(mode == "repeat-guard" && guardCount == 1 ? "Steam Guard code:" : "Waiting for client config...OK\r\nWaiting for user info...");
                }
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
