using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Kettle.Core;

public enum AppKind
{
    Unknown,
    Game,
    Demo,
    Dlc,
    Tool,
    Other
}

public sealed class LibraryCacheException : IOException
{
    public LibraryCacheException() : base(L.Text(TextId.CouldNotWriteTheLibraryCacheInTheApplication))
    {
    }
}

public sealed record OwnedApp(uint AppId, string Name, AppKind Kind, bool Resolved)
{
    public bool ShowInLibrary => Kind is AppKind.Unknown or AppKind.Game or AppKind.Demo;
}

public sealed record LicensePackage(uint PackageId, uint[] AppIds, int Expected, bool Truncated);
public sealed record OwnedLibrarySnapshot(OwnedApp[] Apps, bool OwnershipComplete, bool MetadataComplete, DateTime UpdatedUtc, string Message);
public interface IOwnedLibraryProvider
{
    OwnedLibrarySnapshot? ReadCache(string account);
}

public static class SteamLibraryParser
{
    public static LicensePackage[] Licenses(string text)
    {
        var result = new Dictionary<uint, LicensePackage>();
        foreach (Match block in Regex.Matches(text, @"License packageID\s+(\d+)\s*:(.*?)(?=License packageID\s+\d+\s*:|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (!block.Groups[2].Value.Contains("Active", StringComparison.OrdinalIgnoreCase) || block.Groups[2].Value.Contains("Inactive", StringComparison.OrdinalIgnoreCase))
                continue;
            var apps = Regex.Match(block.Groups[2].Value, @"-\s*Apps\s*:\s*([^\r\n]*)", RegexOptions.IgnoreCase);
            if (!apps.Success)
                throw new InvalidDataException(L.Text(TextId.SomeLicensesDidNotReturnAnAppList));
            var count = Regex.Match(apps.Groups[1].Value, @"\((\d+) in total\)");
            var body = Regex.Replace(apps.Groups[1].Value, @"\([^)]*\)", "");
            var ids = Regex.Matches(body, @"\d+").Select(m => uint.Parse(m.Value)).Distinct().ToArray();
            var expected = count.Success ? int.Parse(count.Groups[1].Value) : ids.Length;
            var id = uint.Parse(block.Groups[1].Value);
            result[id] = new(id, ids, expected, body.Contains("...") || expected > ids.Length);
        }

        if (result.Count == 0 && !Regex.IsMatch(text, @"(?:0\s+licenses|no\s+licenses)", RegexOptions.IgnoreCase))
            throw new InvalidDataException(L.Text(TextId.CouldNotReadAccountLicensesRetry));
        return result.Values.ToArray();
    }

    public static Dictionary<uint, uint[]> Packages(string text)
    {
        var result = new Dictionary<uint, uint[]>();
        foreach (var (id, body) in NumericBlocks(text))
        {
            var apps = Section(body, "appids");
            if (apps is null)
                continue;
            result[id] = Regex.Matches(apps, "\"[0-9]+\"\\s*\"([0-9]+)\"").Select(m => uint.Parse(m.Groups[1].Value)).Distinct().ToArray();
        }

        return result;
    }

    public static Dictionary<uint, OwnedApp> Apps(string text)
    {
        var result = new Dictionary<uint, OwnedApp>();
        foreach (var (id, body) in NumericBlocks(text))
        {
            var common = Section(body, "common") ?? body;
            var values = TopValues(common);
            if (!values.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name) || !values.TryGetValue("type", out var type))
                continue;
            var kind = type.ToLowerInvariant() switch
            {
                "game" => AppKind.Game,
                "demo" => AppKind.Demo,
                "dlc" => AppKind.Dlc,
                "tool" or "config" or "dedicatedserver" or "driver" => AppKind.Tool,
                _ => AppKind.Other
            };
            result[id] = new(id, name, kind, true);
        }

        return result;
    }

    private static Dictionary<string, string> TopValues(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int depth = 0;
        string? key = null;
        foreach (Match token in Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|[{}]"))
        {
            if (token.Value == "{")
            {
                depth++;
                key = null;
                continue;
            }

            if (token.Value == "}")
            {
                depth--;
                continue;
            }

            if (depth != 0)
                continue;
            var value = token.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (key is null)
                key = value;
            else
            {
                result[key] = value;
                key = null;
            }
        }

        return result;
    }

    private static IEnumerable<(uint Id, string Body)> NumericBlocks(string text)
    {
        var position = 0;
        var head = new Regex("\"([0-9]+)\"\\s*\\{", RegexOptions.CultureInvariant);
        while (position < text.Length)
        {
            var match = head.Match(text, position);
            if (!match.Success)
                yield break;
            var start = match.Index + match.Length - 1;
            var end = EndBrace(text, start);
            if (end < 0)
                yield break;
            yield return (uint.Parse(match.Groups[1].Value), text[(start + 1)..end]);
            position = end + 1;
        }
    }

    private static string? Section(string text, string key)
    {
        var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\\{", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        var start = match.Index + match.Length - 1;
        var end = EndBrace(text, start);
        return end < 0 ? null : text[(start + 1)..end];
    }

    private static int EndBrace(string text, int start)
    {
        int depth = 0;
        bool quote = false, escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quote)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    quote = false;
                continue;
            }

            if (c == '"')
            {
                quote = true;
                continue;
            }

            if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return i;
        }

        return -1;
    }
}

/// <summary>Read-only legacy cache compatibility. No acquisition or refresh requests.</summary>
public sealed class SteamOwnedLibraryProvider(string appDirectory) : IOwnedLibraryProvider
{
    public string CachePath(string account) => Path.Combine(appDirectory, "data", "library", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Trim().ToLowerInvariant()))) + ".json");
    public OwnedLibrarySnapshot? ReadCache(string account)
    {
        var path = CachePath(account);
        if (!File.Exists(path))
            return null;
        return KettleJson.Deserialize<OwnedLibrarySnapshot>(File.ReadAllText(path));
    }
}
