using System.Globalization;
using System.Text.RegularExpressions;

namespace Kettle.Core;

public sealed record AppStatus(uint AppId, ulong? SizeOnDiskBytes, bool? NeedsUpdate = null);
public interface IAppStatusProvider
{
    Task<AppStatus> ReadAsync(uint appId, ISteamMetadataCommands commands, CancellationToken token = default);
}

public sealed class SteamAppStatusProvider : IAppStatusProvider
{
    public async Task<AppStatus> ReadAsync(uint appId, ISteamMetadataCommands commands, CancellationToken token = default)
    {
        var status = await AppStatusReader.ReadAsync(appId, commands, token);
        try
        {
            var output = await commands.QueryMetadataAsync([$"app_update_status {appId}"], TimeSpan.FromSeconds(12), token);
            return status with { NeedsUpdate = GameUpdateCheck.Parse(output, appId) };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            // A failed or incomplete request is not proof that an update exists.
            token.ThrowIfCancellationRequested();
            return status;
        }
    }
}

public static class AppStatusReader
{
    public static async Task<AppStatus> ReadAsync(uint appId, ISteamMetadataCommands commands, CancellationToken token = default) => Parse(await commands.QueryMetadataAsync([$"app_status {appId}"], TimeSpan.FromSeconds(12), token), appId);
    public static AppStatus Parse(string output, uint appId)
    {
        SteamResponseErrors.ThrowIfError($"app_status {appId}", output);
        if (!IsComplete(output, appId.ToString(CultureInfo.InvariantCulture)))
            throw new InvalidDataException(L.Format(TextId.CouldNotParseAppStatusResponseNoCompleteStatus, appId));
        if (!Regex.IsMatch(output, $@"\bAppID\s+{appId}\b", RegexOptions.IgnoreCase))
            throw new InvalidDataException(L.Text(TextId.SteamCMDDidNotReturnThisAppSStatus));
        var match = Regex.Match(output, @"\bsize on disk\s*:\s*([0-9]+)\s+bytes\b", RegexOptions.IgnoreCase);
        return new(appId, match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : null);
    }

    internal static bool IsComplete(string body, string appId)
    {
        if (!Regex.IsMatch(body, $@"\bAppID\s+{Regex.Escape(appId)}\b", RegexOptions.IgnoreCase))
            return false;
        var config = Regex.Match(body, @"-\s*user config\s*:", RegexOptions.IgnoreCase);
        if (!config.Success)
            return false;
        var text = body[(config.Index + config.Length)..];
        int depth = 0;
        bool opened = false, quoted = false, escaped = false;
        foreach (var c in text)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (quoted && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
                continue;
            if (c == '{')
            {
                opened = true;
                depth++;
            }
            else if (c == '}' && --depth == 0)
                return opened;
        }

        return false;
    }

    public static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
