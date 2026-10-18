using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kettle.Core;

internal static class AutoCloudPathChecks
{
    internal static string AppInfo(uint id, JsonElement ufs) => Quote(id.ToString()) + " { \"ufs\" { " + Object(ufs) + " } }";
    private static string Object(JsonElement value) => string.Join(" ", value.EnumerateObject().Select(p => Quote(p.Name) + " " + (p.Value.ValueKind == JsonValueKind.Object ? "{ " + Object(p.Value) + " }" : Quote(p.Value.ToString()))));
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static readonly AutoCloudIdentity Identity = new(220538249, 76561198180803977);
    private const string Rule = "\"0\" { \"root\" \"WinAppDataLocalLow\" \"path\" \"Game/{Steam3AccountID}\" \"pattern\" \"*.dat\" \"recursive\" \"1\" }";
    private static AutoCloudDefinition Definition(string rules = Rule, string overrides = "") => AutoCloudDefinition.Parse(42, "\"42\" { \"ufs\" { \"savefiles\" { " + rules + " } \"rootoverrides\" { " + overrides + " } } }");
    public static async Task Run(Action<bool, string> check, string root, bool windowsEnvironment = false)
    {
        var localRoot = Path.Combine(root, "AutoCloud roots");
        Directory.CreateDirectory(localRoot);
        var roots = new WindowsAutoCloudRoots(_ => localRoot);
        AutoCloudPathPlan Plan(string rules = Rule, string overrides = "") => AutoCloudPathPlan.Compile(Definition(rules, overrides), Identity, AutoCloudPlatform.Windows, roots);
        void Reject(Action action, string reason)
        {
            try
            {
                action();
                throw new Exception("Unsafe mapping accepted: " + reason);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
            {
                check(true, reason);
            }
        }

        using var history = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "autocloud-history.json")));
        var h = history.RootElement;
        var historical = AutoCloudDefinition.Parse(h.GetProperty("AppId").GetUInt32(), AppInfo(h.GetProperty("AppId").GetUInt32(), h.GetProperty("Ufs")));
        var historyPlan = AutoCloudPathPlan.Compile(historical, Identity, AutoCloudPlatform.Windows, roots);
        var keys = h.GetProperty("Files").EnumerateArray().Select(f => new AutoCloudKey(f.GetProperty("Root").GetString()!, f.GetProperty("Name").GetString()!)).ToArray();
        var pairs = historyPlan.MapAll(keys);
        check(pairs.Length == 27 && pairs.Count(p => p.Cloud.RelativeName.Contains("/Restore_Points1/")) == 23, "historical 1030300 fixture maps all 27 files including 23 nested paths");
        check(pairs.All(p => p.LocalPath == Path.Combine(localRoot, p.Cloud.RelativeName.Replace('/', Path.DirectorySeparatorChar)) && historyPlan.ToCloud(p.LocalPath) == p.Cloud), "all historical AutoCloud names round-trip through substituted Windows root without reading actual saves");
        check(historyPlan.Rules.Single().LocalDirectory.EndsWith("Team Cherry\\Hollow Knight Silksong\\220538249") && historical.Overrides.Count == 2, "Windows skips actual Mac/Linux overrides and expands Steam3 account placeholder");
        check(Identity.Expand("{Steam3AccountID}/{64BitSteamID}") == "220538249/76561198180803977", "both documented account placeholders use distinct verified identity values");
        Reject(() => new AutoCloudIdentity(2, 1).Expand("x"), "mismatched or invalid account identities are rejected");
        Reject(() => Plan(Rule.Replace("{Steam3AccountID}", "{UnknownUser}")), "unknown account placeholder fails closed");
        var plan = Plan();
        var duplicateBasenames = plan.MapAll([new("WinAppDataLocalLow", "Game/220538249/slot1/save.dat"), new("WinAppDataLocalLow", "Game/220538249/slot2/save.dat")]);
        check(duplicateBasenames.Select(p => p.LocalPath).Distinct().Count() == 2 && duplicateBasenames.All(p => plan.ToCloud(p.LocalPath) == p.Cloud), "duplicate basenames in different directories preserve separate cloud identities");
        check(plan.ToLocal(new("winappdatalocallow", "game/220538249/SAVE.DAT")).EndsWith("SAVE.DAT"), "Windows case-insensitive rule matching preserves file spelling");
        Reject(() => plan.MapAll([new("WinAppDataLocalLow", "Game/220538249/a.dat"), new("winappdatalocallow", "game/220538249/A.DAT")]), "case-insensitive cloud collisions are rejected before returning a batch");
        foreach (var bad in new[]
        {
            "../bad.dat",
            "/Game/220538249/a.dat",
            "Game/220538249/../a.dat",
            "Game//220538249/a.dat",
            "Game/220538249/a.dat:stream",
            "Game/220538249/CON.dat",
            "Game/220538249/a.dat.",
            "Game/220538249/a.dat ",
            "Game/220538249/dir\\a.dat",
            "Game/220538249/\0.dat",
            "C:/Game/220538249/a.dat"
        }

        )
            Reject(() => plan.ToLocal(new("WinAppDataLocalLow", bad)), "unsafe cloud path rejected: " + bad.Replace('\0', '_'));
        foreach (var bad in new[]
        {
            "../outside",
            "/",
            "C:/outside",
            "\\\\server\\share",
            "folder/../outside"
        }

        )
            Reject(() => Plan(Rule.Replace("Game/{Steam3AccountID}", bad.Replace("\\", "\\\\"))), "unsafe rule directory rejected: " + bad);
        Reject(() => plan.ToLocal(new("UnknownRoot", "a.dat")), "unknown logical roots never become filesystem paths");
        Reject(() => Definition(Rule.Replace("WinAppDataLocalLow", "UnknownRoot")), "unknown metadata root fails during parsing");
        Reject(() => plan.ToLocal(new("WinAppDataLocalLow", "Game/220538249/file.dat.bak")), "backup files outside the literal extension mask are excluded");
        Reject(() => plan.ToCloud(Path.Combine(root, "outside.dat")), "local file outside all configured directories is rejected");
        Reject(() => plan.ToCloud(Path.Combine(localRoot, "Game", "220538249", "..", "escape.dat")), "local dot-dot input is not silently normalized");
        var flat = Plan(Rule.Replace("\"recursive\" \"1\"", "\"recursive\" \"0\""));
        check(flat.ToLocal(new("WinAppDataLocalLow", "Game/220538249/a.dat")).EndsWith("a.dat"), "nonrecursive rule accepts direct matching files");
        Reject(() => flat.ToLocal(new("WinAppDataLocalLow", "Game/220538249/sub/a.dat")), "nonrecursive rule rejects nested files");
        check(Plan(Rule.Replace("*.dat", "*")).ToLocal(new("WinAppDataLocalLow", "Game/220538249/plain")).EndsWith("plain"), "documented star pattern accepts extensionless files");
        Reject(() => Definition(Rule.Replace("*.dat", "?.dat")), "unverified pattern syntax is explicitly unsupported");
        Reject(() => Definition(Rule.Replace("\"recursive\" \"1\"", "\"recursive\" \"yes\"")), "nonbinary recursive metadata fails closed");
        Reject(() => Definition(Rule.Replace("\"recursive\" \"1\"", "\"recursive\" { }")), "malformed recursive object cannot silently become false");
        Reject(() => Definition(Rule.Replace("\"pattern\"", "\"future-field\" \"x\" \"pattern\"")), "unknown rule fields cannot silently change path semantics");
        Reject(() => Definition(Rule.Replace("\"path\"", "\"path\" \"conflict\" \"path\"")), "duplicate metadata fields are rejected instead of overwriting");
        var windows = Rule.Replace("\"recursive\" \"1\"", "\"recursive\" \"1\" \"platforms\" { \"0\" \"Windows\" }");
        check(Plan(windows).Rules.Count == 1 && Plan(windows.Replace("\"Windows\"", "\"Linux\"")).Rules.Count == 0, "rule platform restriction is applied before resolving Windows paths");
        Reject(() => Plan(windows.Replace("\"Windows\"", "\"UnknownOS\"")), "unknown platform restrictions are rejected");
        const string replace = "\"0\" { \"root\" \"WinAppDataLocalLow\" \"os\" \"Windows\" \"oscompare\" \"=\" \"useinstead\" \"WinMyDocuments\" \"addpath\" \"Prefix\" \"pathtransforms\" { \"0\" { \"find\" \"Game/{Steam3AccountID}\" \"replace\" \"Other/{64BitSteamID}\" } } }";
        var overridden = Plan(overrides: replace);
        var cloud = new AutoCloudKey("WinAppDataLocalLow", "Game/220538249/sub/save.dat");
        var overriddenLocal = overridden.ToLocal(cloud);
        check(overriddenLocal == Path.Combine(localRoot, "Prefix", "Other", "76561198180803977", "sub", "save.dat") && overridden.ToCloud(overriddenLocal) == cloud, "applicable Windows override transforms local path while retaining original logical cloud root/path");
        Reject(() => Plan(overrides: replace.Replace("\"=\"", "\"!=\"")), "unverified OS comparison is not guessed");
        Reject(() => Plan(overrides: replace.Replace("Other/{64BitSteamID}", "../escape")), "applicable transform cannot escape a Windows root");
        Reject(() => Plan(overrides: replace.Replace("Game/{Steam3AccountID}\" \"replace", "Unmatched\" \"replace")), "unmatched transform fails instead of mapping to a guessed directory");
        Reject(() => Definition(overrides: replace + replace.Replace("\"0\" { \"root\"", "\"1\" { \"root\"")), "conflicting overrides are rejected");
        Reject(() => Definition(overrides: replace.Replace("\"addpath\" \"Prefix\"", "\"addpath\" { }")), "malformed override path cannot silently become empty");
        Reject(() => Plan(Rule + Rule.Replace("\"0\"", "\"1\"")), "duplicate mappings are rejected");
        var overlap = Plan(Rule + Rule.Replace("\"0\"", "\"1\"").Replace("*.dat", "*"));
        Reject(() => overlap.ToLocal(new("WinAppDataLocalLow", "Game/220538249/save.dat")), "overlapping masks cannot silently select one mapping");
        var twoRoots = Plan(Rule + Rule.Replace("\"0\"", "\"1\"").Replace("WinAppDataLocalLow", "WinMyDocuments"));
        Reject(() => twoRoots.ToLocal(cloud), "different cloud roots mapping onto one local file are rejected symmetrically");
        Guid observed = default;
        var apiRoots = new WindowsAutoCloudRoots(id =>
        {
            observed = id;
            return localRoot;
        });
        check(apiRoots.Resolve("WinAppDataLocalLow") == localRoot && observed == new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16"), "Windows resolver requests the Known Folder API identifier rather than USERPROFILE concatenation");
        Reject(() => apiRoots.Resolve("SteamCloudDocuments"), "known but unverified root resolution is explicitly unsupported");
        Reject(() => AutoCloudPathPlan.Compile(historical, Identity, AutoCloudPlatform.Linux, roots), "non-Windows filesystem execution is not advertised as verified");
        var blockedAncestor = Path.Combine(localRoot, "not-a-directory");
        File.WriteAllText(blockedAncestor, "fixture");
        Reject(() => Plan(Rule.Replace("Game/{Steam3AccountID}", "not-a-directory")), "a rule directory cannot be an existing regular file");
        if (windowsEnvironment)
            await JunctionCheck(check, localRoot, plan);
        int calls = 0;
        var guardHome = Path.Combine(root, "autocloud-guard");
        var sync = new CloudSyncService(guardHome, (_, _) =>
        {
            calls++;
            throw new Exception("Must not initialize native cloud context");
        }, deletion: new TestBackupDelete(root));
        bool blocked = false;
        try
        {
            await sync.PrepareAsync(new(1030300, Identity.AccountId, Path.Combine(root, "steamcmd.exe"), new(CloudSupport.UnsupportedAutoCloud, null, "offline planner exists")), ConflictChoice.None, null, default);
        }
        catch (NotSupportedException)
        {
            blocked = true;
        }

        check(blocked && calls == 0 && !Directory.Exists(guardHome), "legacy unsupported metadata without verified rules still refuses native initialization, index and backup state");
    }

    private static async Task JunctionCheck(Action<bool, string> check, string localRoot, AutoCloudPathPlan plan)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var parent = Path.Combine(localRoot, "Game", "220538249");
        Directory.CreateDirectory(parent);
        var link = Path.Combine(parent, "linked");
        var target = Path.Combine(localRoot, "outside-target");
        Directory.CreateDirectory(target);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null"
        }

        )
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (process.ExitCode != 0)
            throw new Exception("Junction fixture failed: " + await process.StandardError.ReadToEndAsync());
        try
        {
            bool rejected = false;
            try
            {
                plan.ToLocal(new("WinAppDataLocalLow", "Game/220538249/linked/save.dat"));
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            check(rejected, "existing NTFS junction is rejected before mapping into its target");
        }
        finally
        {
            Directory.Delete(link);
        } // Unlink this fixture only; never recursively follow its target.
    }

    public static void ProbeKnownFolders()
    {
        var roots = new WindowsAutoCloudRoots();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            UserProfileEnvironment = Environment.GetEnvironmentVariable("USERPROFILE"),
            LocalLow = roots.Resolve("WinAppDataLocalLow"),
            Documents = roots.Resolve("WinMyDocuments"),
            Roaming = roots.Resolve("WinAppDataRoaming"),
            SteamApiInitialized = false
        }));
    }

    public static void VerifyRealHistory(string metadataPath, string mappingPath, string outputDirectory)
    {
        // Read-only comparison against prior diagnostics; no Steam/CloudHost and no index generation.
        var destination = Path.GetFullPath(outputDirectory);
        if (!destination.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Diagnostics must remain inside artifacts.");
        using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
        using var mapping = JsonDocument.Parse(File.ReadAllText(mappingPath));
        var appId = metadata.RootElement.GetProperty("AppId").GetUInt32();
        var definition = AutoCloudDefinition.Parse(appId, AppInfo(appId, metadata.RootElement.GetProperty("Ufs")));
        var plan = AutoCloudPathPlan.Compile(definition, Identity, AutoCloudPlatform.Windows, new WindowsAutoCloudRoots());
        var rows = new List<object>();
        foreach (var entry in mapping.RootElement.EnumerateArray())
        {
            // Root 12 is translated explicitly by this historical fixture, never treated as a universal wire enum.
            if (entry.GetProperty("CachedRoot").GetString() != "12")
                throw new InvalidDataException("Unverified historical root.");
            var key = new AutoCloudKey("WinAppDataLocalLow", entry.GetProperty("CachedName").GetString()!);
            var actual = plan.ToLocal(key);
            var expected = entry.GetProperty("LocalPath").GetString();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase) || plan.ToCloud(actual) != key)
                throw new InvalidDataException("Historical mapping differs from real path.");
            using var stream = File.OpenRead(actual);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream));
            if (!hash.Equals(entry.GetProperty("CachedSha1").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Local save differs from historical index.");
            rows.Add(new
            {
                key.Root,
                key.RelativeName,
                LocalPath = actual,
                Sha1 = hash,
                Matched = true
            });
        }

        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "module-real-history.json"), JsonSerializer.Serialize(new
        {
            AppId = appId,
            Mapped = rows.Count,
            Files = rows,
            NativeClientsStarted = 0,
            CloudRequests = 0,
            SaveOrIndexWrites = 0,
            Source = "Historical cache and current local SHA1; not a current server response"
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Module mapped and hash-verified historical local files: " + rows.Count);
    }
}
