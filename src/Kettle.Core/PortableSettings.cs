using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kettle.Core;

public sealed record UserSettings(string Username = "", string? SteamCmd = null, string DefaultPage = "Installed", int DefaultSort = 0, bool ShowDemos = true, bool ShowUnresolvedApps = true)
{
    [JsonIgnore]
    public bool HasIgnoredLegacyLibraries => Extra?.Keys.Any(key => key.Equals("LocalLibraries", StringComparison.OrdinalIgnoreCase) || key.Equals("LocalLibraryPaths", StringComparison.OrdinalIgnoreCase)) == true;

    // Extension data needs a setter for source-generated deserialization.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra
    {
        get; set;
    }
}

public sealed class PortableSettings(string appDirectory)
{
    public string FilePath { get; } = Path.Combine(appDirectory, "data", "settings.json");

    public UserSettings Load()
    {
        if (!File.Exists(FilePath))
            return new();
        var saved = KettleJson.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("设置文件内容为空。");
        return saved with
        {
            Username = saved.Username ?? "",
            DefaultPage = "Installed",
            DefaultSort = saved.DefaultSort is >= 0 and <= 2 ? saved.DefaultSort : 0
        };
    }

    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, KettleJson.Serialize(settings with
        {
            Extra = settings.Extra?.Where(pair => !pair.Key.Equals("LocalLibraries", StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals("LocalLibraryPaths", StringComparison.OrdinalIgnoreCase)).ToDictionary()
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
