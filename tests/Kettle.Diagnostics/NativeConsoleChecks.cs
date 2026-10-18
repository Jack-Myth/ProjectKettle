using Kettle.Core;
using static SessionChecks;

internal static class NativeConsoleChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        // Actual child process uses ReadConsoleW, with echo disabled for password. A plain stdin pipe is insufficient.
        if (OperatingSystem.IsWindows())
        {
            var terminal = new ConPtyTerminal();
            await using var session = new SteamSession(() => terminal, false, TimeSpan.FromSeconds(8));
            session.Updated += update => Console.WriteLine("ConPTY fixture state: " + update.State + ", exit code " + terminal.ExitCode);
            Console.WriteLine("ConPTY fixture host: " + Environment.ProcessPath);
            await session.StartAsync(Environment.ProcessPath!, "fixture_user");
            await Until(() => session.Current.State == LoginState.Password, 10000);
            await session.SubmitAsync("fixture-sensitive-input".AsMemory());
            await Until(() => session.Current.State == LoginState.GuardCode);
            await session.SubmitAsync("fixture-code".AsMemory());
            await Until(() => session.IsAuthenticated);
            check(session.IsAuthenticated, "real ConPTY child: ReadConsoleW password, stderr guard prompt, UTF-8 and successful login");
            using var process = System.Diagnostics.Process.GetProcessById(terminal.ProcessId);
            check(process.MainWindowHandle == 0, "ConPTY child has no visible console window");
            await Task.Delay(100);
            await session.ExecuteCommandAsync("app_status 42", TimeSpan.FromSeconds(2));
            check(true, "real ConPTY child: command roundtrip");
            var profile = await PlayerProfileReader.ReadAsync("fixture_user", session, CancellationToken.None);
            check(profile.PersonaName == "Fixture Player" && profile.SteamId == "76561198012345678", "profile fields read through actual hidden console command");
            var metadata = await session.QueryMetadataAsync(["app_info_print 42", "app_info_print 43"], TimeSpan.FromSeconds(3));
            check(metadata.Contains("Command complete") && !metadata.Contains("fixture-sensitive-input"), "read-only metadata batch captures only post-login command output");
            try
            {
                await session.QueryMetadataAsync(["app_info_update 1"], TimeSpan.FromSeconds(1));
                throw new Exception("Forbidden metadata command accepted");
            }
            catch (ArgumentException)
            {
                check(true, "metadata API rejects cache-update and non-read-only commands");
            }

            using (var cancelRead = new CancellationTokenSource(30))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await session.QueryMetadataAsync(["app_info_print 44"], TimeSpan.FromSeconds(2), cancelRead.Token);
                    throw new Exception("Read cancellation ignored");
                }
                catch (OperationCanceledException)
                {
                    check(clock.ElapsedMilliseconds < 500 && session.IsAuthenticated, "cancel metadata read promptly without ending login");
                }
            }

            var afterCancel = await session.QueryMetadataAsync(["app_info_print 45"], TimeSpan.FromSeconds(2));
            check(afterCancel.Contains("Command complete"), "cancelled query drains before next serialized metadata request");
            try
            {
                await session.QueryMetadataAsync(["app_info_print 46"], TimeSpan.FromMilliseconds(450));
                throw new Exception("Metadata timeout ignored");
            }
            catch (TimeoutException)
            {
                check(session.IsAuthenticated, "metadata timeout preserves the authenticated process");
            }

            await Until(() => session.CommandChannel.CanRead, 5000);
            await session.ExecuteCommandAsync("spawn_owned_fixture", TimeSpan.FromSeconds(2));
            await Until(() => !terminal.InspectProcesses().CanDisconnect);
            var logout = session.LogoutAsync();
            await Task.Delay(100);
            check(!logout.IsCompleted && !terminal.InspectProcesses().CanDisconnect, "native console teardown waits for owned non-control child instead of killing it on Job close");
            await Task.WhenAll(logout, session.LogoutAsync()).WaitAsync(TimeSpan.FromSeconds(6));
            process.Refresh();
            check(!session.IsAuthenticated && process.HasExited, "repeated logout actually exits owned console process");
        }
    }

    public static async Task Fresh(Action<bool, string> check)
    {
        var transcript = new SteamDebugOutput();
        var terminal = new FreshTerminal();
        await using var session = new SteamSession(() => terminal, false, TimeSpan.FromSeconds(8), debugOutput: transcript.CreateStream("native fixture"));
        var phases = new List<LoginUpdate>();
        session.Updated += u => phases.Add(u);
        await session.StartAsync(Environment.ProcessPath!, "fixture_user");
        await Until(() => session.Current.State == LoginState.Password);
        await session.SubmitAsync("fixture-sensitive-input".AsMemory());
        await Until(() => session.Current.State == LoginState.GuardCode);
        await session.SubmitAsync("fixture-code".AsMemory());
        await Until(() => session.IsAuthenticated);
        check(session.IsAuthenticated && phases.Any(p => p.Message.Contains("获取账户配置较慢")), "native no-echo fresh login survives config warning and waits for final user-info OK");
        check(terminal.Blanks == 0, "native password and Guard authentication receives no ordinary-command trailing blank");
        check(phases.All(p => !p.Message.Contains("fixture-sensitive-input") && !p.Message.Contains("fixture-code")), "native login phases contain no entered secrets");
        long seen = -1;
        var text = transcript.ReadSince(ref seen)!;
        check(text.Contains("Password:") && text.Contains("Steam>") && text.Contains("ERROR!"), "native debug stream keeps authentication prompts, nonfatal errors and final console prompt");
        check(!text.Contains("fixture-sensitive-input") && !text.Contains("fixture-code"), "native console authentication echo is protected in the debug stream");
    }

    private sealed class FreshTerminal : ISteamTerminal
    {
        private readonly ConPtyTerminal inner = new();
        public int Blanks;
        public bool SupportsPromptBoundaries => true;

        public Task StartAsync(string exe, IReadOnlyList<string> args, CancellationToken token) => inner.StartAsync(exe, ["--fresh-login-fixture"], token);
        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken token)
        {
            if (value.IsEmpty)
                Blanks++;
            return inner.WriteLineAsync(value, token);
        }

        public Task StopAsync() => inner.StopAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
