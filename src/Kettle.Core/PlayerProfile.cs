using System.Text.RegularExpressions;

namespace Kettle.Core;

public sealed record PlayerProfile(string AccountName, string? PersonaName = null, string? SteamId = null);
public static class PlayerProfileReader
{
    public static async Task<PlayerProfile> ReadAsync(string account, ISteamMetadataCommands commands, CancellationToken token)
    {
        var info = await commands.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(15), token);
        SteamResponseErrors.ThrowIfError("info", info);
        return Parse(account, info);
    }

    // Only explicit public identity labels are accepted. The rest of `info` is neither exposed nor retained.
    public static PlayerProfile Parse(string account, string text)
    {
        var nickname = Regex.Match(text, @"(?im)^[ \t]*(?:persona name|persona|display name|nickname)[ \t]*[:=][ \t]*([^\r\n]+)");
        var steamId = Regex.Match(text, @"(?im)^[ \t]*(?:steam\s*id(?:64)?)[ \t]*[:=][ \t]*(\d{17}|\[U:\d+:\d+\])");
        var name = nickname.Success ? nickname.Groups[1].Value.Trim().Trim('"') : null;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            name = null;
        return new(account, name, steamId.Success ? steamId.Groups[1].Value : null);
    }
}
