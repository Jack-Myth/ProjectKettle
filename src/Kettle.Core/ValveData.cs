using System.Text.RegularExpressions;

namespace Kettle.Core;

public sealed class ValveData
{
    public string? Value
    {
        get; private init;
    }
    public Dictionary<string, ValveData> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ValveData? this[string key] => Children.GetValueOrDefault(key);
    public static ValveData Parse(string text, bool rejectDuplicates = false)
    {
        var tokens = Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|[{}]").Select(m => m.Value is "{" or "}" ? m.Value : m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")).ToArray();
        int position = 0;
        ValveData Read(bool nested)
        {
            var node = new ValveData();
            while (position < tokens.Length)
            {
                var key = tokens[position++];
                if (key == "}")
                {
                    if (!nested)
                        throw new InvalidDataException();
                    return node;
                }

                if (key == "{" || position == tokens.Length)
                    throw new InvalidDataException();
                var value = tokens[position++];
                if (rejectDuplicates && node.Children.ContainsKey(key))
                    throw new InvalidDataException("Duplicate metadata key: " + key);
                node.Children[key] = value == "{" ? Read(true) : new ValveData
                {
                    Value = value
                };
            }

            if (nested)
                throw new InvalidDataException("Incomplete app metadata.");
            return node;
        }

        return Read(false);
    }
}

public enum CloudSupport
{
    None,
    RemoteStorage,
    UnsupportedAutoCloud,
    AutoCloud
}

public sealed record AppLaunchMetadata(CloudSupport Cloud, string? Executable, string? CloudDescription)
{
    public GameLaunchOption[]? LaunchEntries { get; init; }
    public string? AutoCloudAppInfo
    {
        get; init;
    }

    public static AppLaunchMetadata Read(uint id, string output)
    {
        var response = AppInfoCommand.Parse(id, output);
        var app = response.Data[id.ToString()]!;
        var ufs = app["ufs"];
        var cloud = ufs?["savefiles"]?.Children.Count > 0 || ufs?["rootoverrides"]?.Children.Count > 0 ? CloudSupport.UnsupportedAutoCloud : ufs is null || !ulong.TryParse(ufs["quota"]?.Value, out var quota) || quota == 0 ? CloudSupport.None : CloudSupport.RemoteStorage;
        var entries = GameLaunchOption.Read(app["config"]?["launch"]);
        var executable = (entries?.FirstOrDefault(e => e.Type is "default" or "") ?? entries?.FirstOrDefault())?.Executable;
        var description = cloud == CloudSupport.UnsupportedAutoCloud ? string.Join("; ", ufs?["savefiles"]?.Children.Values.Select(rule => $"{rule["root"]?.Value}/{rule["path"]?.Value} ({rule["pattern"]?.Value})") ?? []) : null;
        return new(cloud == CloudSupport.UnsupportedAutoCloud ? CloudSupport.AutoCloud : cloud, executable, description)
        {
            LaunchEntries = entries,
            AutoCloudAppInfo = cloud == CloudSupport.UnsupportedAutoCloud ? response.Vdf : null
        };
    }
}
