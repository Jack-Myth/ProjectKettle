using System.Text.Json;
using Kettle.Core;

internal static class GameInstallChecks
{
    internal static string ActualInfo()
    {
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gameinstall-pcbs.json")));
        return AutoCloudPathChecks.AppInfo(621060, sample.RootElement.GetProperty("Ufs"));
    }

    public static void Run(Action<bool, string> check, string root)
    {
        var install = Path.Combine(root, "Library B with spaces", "common", "PC Building Simulator");
        Directory.CreateDirectory(install);
        var other = Path.Combine(root, "Library A", "common", "Other Game");
        Directory.CreateDirectory(other);
        var known = Path.Combine(root, "Known Folder");
        Directory.CreateDirectory(known);
        var roots = new WindowsAutoCloudRoots(_ => known);
        var identity = new AutoCloudIdentity(123, 76561197960265851);
        AutoCloudPathPlan Plan(string info, string? path) => AutoCloudPathPlan.Compile(AutoCloudDefinition.Parse(621060, info), identity, AutoCloudPlatform.Windows, roots, path);
        var info = ActualInfo();
        var plan = Plan(info, install);
        foreach (var relative in new[]
        {
            "Saves/a.binary",
            "Saves/76561197960265851/a.binary"
        }

        )
        {
            var key = new AutoCloudKey("gAmEiNsTaLl", relative);
            var local = plan.ToLocal(key);
            check(local == Path.Combine(install, relative.Replace('/', Path.DirectorySeparatorChar)) && plan.ToCloud(local) == new AutoCloudKey("GameInstall", relative), "actual PCBS two nonrecursive rules map both directions under the exact per-app multi-library path with spaces");
            check(AutoCloudWire.Name(AutoCloudWire.Key("%gameinstall%" + relative)) == "%GameInstall%" + relative, "root casing canonicalizes without changing subdirectories or file names");
        }

        check(Plan(info.Replace("gameinstall", "GameInstall"), install).Rules.SequenceEqual(plan.Rules), "metadata GameInstall/gameinstall are equivalent protocol identifiers");
        check(Plan(info, other).Rules.All(r => r.LocalRoot == other) && plan.Rules.All(r => r.LocalRoot == install), "each compiled plan keeps its own app installation, never a shared SteamCMD or launcher root");
        void Reject(Action work, string reason)
        {
            try
            {
                work();
                throw new Exception("Expected mapping failure: " + reason);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                check(true, reason);
            }
        }

        foreach (var missing in new string?[]
        {
            null,
            "",
            Path.Combine(root, "not installed")
        }

        )
            Reject(() => Plan(info, missing), "missing/unavailable install path fails before scan/native connection without a working-directory fallback");
        Reject(() => Plan(info, "relative/game"), "relative installation context is rejected");
        Reject(() => plan.ToLocal(new("GameInstall", "../outside.binary")), "cloud traversal cannot escape game install");
        Reject(() => Plan(info.Replace("\"Saves\"", "\"../Saves\""), install), "metadata traversal remains rejected");
        check(plan.ToLocal(new("GameInstall", "Saves/nested/excluded.binary")) == Path.Combine(install, "Saves", "nested", "excluded.binary"), "existing cloud paths resolve independently of nonrecursive local discovery");
        Reject(() => plan.ToCloud(Path.Combine(other, "Saves", "a.binary")), "another library/game path cannot enter this plan");
        string Rule(string original, string target, string extra) => "\"621060\" { \"ufs\" { \"savefiles\" { \"0\" { \"root\" \"" + original + "\" \"path\" \"Base\" \"pattern\" \"*.binary\" \"recursive\" \"1\" } } \"rootoverrides\" { \"0\" { \"root\" \"" + original + "\" \"os\" \"Windows\" \"useinstead\" \"" + target + "\" \"addpath\" \"Sub Dir\" " + extra + " } } } }";
        var toInstall = Plan(Rule("WinMyDocuments", "gAmEiNsTaLl", "\"pathtransforms\" { \"0\" { \"find\" \"Base\" \"replace\" \"Changed\" } }"), install);
        check(toInstall.ToLocal(new("WinMyDocuments", "Base/nested/a.binary")) == Path.Combine(install, "Sub Dir", "Changed", "nested", "a.binary") && toInstall.ToCloud(Path.Combine(install, "Sub Dir", "Changed", "nested", "a.binary")).Root == "WinMyDocuments", "override into GameInstall uses per-app context and preserves logical cloud root, transform/addpath/recursive semantics");
        Reject(() => Plan(Rule("WinMyDocuments", "GameInstall", ""), null), "override into GameInstall also requires a confirmed path");
        var fromInstall = Plan(Rule("gameinstall", "WinMyDocuments", ""), null);
        check(fromInstall.ToLocal(new("GameInstall", "Base/a.binary")) == Path.Combine(known, "Sub Dir", "Base", "a.binary"), "override away from GameInstall resolves the actual target KnownFolder without guessing an install path");
        Reject(() => AutoCloudRootNames.Canonical("游戏安装目录"), "translated display labels are not protocol aliases");
    }

}
