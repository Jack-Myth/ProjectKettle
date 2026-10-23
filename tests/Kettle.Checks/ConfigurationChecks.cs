using Kettle.Core;

internal static class ConfigurationChecks
{
    public static void Run(Action<bool, string> check, string root)
    {
        var bin = Path.Combine(root, "Kettle");
        Directory.CreateDirectory(bin);
        var executable = Path.Combine(root, "steamcmd.exe");
        File.WriteAllText(executable, "fixture, never executed");
        check(SteamCmdPath.Default(bin) == executable && SteamCmdPath.Resolve(root, bin).FullPath == executable, "portable default and directory parameter resolve the intended executable");
        check(SteamCmdPath.Resolve("../steamcmd.exe", bin).IsValid && !SteamCmdPath.Resolve("", bin).IsValid && !SteamCmdPath.Resolve("missing.exe", bin).IsValid, "relative path resolves explicitly; empty or missing path fails");
        check(LaunchOptions.Parse(["--core-session-resume", "session path"]) is { CoreSessionResume: "session path" }, "CoreSession resume preserves the session path");
        check(LaunchOptions.Parse(["--steamcmd", executable]).SteamCmd == executable, "CLI preserves a path containing spaces");
        check(LaunchOptions.Parse(["--debug", "--steamcmd", executable]) is { Debug: true } debug && debug.SteamCmd == executable && LaunchOptions.Parse(["--preview", "--debug"]).Debug, "debug flag composes with explicit paths and preview");
        foreach (var arguments in new[]
        {
            new[]
            {
                "--steamcmd"
            },
            new[]
            {
                "--steamcmd",
                "--preview"
            },
            new[]
            {
                "--unknown"
            },
            new[]
            {
                "--steamcmd",
                executable,
                "--steamcmd",
                executable
            }
        }

        )
        {
            bool rejected = false;
            try
            {
                LaunchOptions.Parse(arguments);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            check(rejected, "invalid or duplicate launch argument fails explicitly: " + arguments[0]);
        }

        var settings = new PortableSettings(bin);
        settings.Save(new("fixture", executable));
        check(settings.Load() == new UserSettings("fixture", executable), "portable configuration roundtrips in its own directory");
        File.WriteAllText(settings.FilePath, "{\"Username\":null,\"DefaultPage\":\"Owned\",\"DefaultSort\":999,\"LocalLibraries\":[\"old\"],\"FutureOption\":true}");
        var legacy = settings.Load();
        check(legacy.Username == "" && legacy.DefaultPage == "Installed" && legacy.DefaultSort == 0 && legacy.HasIgnoredLegacyLibraries, "legacy configuration normalizes to installed library and signals obsolete local libraries");
        settings.Save(legacy);
        check(!settings.Load().HasIgnoredLegacyLibraries && settings.Load().Extra?.ContainsKey("FutureOption") == true, "saving drops obsolete library options and preserves unrelated future settings");
        File.WriteAllText(settings.FilePath, "{broken");
        bool invalid = false;
        try
        {
            settings.Load();
        }
        catch (System.Text.Json.JsonException)
        {
            invalid = true;
        }

        check(invalid && File.ReadAllText(settings.FilePath) == "{broken", "corrupt configuration fails without silently overwriting user data");
    }
}
