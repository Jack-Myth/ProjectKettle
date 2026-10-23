namespace Kettle.Core;

public static class AppLaunchMetadataReader
{
    public static AppLaunchMetadata Read(uint id, string output)
    {
        var response = AppInfoCommand.Parse(id, output);
        var app = response.Data[id.ToString()]!;
        var ufs = app["ufs"];
        var cloud = ufs?["savefiles"]?.Children.Count > 0 || ufs?["rootoverrides"]?.Children.Count > 0 ? CloudSupport.UnsupportedAutoCloud : ufs is null || !ulong.TryParse(ufs["quota"]?.Value, out var quota) || quota == 0 ? CloudSupport.None : CloudSupport.RemoteStorage;
        var entries = GameLaunchOptionReader.Read(app["config"]?["launch"]);
        var executable = (entries?.FirstOrDefault(e => e.Type is "default" or "") ?? entries?.FirstOrDefault())?.Executable;
        var description = cloud == CloudSupport.UnsupportedAutoCloud ? string.Join("; ", ufs?["savefiles"]?.Children.Values.Select(rule => $"{rule["root"]?.Value}/{rule["path"]?.Value} ({rule["pattern"]?.Value})") ?? []) : null;
        return new(cloud == CloudSupport.UnsupportedAutoCloud ? CloudSupport.AutoCloud : cloud, executable, description)
        {
            LaunchEntries = entries,
            AutoCloudAppInfo = cloud == CloudSupport.UnsupportedAutoCloud ? response.Vdf : null
        };
    }
}
