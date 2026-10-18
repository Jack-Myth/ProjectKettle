using System.Text.RegularExpressions;

namespace Kettle.Core;

public sealed record RunningApp(uint AppId, ulong GameId, int ProcessId, int[] AssociatedProcesses, string CommandLine, bool IsAuxiliary);
public sealed record AppsRunningSnapshot(RunningApp[] Apps)
{
    public bool HasGame(uint id) => Apps.Any(app => app.AppId == id && !app.IsAuxiliary);
    public static AppsRunningSnapshot Parse(string output)
    {
        output = output.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(output))
            return new([]);
        var headers = Regex.Matches(output, @"(?m)^[ \t]*-[ \t]+AppID[ \t]+(\d+),[ \t]*GameID[ \t]+(\d+),[ \t]*ProcessID[ \t]+(\d+),[ \t]*IP:Port[ \t]+[^\s]+[ \t]*$", RegexOptions.CultureInvariant);
        if (headers.Count == 0 || !string.IsNullOrWhiteSpace(output[..headers[0].Index]))
            throw Invalid();
        var apps = new List<RunningApp>();
        for (int i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            if (!uint.TryParse(header.Groups[1].Value, out var id) || id == 0 || !ulong.TryParse(header.Groups[2].Value, out var game) || game == 0 || !int.TryParse(header.Groups[3].Value, out var pid) || pid <= 0)
                throw Invalid();
            var body = output[(header.Index + header.Length)..(i + 1 < headers.Count ? headers[i + 1].Index : output.Length)];
            var fields = Regex.Match(body, @"\A\s*commandline[ \t]+(?<command>[\s\S]*?)\r?\n[ \t]*extra info[ \t]+""[^""\r\n]*""\s*associated PIDs[ \t]*\((?<pids>[^)]*)\)\s*dwLastIsRunningCheck[ \t]+\d+\s*\z", RegexOptions.CultureInvariant);
            if (!fields.Success)
                throw Invalid();
            var command = fields.Groups["command"].Value.Trim();
            if (!command.StartsWith('"') || !command.EndsWith('"'))
                throw Invalid();
            var processText = fields.Groups["pids"].Value;
            if (!Regex.IsMatch(processText, @"\A\s*(?:\d+\s*\{parent:\s*\d+\s*\}\s*,?\s*)*\z", RegexOptions.CultureInvariant))
                throw Invalid();
            var associated = new HashSet<int>();
            foreach (Match match in Regex.Matches(processText, @"(\d+)\s*\{parent:"))
            {
                if (!int.TryParse(match.Groups[1].Value, out var child) || child <= 0)
                    throw Invalid();
                associated.Add(child);
            }

            var executable = Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe");
            var unquoted = command.Replace("\n", "").TrimStart('"');
            var auxiliary = unquoted.StartsWith(executable, StringComparison.OrdinalIgnoreCase) && (unquoted.Length == executable.Length || unquoted[executable.Length] is '"' or ' ');
            apps.Add(new(id, game, pid, associated.ToArray(), command, auxiliary));
        }

        if (apps.Select(app => (app.AppId, app.ProcessId)).Distinct().Count() != apps.Count)
            throw Invalid();
        return new(apps.ToArray());
    }

    private static InvalidDataException Invalid() => new(L.Text(TextId.AppsRunningSnapshotIsIncompleteOrUnrecognizedGameExit));
}
