using System.Text.RegularExpressions;

namespace Kettle.Core;

public class SteamCommandExecutionException(string message) : IOException(message);
// Pure business utility; never used by the terminal or response framer.
public static class SteamResponseErrors
{
    public static string? ErrorLine(string response)
    {
        var match = Regex.Match(response, @"(?im)^[ \t]*ERROR(?:!|:|\b)[ \t]*([^\r\n]*)");
        if (!match.Success)
            return null;
        var reason = match.Groups[1].Value.Trim();
        return reason.Length <= 512 ? reason : reason[..512];
    }

    public static void ThrowIfError(string command, string response)
    {
        if (ErrorLine(response) is { } reason)
            throw new SteamCommandExecutionException($"{command} 执行失败：{reason}");
    }
}
