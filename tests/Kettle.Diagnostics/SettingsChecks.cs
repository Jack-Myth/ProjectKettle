using Kettle.Core;
using System.Text.Json;
using System.Threading.Channels;

internal static class SettingsChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var home = Path.Combine(root, "settings-app");
        Directory.CreateDirectory(Path.Combine(home, "data"));
        var store = new PortableSettings(home);
        File.WriteAllText(store.FilePath, """{"Username":"saved_user","SteamCmd":"saved_path","FutureOption":{"Enabled":true}}""");
        var legacy = store.Load();
        check(legacy.DefaultPage == "Installed" && legacy.DefaultSort == 0 && legacy.ShowDemos, "legacy settings load useful defaults");
        store.Save(legacy with
        {
            DefaultSort = 3,
            ShowDemos = false
        });
        check(store.Load().Extra?.ContainsKey("FutureOption") == true && store.Load().Username == "saved_user", "preference save preserves existing and unknown config fields");
        var known = PlayerProfileReader.Parse("account_name", "Persona name: Ordinary Player\nSteamID: [U:1:42]");
        check(known.PersonaName == "Ordinary Player" && known.SteamId == "[U:1:42]", "profile parses persona and SteamID");
        var unknown = PlayerProfileReader.Parse("account_name", "Unknown command: info");
        check(unknown.AccountName == "account_name" && unknown.PersonaName is null && unknown.SteamId is null, "account name never fabricated as a nickname");
        var cmd = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(cmd, "fixture; never executed");
        store.Save(store.Load() with
        {
            DefaultSort = 0,
            ShowDemos = true
        });
        var terminal = new IdentityTerminal();
        var session = new SteamSession(() => terminal, false);
        await session.StartAsync(cmd, "player_account");
        await SessionChecks.Until(() => session.IsAuthenticated);
        await using var vm = new Kettle.MainViewModel(LaunchOptions.Parse(["--steamcmd", cmd]), new Kettle.AuthenticatedSession(session, "player_account", cmd), action => action(), home);
        vm.LogoutRequested += async () =>
        {
            await vm.DisposeAsync();
            await session.LogoutAsync();
            vm.ClearSessionData();
        };
        await SessionChecks.Until(() => vm.PlayerSteamId == "76561198012345678" && !vm.Busy && vm.Games.Count == 0);
        check(vm.PlayerTitle == "Test Persona" && vm.PlayerAccount == "player_account", "player entry uses explicit nickname and retains separate account name");
        vm.OpenSettingsCommand.Execute(null);
        check(vm.IsSettingsOpen && !vm.IsLibraryContentVisible, "settings opens from player menu");
        vm.SortIndex = 1;
        vm.LanguageIndex = 2;
        check(store.Load().Language == "en" && vm.LanguageIndex == 2 && L.Language == "zh-CN", "language choice persists without changing the active session");
        check(vm.Games.Count == 0 && vm.SortIndex == 1, "sort remains usable with empty native installed list");
        var saved = store.Load();
        check(saved.DefaultPage == "Installed" && saved.DefaultSort == 1 && saved.Language == "en" && saved.Extra?.ContainsKey("FutureOption") == true, "installed sort and language persist while unknown settings remain compatible");
        vm.CloseSettingsCommand.Execute(null);
        check(vm.IsLibraryContentVisible, "settings return path restores library");
        Directory.CreateDirectory(Path.Combine(home, "data", "library"));
        var sentinel = Path.Combine(home, "data", "library", "cache-fixture.json");
        File.WriteAllText(sentinel, "non-sensitive cache fixture");
        var inflight = session.QueryMetadataAsync(["info"], TimeSpan.FromSeconds(2));
        await Task.Delay(20);
        await Task.WhenAll(vm.LogoutAsync(), vm.LogoutAsync());
        try
        {
            await inflight;
            throw new Exception("Pending query survived logout");
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            check(true, "logout cancels an in-flight command queue");
        }

        check(terminal.Stopped && !session.IsAuthenticated && vm.AccountName == "" && vm.Games.Count == 0 && vm.PlayerSteamId == "暂未获取", "logout ends session and clears account, identity and list before login page");
        check(File.Exists(sentinel) && store.Load().Username == "saved_user", "logout preserves disk metadata and remembered non-secret account");
        await using var reloaded = new Kettle.MainViewModel(LaunchOptions.Parse(["--preview"]), dispatcher: action => action(), dataDirectory: home);
        await SessionChecks.Until(() => !reloaded.Busy);
        check(reloaded.PageTitle == "已安装" && reloaded.SortIndex == 1 && reloaded.LanguageIndex == 2, "startup page, sort and language choice restored on next entry");
        var invalidHome = Path.Combine(root, "invalid-settings-app");
        Directory.CreateDirectory(Path.Combine(invalidHome, "data"));
        File.WriteAllText(new PortableSettings(invalidHome).FilePath, "{invalid json");
        await using var invalidVm = new Kettle.MainViewModel(LaunchOptions.Parse(["--preview"]), dispatcher: action => action(), dataDirectory: invalidHome);
        invalidVm.OpenSettingsCommand.Execute(null);
        check(invalidVm.SettingsMessage.Contains("默认") && invalidVm.SortIndex == 0, "malformed settings show an error and useful defaults");
    }
}

internal sealed class SettingsLibrary : IOwnedLibraryProvider
{
    public OwnedLibrarySnapshot? ReadCache(string account) => null;
    public Task<OwnedLibrarySnapshot> LoadAsync(string account, ISteamMetadataCommands commands, IProgress<OwnedLibrarySnapshot>? progress, CancellationToken token) => Task.FromResult(new OwnedLibrarySnapshot([new(10, "A Game", AppKind.Game, true), new(40, "B Demo", AppKind.Demo, true)], true, true, DateTime.UtcNow, "fixture"));
}

internal sealed class IdentityTerminal(Func<string, string?>? query = null) : ISteamTerminal
{
    private readonly Channel<string> output = Channel.CreateUnbounded<string>();
    public bool Stopped;
    public Task StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        output.Writer.TryWrite("Steam>");
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
    public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
    {
        if (line.IsEmpty)
        {
            output.Writer.TryWrite("\r\nSteam>");
            return Task.CompletedTask;
        }

        foreach (var command in line.ToString().Split('\r'))
        {
            var result = command.StartsWith("login ") ? "Logged in OK" : query?.Invoke(command) ?? command switch
            {
                "library_folder_list" => "Index 0, ContentID 42, Path \"C:\\Synthetic Steam\", Label \"\", Disk Space 1 GB/10 GB, Apps 0, Mounted yes",
                "apps_installed" => "0 apps installed :",
                _ when command.StartsWith("app_update_status ") => GameUpdateChecks.Body(uint.Parse(command[18..]), 1, 1),
                _ when command.StartsWith("app_status ") => $"AppID {command[11..]} (Synthetic app):\r\n - size on disk: 1073741824 bytes, BuildID 1\r\n - user config: \"UserConfig\"\r\n{{\r\n}}",
                _ => "Persona name: Test Persona\r\nSteamID: 76561198012345678"
            };
            output.Writer.TryWrite(command + "\r\n" + result + "\r\nSteam>");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Stopped = true;
        output.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
