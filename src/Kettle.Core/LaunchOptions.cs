namespace Kettle.Core;

public sealed record LaunchOptions(string? SteamCmd, bool Preview, bool Library, bool Help, string? Capture, bool ExitAfterCapture)
{
    public bool DiagnoseLaunch
    {
        get; init;
    }
    public bool Debug
    {
        get; init;
    }

    public static LaunchOptions Parse(string[] args)
    {
        string? steam = null, capture = null;
        bool preview = false, library = false, help = false, exit = false, diagnose = false, debug = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value(string flag)
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--"))
                    throw new ArgumentException(L.Format(TextId.RequiresANonEmptyPathArgument, flag));
                return args[i];
            }

            switch (args[i])
            {
                case "--debug":
                    debug = true;
                    break;
                case "--diagnose-launch":
                    diagnose = true;
                    break;
                case "--steamcmd":
                    if (steam is not null)
                        throw new ArgumentException(L.Text(TextId.SteamcmdCannotBeSpecifiedMoreThanOnce));
                    steam = Value("--steamcmd");
                    break;
                case "--preview":
                    preview = true;
                    break;
                case "--library":
                    library = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--capture":
                    capture = Value("--capture");
                    break;
                case "--exit-after-capture":
                    exit = true;
                    break;
                default:
                    throw new ArgumentException(L.Format(TextId.UnknownArgumentUseHelpForUsage, args[i]));
            }
        }

        if (exit && capture is null)
            throw new ArgumentException(L.Text(TextId.ExitAfterCaptureRequiresCapture));
        if (diagnose && preview)
            throw new ArgumentException(L.Text(TextId.LaunchDiagnosticsRequireARealLoginAndCannotBe));
        return new(steam, preview, library, help, capture, exit)
        {
            DiagnoseLaunch = diagnose,
            Debug = debug
        };
    }

    public static string Usage => L.Text(TextId.KettleKettleExeSteamcmdSteamcmdExeOrDirectoryLibrary);
}

public sealed record PathStatus(string FullPath, bool IsValid, string Message);
public static class SteamCmdPath
{
    public static string Default(string appDirectory) => Path.GetFullPath(Path.Combine(appDirectory, "..", "steamcmd.exe"));
    public static PathStatus Resolve(string? input, string relativeBase)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new("", false, L.Text(TextId.EnterTheSteamCMDPath));
        try
        {
            var full = Path.GetFullPath(input.Trim(), relativeBase);
            if (Directory.Exists(full))
                full = Path.Combine(full, "steamcmd.exe");
            if (!string.Equals(Path.GetFileName(full), "steamcmd.exe", StringComparison.OrdinalIgnoreCase))
                return new(full, false, L.Text(TextId.SelectSteamcmdExeOrItsContainingDirectory));
            return File.Exists(full) ? new(full, true, L.Text(TextId.SteamCMDReadyValidPath)) : new(full, false, L.Text(TextId.SteamcmdExeWasNotFoundCheckThePath));
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return new("", false, L.Text(TextId.ThePathIsInvalidOrInaccessible));
        }
    }
}
