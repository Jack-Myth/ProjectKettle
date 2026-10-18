using Kettle.Core;

internal static class DebugOutputChecks
{
    public static Task Run(Action<bool, string> check)
    {
        var output = new SteamDebugOutput();
        using var stream = output.CreateStream("fixture");
        foreach (char c in "\x1b[97mGame process added: AppID 42\x1b[m\r\nERROR! fixture error\r\nSteam>")
            stream.Write(c.ToString());
        long version = -1;
        var text = output.ReadSince(ref version)!;
        check(text.Contains("Game process added") && text.Contains("ERROR! fixture error") && text.EndsWith("Steam>") && !text.Contains('\x1b'), "debug preserves output across split terminal controls");
        check(output.ReadSince(ref version) is null, "unchanged debug output does not trigger UI updates");
        stream.Write(new string('x', SteamDebugOutput.Capacity * 3));
        version = -1;
        check(output.ReadSince(ref version)!.Length == SteamDebugOutput.Capacity, "debug history stays bounded");
        output.Clear();
        version = -1;
        check(output.ReadSince(ref version) == "", "debug clear drops retained text");
        return Task.CompletedTask;
    }
}