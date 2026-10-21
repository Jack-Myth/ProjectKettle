using Kettle.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args is ["--debug-trace-fixture", var traceMarker])
{
    DebugTraceChecks.Fixture(traceMarker);
    return;
}
if (args is ["--cloud-host-fixture", var cloudMode])
{
    CloudHostChecks.Fixture(cloudMode);
    return;
}
if (args is ["--update-fixture", var updateMode])
{
    StartupUpdateNativeChecks.Fixture(updateMode);
    return;
}

if (args is ["--fresh-login-fixture"])
{
    ConsoleFixture.Run(true);
    return;
}

if (args.FirstOrDefault() is "--console-fixture" or "-globaluser")
{
    ConsoleFixture.Run();
    return;
}

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("Optional Kettle.Diagnostics; no Steam or cloud probes are built here.");
    Console.WriteLine("--check-ui : offline view-model regressions; no desktop required");
    Console.WriteLine("--check-cloud-host : synthetic helper handshake, stderr bounds, failure and shutdown checks");
    Console.WriteLine("--check-run-controls : focused offline stop/selection view-model checks");
    Console.WriteLine("--check-channel-recovery : focused offline channel/launch recovery UI checks");
    Console.WriteLine("--check-controls <output directory> : Windows Avalonia popup Escape/outside-pointer checks and control bitmaps");
    Console.WriteLine("--check-windows : Windows interactive host/ConPTY fixture and NTFS junction; no Steam; requires dotnet run (not dotnet test)");
    Console.WriteLine("--render-library|--render-details|--render-settings|--render-prompts|--render-hybrid <output directory> : interactive Windows desktop, Avalonia bitmaps (not desktop screenshots)");
    Console.WriteLine("--check-debug-ui|--check-login-phase-ui <output directory> : synthetic UI checks and bitmaps");
    Console.WriteLine("--check-startup-ui|--check-startup-close <output directory> : simulated updater UI and cancellation");
    Console.WriteLine("--check-startup-update|--check-update-cancel|--check-update-live : native synthetic updater processes; no Steam");
    Console.WriteLine("--check-pty-wrap <output directory> : regenerate synthetic ConPTY wrapping fixtures");
    Console.WriteLine("--check-game-update-ui <output directory> : update-required disabled button and refresh recovery");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "Kettle Diagnostics " + Guid.NewGuid().ToString("N"));
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
    switch (args)
    {
        case ["--check-debug-trace"]:
            await DebugTraceChecks.Run(Check);
            Console.WriteLine("Debug trace checks passed. Assertions: " + count);
            break;
        case ["--check-cloud-host"]:
            await CloudHostChecks.Run(Check);
            Console.WriteLine("CloudHost checks passed. Assertions: " + count);
            break;
        case ["--check-game-update-ui", var updateStateOutput]:
            DetailsRender.Run(updateStateOutput, updatesOnly: true);
            break;
        case ["--check-debug-ui", var debugOutput]:
            DebugUiChecks.Run(debugOutput);
            break;
        case ["--check-login-phase-ui", var loginOutput]:
            LoginPhaseUiChecks.Run(loginOutput);
            break;
        case ["--check-channel-recovery"]:
            await LaunchRecoveryChecks.Run(Check, root);
            await ChannelRecoveryChecks.RunUi(Check, root);
            Console.WriteLine("Channel recovery UI checks passed. Assertions: " + count);
            break;
        case ["--check-startup-close", var closeOutput]:
            StartupUpdateRender.Run(closeOutput, true);
            break;
        case ["--check-startup-ui", var updateOutput]:
            StartupUpdateRender.Run(updateOutput);
            break;
        case ["--check-update-cancel"]:
            await StartupUpdateNativeChecks.Cancel(Check);
            Console.WriteLine("Update cancellation checks passed. Assertions: " + count);
            break;
        case ["--check-update-live"]:
            await StartupUpdateNativeChecks.Live(Check);
            break;
        case ["--check-startup-update"]:
            StartupUpdateChecks.Run(Check);
            await StartupUpdateNativeChecks.Run(Check);
            await StartupUpdateNativeChecks.Run(Check, true);
            Console.WriteLine("Startup update checks passed. Assertions: " + count);
            break;
        case ["--check-ui"]:
            var executable = Path.Combine(root, "steamcmd.exe");
            File.WriteAllText(executable, "fixture only");
            await LoginChecks.Run(Check, executable, root);
            await LocalFeatureChecks.Run(Check, root, executable);
            await DetailChecks.Run(Check, root, executable);
            await SettingsChecks.Run(Check, root);
            await InstalledOnlyChecks.Run(Check, root);
            await DirectLibraryChecks.Run(Check, root);
            await LaunchRecoveryChecks.Run(Check, root);
            await ChannelRecoveryChecks.RunUi(Check, root);
            await RunControlsChecks.Run(Check, root);
            await GameUpdateUiChecks.Run(Check, root);
            try
            {
                L.Initialize("en");
                await GameUpdateUiChecks.Run(Check, Path.Combine(root, "english"));
            }
            finally { L.Initialize("zh-CN"); }
            Console.WriteLine("Optional UI regressions passed. Assertions: " + count);
            break;
        case ["--check-run-controls"]:
            await RunControlsChecks.Run(Check, root);
            Console.WriteLine("Run controls checks passed: " + count);
            break;
        case ["--check-windows"]:
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows required; no tests started.");
            await NativeConsoleChecks.Run(Check);
            await NativeConsoleChecks.Fresh(Check);
            await AutoCloudPathChecks.Run(Check, root, windowsEnvironment: true);
            Console.WriteLine("Optional Windows environment checks passed. Assertions: " + count);
            break;
        case ["--render-library", var output]:
            LibraryRender.Run(output);
            break;
        case ["--render-details", var output]:
            DetailsRender.Run(output);
            break;
        case ["--render-settings", var output]:
            SettingsRender.Run(output);
            break;
        case ["--render-settings-en", var output]:
            L.Initialize("en");
            SettingsRender.Run(output);
            break;
        case ["--render-prompts", var output]:
            PromptRender.Run(output);
            break;
        case ["--render-hybrid", var output]:
            HybridRunChecks.Render(output);
            break;
        case ["--check-controls", var output]:
            RunControlsRender.Run(output);
            break;
        case ["--check-pty-wrap", var output]:
            await PtyWrapCheck.Run(output);
            break;
        default:
            Console.Error.WriteLine("Unknown/incomplete arguments. Use --help; no diagnostic was started.");
            Environment.ExitCode = 2;
            break;
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
        && Path.GetFileName(root).StartsWith("Kettle Diagnostics ", StringComparison.Ordinal))
        Directory.Delete(root, true);
}
