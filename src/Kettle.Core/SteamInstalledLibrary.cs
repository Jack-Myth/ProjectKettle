using System.Text.RegularExpressions;

namespace Kettle.Core;

public sealed record SteamVolume(int Index, string Root, bool Mounted);
public sealed record SteamInstalledSnapshot(SteamVolume[] Volumes, Game[] Games, DateTime QueriedUtc);
/// <summary>Live SteamCMD results only. Never reads libraryfolders or appmanifest files.</summary>
public sealed class SteamInstalledLibrary
{
    public async Task<SteamInstalledSnapshot> ReadAsync(ISteamMetadataCommands commands, CancellationToken token = default)
    {
        // Two queries, regardless of the size of the account's owned library. One shared session.
        var volumes = await LibraryFoldersCommand.ReadAsync(commands, token);
        var games = await InstalledAppsCommand.ReadAsync(commands, token);
        return new(volumes, games, DateTime.UtcNow);
    }

    public static SteamVolume[] ParseVolumes(string text)
    {
        var matches = Regex.Matches(text, "Index\\s+(\\d+),\\s*ContentID\\s+\\d+,\\s*Path\\s+\"([^\"]+)\"[^\\r\\n]*Mounted\\s+(yes|no)", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
            throw new InvalidDataException("SteamCMD 未返回可识别的库目录；不会改用本地扫描。");
        return matches.Select(m => new SteamVolume(int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value.Equals("yes", StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public static Game[] ParseInstalled(string text)
    {
        var header = Regex.Match(text, @"\b(\d+)\s+apps installed\s*:", RegexOptions.IgnoreCase);
        if (!header.Success)
            throw new InvalidDataException("SteamCMD 未返回可识别的安装列表；安装状态尚未确认。");
        var matches = Regex.Matches(text, "AppID\\s+(\\d+)\\s*:\\s*\"([^\"]*)\"\\s*:\\s*([^\\r\\n]+)", RegexOptions.IgnoreCase);
        if (!int.TryParse(header.Groups[1].Value, out var expected) || expected != matches.Count)
            throw new InvalidDataException("SteamCMD 安装列表不完整，不能确认安装状态。");
        var games = matches.Select(m => new Game(m.Groups[2].Value, m.Groups[1].Value, m.Groups[3].Value.Trim(), "已安装", "尚未查询", "") { MetadataStatus = "SteamCMD 实时" }).ToArray();
        if (games.Any(g => !uint.TryParse(g.AppId, out _) || !Path.IsPathFullyQualified(g.InstallPath)) || games.Select(g => g.AppId).Distinct().Count() != games.Length)
            throw new InvalidDataException("SteamCMD 返回的安装信息无效。");
        return games;
    }
}
