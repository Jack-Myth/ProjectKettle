using Kettle.Core;

internal static class DebugOutputChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (int chunk in new[]
        {
            1,
            3,
            4096
        }

        )
        {
            var output = new SteamDebugOutput();
            using var stream = output.CreateStream("fixture");
            stream.Protect("fixture-password".AsSpan());
            stream.Protect("765432".AsSpan());
            string input = "Player player_76561198012345678\r\nPassword: fixture-pass\x1b[97mword\r\nGuard: 765432\r\nGame process added: AppID 42, ProcID 123\r\nERROR! fixture error\r\nSteam>";
            long version = -1;
            for (int i = 0; i < input.Length; i += chunk)
            {
                stream.Write(input.Substring(i, Math.Min(chunk, input.Length - i)));
                string text = output.ReadSince(ref version) ?? "";
                check(!text.Contains("fixture-password") && !text.Contains("765432"), "no sensitive echo enters even intermediate debug snapshots");
            }

            version = -1;
            string final = output.ReadSince(ref version)!;
            check(final.Contains("player_76561198012345678") && final.Contains("Game process added") && final.Contains("ERROR! fixture error") && final.EndsWith("Steam>"), "debug preserves account, asynchronous notifications, errors and prompt across blocks");
            check(!final.Contains('\x1b') && final.Contains("●●●"), "split terminal controls do not expose protected input");
            check(output.ReadSince(ref version) is null, "unchanged debug buffer does not trigger UI updates");
            stream.Write(new string('x', SteamDebugOutput.Capacity * 3));
            version = -1;
            check(output.ReadSince(ref version)!.Length == SteamDebugOutput.Capacity, "debug retention is bounded under large output");
            output.Clear();
            version = -1;
            check(output.ReadSince(ref version) == "", "debug clear drops retained text");
        }

        var transcript = new SteamDebugOutput();
        var terminal = new FakeTerminal("guard");
        await using var session = new SteamSession(() => terminal, false, debugOutput: transcript.CreateStream("session"));
        await session.StartAsync("fixture.exe", "fixture_user");
        await SessionChecks.Until(() => session.Current.State == LoginState.Password);
        await session.SubmitAsync("fixture-sensitive-input".AsMemory());
        await SessionChecks.Until(() => session.Current.State == LoginState.GuardCode);
        await session.SubmitAsync("fixture-code".AsMemory());
        await SessionChecks.Until(() => session.IsAuthenticated);
        long seen = -1;
        string actual = transcript.ReadSince(ref seen)!;
        check(actual.Contains("Password:") && actual.Contains("Steam>") && !actual.Contains("fixture-sensitive-input") && !actual.Contains("fixture-code"), "session observer receives unclassified terminal output with protected authentication responses");
    }
}
