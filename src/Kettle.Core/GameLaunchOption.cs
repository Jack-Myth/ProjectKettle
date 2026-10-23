using System.Globalization;

namespace Kettle.Core;

public static class GameLaunchOptionReader
{
    public static GameLaunchOption[]? Read(ValveData? launch, string? operatingSystem = null)
    {
        if (launch is null) return null;
        operatingSystem ??= OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var entries = new List<GameLaunchOption>();
        foreach (var (key, value) in launch.Children)
        {
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) continue;
            var os = value["config"]?["oslist"]?.Value;
            if (!string.IsNullOrWhiteSpace(os) && !os.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(operatingSystem, StringComparer.OrdinalIgnoreCase)) continue;
            var executable = value["executable"]?.Value;
            if (string.IsNullOrWhiteSpace(executable)) continue;
            var arguments = value["arguments"]?.Value ?? "";
            var type = value["type"]?.Value?.ToLowerInvariant() ?? "";
            var localized = value["description_loc"];
            var name = new[] { localized?[L.Language == "zh-CN" ? "schinese" : "english"]?.Value, localized?["english"]?.Value, value["description"]?.Value }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            name ??= type switch
            {
                "default" or "" => L.Text(TextId.PlayGame),
                "config" => L.Text(TextId.LaunchConfigurationTool),
                "editor" => L.Text(TextId.LaunchGameEditor),
                "server" => L.Text(TextId.LaunchDedicatedServer),
                "benchmark" => L.Text(TextId.RunBenchmark),
                "manual" => L.Text(TextId.ViewManual),
                "safemode" => L.Text(TextId.PlayInSafeMode),
                "multiplayer" => L.Text(TextId.PlayMultiplayer),
                "vr" => L.Text(TextId.PlayInSteamVRMode),
                "openxr" => L.Text(TextId.PlayInOpenXRMode),
                "othervr" => L.Text(TextId.PlayInVRMode),
                "osvr" => L.Text(TextId.PlayInOSVRMode),
                _ => executable + (arguments.Length > 0 ? " " + arguments : "")
            };
            entries.Add(new(index, name, executable, arguments, type));
        }
        return entries.OrderBy(e => e.Index).ToArray();
    }
}
