using Kettle;
using Kettle.Core;

internal static class LocalFeatureChecks
{
    public static async Task Run(Action<bool, string> check, string root, string executable)
    {
        var range = new Kettle.RangeCollection<int>();
        var notifications = 0;
        range.CollectionChanged += (_, _) => notifications++;
        range.ReplaceAll(Enumerable.Range(0, 5000));
        check(range.Count == 5000 && notifications == 1, "5000 item updates issue one collection reset");
        var library = Path.Combine(root, "Selected Steam Library");
        Directory.CreateDirectory(library);
        var path = Path.Combine(library, "steamapps", "common", "Returned by SteamCMD");
        string Volume(string folder, bool mounted = true) => $"Index 0, ContentID 42, Path \"{folder}\", Label \"\", Disk Space 1 GB/10 GB, Apps 1, Mounted {(mounted ? "yes" : "no")}";
        var installed = $"1 apps installed :\nAppID 90001 : \"Queried Game\" : {path}\n";
        check(SteamInstalledLibrary.ParseInstalled(installed).Single().InstallPath == path && !Directory.Exists(path), "installation path and state come from SteamCMD even without Kettle filesystem inference");
        check(SteamInstalledLibrary.ParseInstalled(installed).Single().Size == "尚未查询", "unqueried size label makes no claim about SteamCMD backend capability");
        check(SteamInstalledLibrary.ParseInstalled("0 apps installed :").Length == 0, "SteamCMD empty installation list is an explicit empty state");
        foreach (var bad in new[]
        {
            "Unknown command: apps_installed",
            "2 apps installed :\nAppID 1 : \"Partial\" : C:\\partial",
            "connection failed"
        }

        )
        {
            try
            {
                SteamInstalledLibrary.ParseInstalled(bad);
                throw new Exception("Bad output accepted");
            }
            catch (InvalidDataException)
            {
                check(true, "failed or incomplete installed response cannot become a false uninstalled result");
            }
        }

        check(!SteamInstalledLibrary.ParseVolumes(Volume(library, false)).Single().Mounted, "unmounted volume is reported by SteamCMD rather than directory existence");
        var query = new InstalledCommands(Volume(library), installed);
        var snapshot = await new SteamInstalledLibrary().ReadAsync(query);
        check(query.Commands.SequenceEqual(new[] { "library_folder_list", "apps_installed" }) && snapshot.Games.Length == 1, "installed set uses two shared-session queries regardless of thousands of owned apps");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try
            {
                await new SteamInstalledLibrary().ReadAsync(query, cancel.Token);
                throw new Exception("Cancelled query continued");
            }
            catch (OperationCanceledException)
            {
                check(true, "installation query propagates cancellation");
            }
        }

        var home = Path.Combine(root, "steam-managed-app");
        Directory.CreateDirectory(Path.Combine(home, "data"));
        var store = new PortableSettings(home);
        File.WriteAllText(store.FilePath, "{\"Username\":\"saved_user\",\"LocalLibraries\":[\"C:\\\\NeverAutoRegister\"],\"FutureOption\":42}");
        var fail = false;
        var terminal = new IdentityTerminal(command => command switch
        {
            "library_folder_list" => Volume(library),
            "apps_installed" => fail ? "Unknown command: apps_installed" : installed,
            _ => null
        });
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync(executable, "local_player");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var provider = new LocalFixtureOwned();
        var config = Path.Combine(Path.GetDirectoryName(executable)!, "steamapps", "libraryfolders.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "\"libraryfolders\" { \"0\" { \"path\" \"" + library.Replace("\\", "\\\\") + "\" \"apps\" { } } }");
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable]), new(session, "local_player", executable), action => action(), home);
        await SessionChecks.Until(() => !vm.Busy && vm.Games.Count == 1);
        check(vm.Games.Single(game => game.AppId == "90001").State == "已安装" && vm.Libraries.Single().Root == library, "main library uses live SteamCMD installed set and volume results");
        check(vm.LocalLibraryMessage.Contains("已忽略") && vm.Libraries.Count == 1, "legacy Kettle paths ignored with migration notice and never silently registered");
        var another = Path.Combine(root, "Another Selected Library");
        Directory.CreateDirectory(another);
        await vm.AddLocalLibraryAsync(another);
        check(SteamLibraryRegistration.ReadConfigured(executable).Any(volume => volume.Root == another) && vm.Libraries.Any(entry => entry.Root == another) && session.IsAuthenticated, "addition writes SteamCMD config immediately and refreshes UI without ending session");
        await vm.AddLocalLibraryAsync(another);
        check(vm.Libraries.Count(entry => entry.Root == another) == 1, "duplicate additions do not duplicate config entries");
        vm.OpenSettingsCommand.Execute(null);
        vm.SortIndex = 1;
        check(vm.Games.Count == 1 && vm.PageTitle == "已安装", "installed list ignores legacy owned-app filters");
        check(!store.Load().HasIgnoredLegacyLibraries && store.Load().Extra?.ContainsKey("FutureOption") == true && store.Load().Username == "saved_user", "sort save preserves account and unrelated settings");
        vm.Search = "";
        fail = true;
        await vm.LoadLibrary();
        check(vm.Games.Count == 0 && vm.HasNotice, "failed live query clears stale installation authority and never falls back to filesystem scanning");
        check(vm.LibraryStatus.Contains("apps_installed 响应解析失败") && session.CommandChannel.CanRead, "complete unsupported installed response is reported accurately without losing transport synchronization");
        fail = false;
        await vm.LoadLibrary();
        check(vm.Games.Count == 1 && session.CommandChannel.CanRead, "valid next response can recover the installed list after a business parse failure on the same transport");
        await using var reconnectedSession = new SteamSession(() => new IdentityTerminal(command => command switch
        {
            "library_folder_list" => Volume(library),
            "apps_installed" => installed,
            _ => null
        }), false);
        await reconnectedSession.StartAsync(executable, "local_player");
        await SessionChecks.Until(() => reconnectedSession.IsAuthenticated);
        await using var reconnected = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable]), new(reconnectedSession, "local_player", executable), action => action(), home);
        await SessionChecks.Until(() => !reconnected.Busy && reconnected.Games.Count == 1);
        check(reconnected.Games.Single().AppId == "90001", "explicit fresh session recovers authoritative installation state");
        var beforeCancel = File.ReadAllBytes(config);
        vm.FolderPicker = () => Task.FromResult<string?>(null);
        vm.AddLocalLibraryCommand.Execute(null);
        check(File.ReadAllBytes(config).SequenceEqual(beforeCancel), "cancelled folder picker does not write library config");
        await CheckRegistration(check, root);
    }

    private static async Task CheckRegistration(Action<bool, string> check, string root)
    {
        var host = Path.Combine(root, "Standalone SteamCMD");
        Directory.CreateDirectory(Path.Combine(host, "steamapps"));
        var executable = Path.Combine(host, "steamcmd.exe");
        File.WriteAllText(executable, "fixture, never executed");
        var config = Path.Combine(host, "steamapps", "libraryfolders.vdf");
        var original = "\"libraryfolders\" { \"0\" { \"path\" \"" + host.Replace("\\", "\\\\") + "\" \"unknown_field\" \"keep me\" \"apps\" { \"42\" \"123\" } } }";
        File.WriteAllText(config, original);
        var folder = Path.Combine(root, "Registration with spaces");
        Directory.CreateDirectory(folder);
        var receipt = SteamLibraryRegistration.RegisterStopped(executable, folder);
        check(receipt.Changed && receipt.BackupPath.Length == 0 && receipt.OriginalBytes is not null && File.ReadAllText(config).Contains("keep me"), "stopped-instance registration keeps temporary in-memory rollback and preserves existing config entries");
        var duplicate = SteamLibraryRegistration.RegisterStopped(executable, Path.Combine(folder, "steamapps") + Path.DirectorySeparatorChar);
        check(!duplicate.Changed, "registration root/steamapps and trailing separator are deduplicated in SteamCMD configuration");
        check(SteamLibraryRegistration.Confirmed(receipt, [new(1, folder, true)]) && !SteamLibraryRegistration.Confirmed(receipt, [new(1, folder, false)]), "registration success requires matching mounted SteamCMD readback");
        SteamLibraryRegistration.RestoreTestRegistration(receipt);
        check(File.ReadAllText(config) == original, "reversible registration restores exact original configuration without deleting games");
        await using (var owner = new SteamSession(() => new IdentityTerminal(), true))
        {
            await owner.StartAsync(executable, "fixture_owner");
            await SessionChecks.Until(() => owner.IsAuthenticated);
            try
            {
                SteamLibraryRegistration.RegisterStopped(executable, folder);
                throw new Exception("Busy configuration was modified");
            }
            catch (IOException)
            {
                check(File.ReadAllText(config) == original, "busy SteamCMD ownership blocks configuration write without stopping another process");
            }
        }

        File.WriteAllText(config, "broken");
        try
        {
            SteamLibraryRegistration.RegisterStopped(executable, folder);
            throw new Exception("Broken config modified");
        }
        catch (InvalidDataException)
        {
            check(File.ReadAllText(config) == "broken", "unknown SteamCMD config format fails without overwrite");
        }
    }
}

internal sealed class InstalledCommands(string volumes, string installed) : ISteamMetadataCommands
{
    public bool IsAuthenticated => true;
    public List<string> Commands { get; } = [];

    public Task<string> QueryMetadataAsync(IReadOnlyList<string> commands, TimeSpan timeout, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Commands.AddRange(commands);
        return Task.FromResult(commands.Single() == "library_folder_list" ? volumes : installed);
    }
}

internal sealed class LocalFixtureOwned : IOwnedLibraryProvider
{
    public OwnedLibrarySnapshot Snapshot { get; } = new([new(90001, "Owned Game", AppKind.Game, true), new(90002, "App 90002 · 待解析", AppKind.Unknown, false), new(90003, "Owned Demo", AppKind.Demo, true), new(90004, "Type waiting", AppKind.Unknown, true)], true, false, DateTime.UtcNow, "synthetic metadata");

    public OwnedLibrarySnapshot? ReadCache(string account) => null;
    public Task<OwnedLibrarySnapshot> LoadAsync(string account, ISteamMetadataCommands commands, IProgress<OwnedLibrarySnapshot>? progress, CancellationToken token) => Task.FromResult(Snapshot);
}
