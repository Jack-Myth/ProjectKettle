using Kettle.Core;

internal static class LaunchOptionChecks
{
    public static async Task Run(Action<bool, string> check, string root)
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
        var entries = GameLaunchOption.Read(data["launch"], "windows")!;
        check(entries.Select(e => e.Index).SequenceEqual(new[] { 3, 7, 9 }), "OS filtering uses complete tokens and preserves original Steam indices");
        check(entries[1].Name == "启动配置工具" && entries[1].Arguments == "SETUP" && entries[2].Name == "中文", "type labels, arguments and Chinese publisher names");
        var fallback = GameLaunchOption.Read(data["fallback"], "windows")!;
        check(fallback.Select(e => e.Name).SequenceEqual(new[] { "English fallback", "Publisher name", "c.exe --special" }), "English, description and executable fallbacks");
        L.Initialize("en");
        try { check(GameLaunchOption.Read(data["launch"], "windows")![2].Name == "English" && GameLaunchOption.Read(data["launch"], "windows")![1].Name == "Launch configuration tool", "English launch names"); }
        finally { L.Initialize("zh-CN"); }
        check(GameLaunchOption.Read(data["mac"], "windows")!.Length == 0 && GameLaunchOption.Read(data["mac"], "macos")!.Length == 1, "platform-specific entries excluded");

        var backend = new Backend { Entries = entries };
        var cloud = new Cloud();
        var lifecycle = new GameLifecycle(backend, cloud, Path.Combine(root, "launch-options"), Path.Combine(root, "fixture.exe"), "fixture", () => Task.FromResult(123u));
        var game = new Game("Fixture", "12", root, "已安装", "", "");
        lifecycle.SelectLaunchOption = (_, _) => Task.FromResult<GameLaunchOption?>(null);
        await lifecycle.StartAsync(game);
        check(lifecycle.Current.Phase == GamePhase.Idle && backend.Runs == 0 && cloud.Prepares == 0, "cancel selection neither launches nor synchronizes");
        lifecycle.SelectLaunchOption = (_, options) => Task.FromResult<GameLaunchOption?>(options.Single(e => e.Index == 7));
        await lifecycle.StartAsync(game);
        check(backend.Index == 7 && backend.Runs == 1 && lifecycle.Current.Phase == GamePhase.Completed, "selected original index reaches backend");
        backend.Entries = [entries[0]];
        lifecycle.SelectLaunchOption = (_, _) => throw new Exception("Single option must not show a dialog");
        await lifecycle.StartAsync(game);
        check(backend.Index == 3 && backend.Runs == 2, "single filtered option launches its original index without a dialog");
        backend.Entries = [];
        await lifecycle.StartAsync(game);
        check(lifecycle.Current.Phase == GamePhase.Failed && backend.Runs == 2 && cloud.Prepares == 2, "no compatible options cannot launch or change saves");
    }

    private sealed class Backend : IGameBackend
    {
        public GameLaunchOption[] Entries = [];
        public int Runs;
        public int? Index;
        public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => Task.FromResult(new AppLaunchMetadata(CloudSupport.None, null, null) { LaunchEntries = Entries });
        public Task WaitIdleAsync(uint id, CancellationToken token) => Task.CompletedTask;
        public Task RunAsync(uint id, Action<bool> started, CancellationToken token) => throw new Exception("Launch index was lost");
        public Task RunAsync(uint id, Action<bool> started, CancellationToken token, int? launchOptionIndex)
        {
            Runs++; Index = launchOptionIndex; started(true); return Task.CompletedTask;
        }
    }

    private sealed class Cloud : IGameCloudSync
    {
        public int Prepares;
        public Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            Prepares++; return Task.FromResult(new CloudPreparation(context, "fixture", false));
        }
        public Task FinishAsync(CloudPreparation preparation, ConflictChoice choice, string? ticket, CancellationToken token) => Task.CompletedTask;
    }
}
