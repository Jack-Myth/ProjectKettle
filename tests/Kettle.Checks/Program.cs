Console.OutputEncoding = System.Text.Encoding.UTF8;
var suites = new[]
{
    "output",
    "cloud",
    "configuration"
};
if (args.Length == 1 && args[0] is "--help" or "--list")
{
    Console.WriteLine("Kettle.Checks: deterministic offline regressions. No Steam, game, network, or native-console launch.");
    Console.WriteLine("dotnet run --project tests/Kettle.Checks -c Release -- [--suite output|cloud|configuration]");
    Console.WriteLine("Optional tools: tests/Kettle.Diagnostics (UI/Windows), tests/Kettle.AotChecks (published AOT), tests/Kettle.MemoryProbe (measurement). See tests/README.md.");
    return;
}

string? selected = null;
if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "--suite" || !suites.Contains(args[1]))
    {
        Console.Error.WriteLine("Unknown or incomplete argument. Use --help; no tests were started.");
        Environment.ExitCode = 2;
        return;
    }

    selected = args[1];
}

var root = Path.Combine(Path.GetTempPath(), "Kettle Checks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
void Check(bool ok, string name)
{
    if (!ok)
        throw new Exception(name);
    count++;
}

try
{
    foreach (var suite in suites.Where(s => selected is null || s == selected))
    {
        var assertionsBefore = count;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("RUN " + suite);
        switch (suite)
        {
            case "output":
                await LaunchErrorChecks.Run(Check, root);
                await CommandRetryChecks.Run(Check);
                await PromptBoundaryChecks.Run(Check);
                await MetadataBoundaryChecks.Run(Check);
                await RuntimeInterleaveChecks.Run(Check);
                await ChannelRecoveryChecks.Run(Check, root);
                await OutputPreprocessingChecks.Run(Check, root);
                await PidWrapChecks.Run(Check);
                await SerialCommandChecks.Run(Check);
                await AppInfoHeaderChecks.Run(Check);
                GameUpdateChecks.Run(Check);
                await TransportRefactorChecks.Run(Check);
                break;
            case "cloud":
                SaveErrorChecks.Run(Check);
                await MissingSaveChecks.Run(Check, root);
                await CloudDirectoryChecks.Run(Check);
                await AutoCloudPathChecks.Run(Check, root);
                GameInstallChecks.Run(Check, root);
                await RunCloudChecks.Run(Check, root);
                await AutoCloudIntegrationChecks.Run(Check, root);
                break;
            case "configuration":
                await LaunchOptionChecks.Run(Check, root);
                LocalizationChecks.Run(Check, root);
                await BackupRetentionChecks.Run(Check, root);
                await LibraryCleanupChecks.Run(Check, root);
                ZeroManifestChecks.Run(Check, root);
                StartupUpdateChecks.Run(Check);
                ConfigurationChecks.Run(Check, root);
                await SessionChecks.Run(Check);
                await FreshLoginChecks.Run(Check);
                await LoginPhaseChecks.Run(Check);
                await InitialCredentialChecks.Run(Check);
                await LibraryChecks.Run(Check, root);
                await DebugOutputChecks.Run(Check);
                break;
        }

        Console.WriteLine($"PASS {suite}: {count - assertionsBefore} assertions, {elapsed.Elapsed.TotalSeconds:F1}s");
    }

    Console.WriteLine("Offline regression suite passed. Assertions: " + count);
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error);
    Console.Error.WriteLine("Command failure evidence, when available: " + Path.Combine(AppContext.BaseDirectory, "data", "diagnostics", "steam-commands-latest.json"));
    Environment.ExitCode = 1;
}
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("Kettle Checks "))
        Directory.Delete(root, true);
}
