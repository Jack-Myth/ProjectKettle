using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Kettle.Core;
// Offline path planning only. This module never connects, downloads, writes files or updates an index.
public enum AutoCloudPlatform
{
    Windows,
    MacOS,
    Linux
}

public sealed record AutoCloudIdentity(uint AccountId, ulong SteamId64)
{
    public string Expand(string text)
    {
        if (AccountId == 0 || SteamId64 != 76561197960265728ul + AccountId)
            throw new InvalidDataException("Steam account identifiers do not match.");
        var expanded = text.Replace("{Steam3AccountID}", AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("{64BitSteamID}", SteamId64.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (expanded.Contains('{') || expanded.Contains('}'))
            throw new NotSupportedException("Unknown AutoCloud placeholder.");
        return expanded;
    }
}

public sealed record AutoCloudRule(string Root, string Path, string Pattern, bool Recursive, IReadOnlyList<AutoCloudPlatform> Platforms);
public sealed record AutoCloudPathTransform(string Find, string Replace);
public sealed record AutoCloudOverride(string Root, AutoCloudPlatform Platform, string NewRoot, string AddPath, IReadOnlyList<AutoCloudPathTransform> Transforms);
public sealed record AutoCloudDefinition(uint AppId, IReadOnlyList<AutoCloudRule> Rules, IReadOnlyList<AutoCloudOverride> Overrides)
{
    public bool NeedsGameInstall(AutoCloudPlatform platform) => Rules.Where(r => r.Platforms.Count == 0 || r.Platforms.Contains(platform)).Any(r => (Overrides.SingleOrDefault(o => o.Root.Equals(r.Root, StringComparison.OrdinalIgnoreCase) && o.Platform == platform)?.NewRoot ?? r.Root).Equals("GameInstall", StringComparison.OrdinalIgnoreCase));
    public static AutoCloudDefinition Parse(uint appId, string appInfo)
    {
        if (appId == 0)
            throw new ArgumentOutOfRangeException(nameof(appId));
        var app = ValveData.Parse(appInfo, rejectDuplicates: true)[appId.ToString()] ?? throw new InvalidDataException("Missing requested AppID.");
        var ufs = app["ufs"] ?? throw new InvalidDataException("Missing ufs.");
        if (ufs.Value is not null || ufs["savefiles"] is not { Value: null } || ufs["rootoverrides"]?.Value is not null)
            throw new InvalidDataException("Invalid AutoCloud containers.");
        var rules = new List<AutoCloudRule>();
        foreach (var entry in ufs["savefiles"]?.Children.Values.AsEnumerable() ?? [])
        {
            Fields(entry, "root", "path", "pattern", "recursive", "platforms");
            var root = AutoCloudRootNames.Canonical(Required(entry, "root"));
            var path = Required(entry, "path", allowEmpty: true);
            var pattern = Required(entry, "pattern");
            AutoCloudPathSafety.Pattern(pattern);
            var recursive = entry["recursive"] is null ? "0" : Required(entry, "recursive");
            if (recursive is not ("0" or "1"))
                throw new InvalidDataException("Invalid recursive flag.");
            var platforms = new List<AutoCloudPlatform>();
            if (entry["platforms"] is { } restrictions)
            {
                var values = restrictions.Value is { } scalar ? [scalar] : restrictions.Children.Values.Select(v => v.Value ?? throw new InvalidDataException("Nested platform restriction.")).ToArray();
                foreach (var value in values)
                {
                    if (value.Equals("all", StringComparison.OrdinalIgnoreCase) || value.Equals("CrossPlatform", StringComparison.OrdinalIgnoreCase))
                        continue;
                    platforms.Add(Platform(value));
                }

                if (platforms.Count > 0 && values.Any(v => v.Equals("all", StringComparison.OrdinalIgnoreCase) || v.Equals("CrossPlatform", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Ambiguous platform restriction.");
            }

            rules.Add(new(root, path, pattern, recursive == "1", platforms.Distinct().ToArray()));
        }

        if (rules.Count == 0)
            throw new InvalidDataException("No AutoCloud save rules.");
        var overrides = new List<AutoCloudOverride>();
        foreach (var entry in ufs["rootoverrides"]?.Children.Values.AsEnumerable() ?? [])
        {
            Fields(entry, "root", "os", "oscompare", "useinstead", "addpath", "pathtransforms");
            if ((entry["oscompare"] is null ? "=" : Required(entry, "oscompare")) != "=")
                throw new NotSupportedException("Unverified override OS comparison.");
            if (entry["pathtransforms"]?.Value is not null)
                throw new InvalidDataException("Invalid transform container.");
            var transforms = new List<AutoCloudPathTransform>();
            foreach (var transform in entry["pathtransforms"]?.Children.Values.AsEnumerable() ?? [])
            {
                Fields(transform, "find", "replace");
                transforms.Add(new(Required(transform, "find", true), Required(transform, "replace", true)));
            }

            // Steam's order/overlap semantics for multiple transforms have not been verified here.
            if (transforms.Count > 1)
                throw new NotSupportedException("Multiple path transforms require separate validation.");
            overrides.Add(new(AutoCloudRootNames.Canonical(Required(entry, "root")), Platform(Required(entry, "os")), AutoCloudRootNames.Canonical(Required(entry, "useinstead")), entry["addpath"] is null ? "" : Required(entry, "addpath", true), transforms));
        }

        if (overrides.GroupBy(o => (o.Root, o.Platform)).Any(g => g.Count() != 1))
            throw new InvalidDataException("Conflicting root overrides.");
        return new(appId, rules, overrides);
    }

    private static AutoCloudPlatform Platform(string text) => Enum.TryParse<AutoCloudPlatform>(text, true, out var value) && Enum.IsDefined(value) && !int.TryParse(text, out _) ? value : throw new NotSupportedException("Unknown AutoCloud platform: " + text);
    private static string Required(ValveData node, string key, bool allowEmpty = false) => node[key]?.Value is { } value && (allowEmpty || value.Length != 0) ? value : throw new InvalidDataException("Missing scalar field: " + key);
    private static void Fields(ValveData node, params string[] supported)
    {
        if (node.Value is not null || node.Children.Keys.Any(k => !supported.Contains(k, StringComparer.OrdinalIgnoreCase)))
            throw new NotSupportedException("Unverified AutoCloud rule field.");
    }
}

public static class AutoCloudRootNames
{
    private static readonly string[] Known = ["GameInstall", "WinMyDocuments", "WinAppDataLocal", "WinAppDataLocalLow", "WinAppDataRoaming", "WinSavedGames", "MacHome", "MacAppSupport", "MacDocuments", "LinuxHome", "LinuxXdgDataHome", "SteamCloudDocuments"];
    public static string Canonical(string root) => Known.FirstOrDefault(r => r.Equals(root, StringComparison.OrdinalIgnoreCase)) ?? throw new NotSupportedException("Unknown AutoCloud root: " + root);
}

public interface IAutoCloudRoots
{
    string Resolve(string root);
}

public sealed class WindowsAutoCloudRoots(Func<Guid, string>? lookup = null) : IAutoCloudRoots
{
    private static readonly Dictionary<string, Guid> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WinMyDocuments"] = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        ["WinAppDataLocal"] = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
        ["WinAppDataLocalLow"] = new("A520A1A4-1780-4FF6-BD18-167343C5AF16"),
        ["WinAppDataRoaming"] = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"),
        ["WinSavedGames"] = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4")
    };
    public string Resolve(string root)
    {
        if (!Folders.TryGetValue(AutoCloudRootNames.Canonical(root), out var id))
            throw new NotSupportedException("This root has no verified Windows resolver.");
        return AutoCloudPathSafety.Absolute(lookup?.Invoke(id) ?? KnownFolder(id));
    }

    private static string KnownFolder(Guid id)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var result = SHGetKnownFolderPath(ref id, 0, 0, out var pointer);
        if (result != 0)
            Marshal.ThrowExceptionForHR(result);
        try
        {
            return Marshal.PtrToStringUni(pointer) ?? throw new IOException("Known Folder returned no path.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
}

// Logical root + canonical relative name, NOT an unverified server wire filename/prefix index.
public sealed record AutoCloudKey(string Root, string RelativeName);
public sealed record AutoCloudPathPair(AutoCloudKey Cloud, string LocalPath);
public sealed record AutoCloudCompiledRule(string CloudRoot, string CloudDirectory, string LocalRoot, string LocalDirectory, string Pattern, bool Recursive, uint Platforms = uint.MaxValue);
public sealed class AutoCloudPathPlan
{
    private readonly AutoCloudCompiledRule[] rules;
    public IReadOnlyList<AutoCloudCompiledRule> Rules => Array.AsReadOnly(rules);

    private AutoCloudPathPlan(AutoCloudCompiledRule[] rules)
    {
        this.rules = rules;
    }

    public static AutoCloudPathPlan Compile(AutoCloudDefinition definition, AutoCloudIdentity identity, AutoCloudPlatform platform, IAutoCloudRoots roots, string? installPath = null)
    {
        if (platform != AutoCloudPlatform.Windows)
            throw new PlatformNotSupportedException("Only Windows filesystem planning is verified. Other OS overrides are parsed, not executed.");
        identity.Expand("");
        var compiled = new List<AutoCloudCompiledRule>();
        foreach (var rule in definition.Rules)
        {
            if (rule.Platforms.Count > 0 && !rule.Platforms.Contains(platform))
                continue;
            var cloudRoot = AutoCloudRootNames.Canonical(rule.Root);
            AutoCloudPathSafety.Pattern(rule.Pattern);
            var cloudDirectory = AutoCloudPathSafety.Relative(identity.Expand(rule.Path), true, metadata: true);
            var localDirectory = cloudDirectory;
            var localRootName = cloudRoot;
            var applicable = definition.Overrides.Where(o => AutoCloudRootNames.Canonical(o.Root) == cloudRoot && o.Platform == platform).ToArray();
            if (applicable.Length > 1)
                throw new InvalidDataException("Conflicting applicable overrides.");
            var replace = applicable.SingleOrDefault();
            if (replace is not null)
            {
                localRootName = replace.NewRoot;
                foreach (var transform in replace.Transforms)
                {
                    var find = AutoCloudPathSafety.Relative(identity.Expand(transform.Find), true, metadata: true);
                    var replacement = AutoCloudPathSafety.Relative(identity.Expand(transform.Replace), true, metadata: true);
                    if (!TrySuffix(localDirectory, find, out var suffix))
                        throw new NotSupportedException("Override transform is not an anchored path match.");
                    localDirectory = Join(replacement, suffix);
                }

                localDirectory = Join(AutoCloudPathSafety.Relative(identity.Expand(replace.AddPath), true, metadata: true), localDirectory);
            }

            // GameInstall is per-app SteamCMD context, never a KnownFolder or the
            // directory of this process, SteamCMD, an executable, or a guessed manifest.
            var root = AutoCloudRootNames.Canonical(localRootName) == "GameInstall" ? GameInstall(installPath) : AutoCloudPathSafety.Absolute(roots.Resolve(localRootName));
            var directory = AutoCloudPathSafety.Combine(root, localDirectory);
            AutoCloudPathSafety.NoLinks(directory, directoryOnly: true);
            compiled.Add(new(cloudRoot, cloudDirectory, root, directory, rule.Pattern, rule.Recursive, rule.Platforms.Count == 0 ? uint.MaxValue : 1));
        }

        if (compiled.Distinct().Count() != compiled.Count)
            throw new InvalidDataException("Duplicate AutoCloud mappings.");
        return new(compiled.ToArray());
    }

    private static string GameInstall(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new IOException(L.Text(TextId.SteamCMDHasNotConfirmedTheInstallDirectoryRefreshInstalled));
        var root = AutoCloudPathSafety.Absolute(path);
        if (!Directory.Exists(root))
            throw new IOException(L.Text(TextId.TheInstallDirectoryReportedBySteamCMDIsInaccessibleConnect));
        return root;
    }

    public string ToLocal(AutoCloudKey input)
    {
        var key = Canonical(input);
        var candidates = LocalCandidates(key).ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException("Cloud path is unmatched or ambiguous.");
        var path = candidates[0];
        var reverse = CloudCandidates(path).ToArray();
        if (reverse.Length != 1 || reverse[0].Root != key.Root || !reverse[0].RelativeName.Equals(key.RelativeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mapping is not one-to-one.");
        AutoCloudPathSafety.NoLinks(path);
        return path;
    }

    public AutoCloudKey ToCloud(string input)
    {
        var path = AutoCloudPathSafety.Absolute(input);
        AutoCloudPathSafety.NoLinks(path);
        var candidates = CloudCandidates(path).ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException("Local path is unmatched or ambiguous.");
        var reverse = LocalCandidates(candidates[0]).ToArray();
        if (reverse.Length != 1 || !reverse[0].Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mapping is not one-to-one.");
        return candidates[0];
    }

    public AutoCloudPathPair[] MapAll(IEnumerable<AutoCloudKey> keys)
    {
        var pairs = keys.Select(Canonical).Select(k => new AutoCloudPathPair(k, ToLocal(k))).ToArray();
        if (pairs.Select(p => p.Cloud.Root + "/" + p.Cloud.RelativeName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pairs.Length || pairs.Select(p => p.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pairs.Length)
            throw new InvalidDataException("Cloud/local path collision.");
        return pairs;
    }

    public uint PlatformsFor(AutoCloudKey input)
    {
        var key = Canonical(input);
        _ = ToLocal(key);
        return rules.Single(r => r.CloudRoot == key.Root && TrySuffix(key.RelativeName, r.CloudDirectory, out var suffix) && Matches(r, suffix)).Platforms;
    }

    private IEnumerable<string> LocalCandidates(AutoCloudKey key)
    {
        foreach (var rule in rules)
            if (rule.CloudRoot == key.Root && TrySuffix(key.RelativeName, rule.CloudDirectory, out var suffix) && Matches(rule, suffix))
                yield return AutoCloudPathSafety.Combine(rule.LocalDirectory, suffix);
    }

    private IEnumerable<AutoCloudKey> CloudCandidates(string path)
    {
        foreach (var rule in rules)
        {
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(rule.LocalDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            var suffix = AutoCloudPathSafety.Relative(Path.GetRelativePath(rule.LocalDirectory, path), false, metadata: true);
            if (Matches(rule, suffix))
                yield return new(rule.CloudRoot, Join(rule.CloudDirectory, suffix));
        }
    }

    private static bool Matches(AutoCloudCompiledRule rule, string suffix) => suffix.Length > 0 && (rule.Recursive || !suffix.Contains('/')) && AutoCloudPathSafety.Pattern(rule.Pattern).IsMatch(suffix.Split('/')[^1]);
    private static AutoCloudKey Canonical(AutoCloudKey key) => new(AutoCloudRootNames.Canonical(key.Root), AutoCloudPathSafety.Relative(key.RelativeName, false, metadata: false));
    private static string Join(string prefix, string suffix) => prefix.Length == 0 ? suffix : suffix.Length == 0 ? prefix : prefix + "/" + suffix;
    private static bool TrySuffix(string path, string prefix, out string suffix)
    {
        if (prefix.Length == 0)
        {
            suffix = path;
            return true;
        }

        if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            suffix = "";
            return true;
        }

        if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            suffix = path[(prefix.Length + 1)..];
            return true;
        }

        suffix = "";
        return false;
    }
}

internal static class AutoCloudPathSafety
{
    public static string Relative(string text, bool allowEmpty, bool metadata)
    {
        if (!metadata && text.Contains('\\'))
            throw new InvalidDataException("Cloud keys require canonical forward slashes.");
        text = text.Replace('\\', '/');
        if (text.StartsWith('/'))
            throw new InvalidDataException("Rooted relative path.");
        if (allowEmpty)
            text = text.TrimEnd('/');
        if (allowEmpty && text is "" or ".")
            return "";
        if (text.Length == 0 || text.StartsWith('/') || text.Contains(':'))
            throw new InvalidDataException("Rooted or empty relative path.");
        foreach (var part in text.Split('/'))
            Segment(part);
        return text;
    }

    public static string Absolute(string text)
    {
        // UNC/device namespaces need their own verified security policy; do not silently reinterpret them.
        if (!Regex.IsMatch(text, @"^[A-Za-z]:[\\/]") || text.Replace('\\', '/').Split('/').Any(p => p is "." or ".."))
            throw new NotSupportedException("Expected canonical Windows local-volume path.");
        var path = Path.GetFullPath(text);
        var tail = path[Path.GetPathRoot(path)!.Length..];
        foreach (var part in tail.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            Segment(part);
        return Path.TrimEndingDirectorySeparator(path);
    }

    public static string Combine(string directory, string relative)
    {
        var root = Absolute(directory);
        relative = Relative(relative, true, metadata: false);
        if (relative.Length == 0)
            return root;
        var path = Absolute(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path escaped its root.");
        return path;
    }

    public static Regex Pattern(string text)
    {
        if (text.Contains('?') || text.Contains('[') || text.Contains(']'))
            throw new NotSupportedException("Only literal names and '*' masks are verified.");
        Segment(text.Replace('*', 'x'));
        return new("^" + Regex.Escape(text).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private static void Segment(string part)
    {
        if (part.Length is 0 or > 240 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || "<>:\"/\\|?*{}".Contains(c)) || Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Unsafe path component.");
    }

    public static void NoLinks(string path, bool directoryOnly = false)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked AutoCloud path is not allowed.");
                if ((directoryOnly || current != path) && (attributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("A path ancestor is not a directory.");
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
