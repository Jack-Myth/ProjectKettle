using Kettle.Core;

internal static class GameUpdateChecks
{
    internal static string Body(uint id, ulong from, ulong to) => $"App Update Info {id} : No Error\r\n - State flags : None\r\n - Verifying Installed : 0/0 bytes\r\n - Preallocating : 0/0 bytes\r\n - Downloading : 0/0 bytes\r\n - Staging : 0/0 bytes\r\n - Verifying Staged : 0/0 bytes\r\n - Copying : 0/0 bytes\r\n - Committing : 0/0 bytes\r\n - BuildIDs : {from} -> {to}\r\n - Memory : 0 MB, 0% network, 0% CPU, 0% disk\r\n";

    public static void Run(Action<bool, string> check)
    {
        check(GameUpdateCheck.Parse(Body(2483190, 25104224, 25588841), 2483190) == true, "observed differing BuildIDs require update even with State flags None");
        check(GameUpdateCheck.Parse(Body(2806050, 24670874, 24670874), 2806050) == false, "observed equal BuildIDs are current");
        check(GameUpdateCheck.Parse(Body(635940, 2932384, 2932384), 635940) == false, "second observed equal build pair is current");
        check(GameUpdateCheck.Parse(Body(42, 5, 4), 42) == true, "target rollback also requires installation change");
        check(GameUpdateCheck.Parse(Body(42, 1, 2), 43) is null, "another app cannot determine selected update state");
        check(GameUpdateCheck.Parse(Body(42, 0, 2), 42) is null && GameUpdateCheck.Parse(Body(42, 1, 0), 42) is null, "unknown zero builds do not prove an update");
        check(GameUpdateCheck.Parse(Body(42, 1, 2).Replace("No Error", "Failed"), 42) is null, "failed update query is unknown");
        check(GameUpdateCheck.Parse(Body(42, 1, 2).Split(" - Memory")[0], 42) is null, "truncated response is unknown");
        check(GameUpdateCheck.Parse(Body(42, 1, 2) + Body(42, 2, 2), 42) is null, "multiple responses are not combined");
        check(GameUpdateCheck.Parse("", 42) is null && GameUpdateCheck.Parse("Unknown command", 42) is null, "missing and unsupported output is unknown");
        check(GameUpdateCheck.Parse(Body(42, ulong.MaxValue, ulong.MaxValue), 42) == false, "64-bit build numbers preserve precision");
    }
}
