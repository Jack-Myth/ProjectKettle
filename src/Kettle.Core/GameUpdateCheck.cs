using System.Globalization;
using System.Text.RegularExpressions;

namespace Kettle.Core;

public static class GameUpdateCheck
{
    public static bool? Parse(string output, uint appId)
    {
        var headers = Regex.Matches(output, @"(?im)^App Update Info\s+(\d+)\s*:\s*([^\r\n]*)");
        var builds = Regex.Matches(output, @"(?im)^\s*-\s*BuildIDs\s*:\s*(\d+)\s*->\s*(\d+)\s*$");
        if (headers.Count != 1 || headers[0].Groups[1].Value != appId.ToString(CultureInfo.InvariantCulture)
            || !headers[0].Groups[2].Value.Trim().Equals("No Error", StringComparison.OrdinalIgnoreCase)
            || builds.Count != 1 || !Regex.IsMatch(output, @"(?im)^\s*-\s*Memory\s*:.*\bdisk\s*$")
            || !ulong.TryParse(builds[0].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var installed)
            || !ulong.TryParse(builds[0].Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var target)
            || installed == 0 || target == 0)
            return null;
        return installed != target;
    }
}
