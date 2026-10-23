using Kettle.Core;

internal static class LaunchOptionChecks
{
    public static Task Run(Action<bool, string> check, string root)
    {
        var data = ValveData.Parse("""
            "launch" {
                "0" { "executable" "linux" "config" { "oslist" "linux" } }
                "3" { "executable" "game.exe" "type" "default" "config" { "oslist" "linux, WINDOWS" } }
                "7" { "executable" "game.exe" "arguments" "SETUP" "type" "config" }
                "9" { "executable" "tool.exe" "description" "Raw" "description_loc" { "english" "English" "schinese" "中文" } }
                "11" { "executable" "unknown.exe" "arguments" "--special" "type" "custom" "config" { "oslist" "windowsserver" } }
            }
            "fallback" {
                "1" { "executable" "a.exe" "description_loc" { "english" "English fallback" } }
                "2" { "executable" "b.exe" "description" "Publisher name" }
                "4" { "executable" "c.exe" "arguments" "--special" "type" "custom" }
            }
            "mac" { "2" { "executable" "Mac.app" "config" { "oslist" "macos" } } }
            """
        );
        var entries = GameLaunchOptionReader.Read(data["launch"], "windows")!;
        check(entries.Select(e => e.Index).SequenceEqual(new[] { 3, 7, 9 }), "OS filtering uses complete tokens and preserves original Steam indices");
        check(entries[1].Name == "启动配置工具" && entries[1].Arguments == "SETUP" && entries[2].Name == "中文", "type labels, arguments and Chinese publisher names");
        var fallback = GameLaunchOptionReader.Read(data["fallback"], "windows")!;
        check(fallback.Select(e => e.Name).SequenceEqual(new[] { "English fallback", "Publisher name", "c.exe --special" }), "English, description and executable fallbacks");
        L.Initialize("en");
        try { check(GameLaunchOptionReader.Read(data["launch"], "windows")![2].Name == "English" && GameLaunchOptionReader.Read(data["launch"], "windows")![1].Name == "Launch configuration tool", "English launch names"); }
        finally { L.Initialize("zh-CN"); }
        check(GameLaunchOptionReader.Read(data["mac"], "windows")!.Length == 0 && GameLaunchOptionReader.Read(data["mac"], "macos")!.Length == 1, "platform-specific entries excluded");

        return Task.CompletedTask;
    }
}
