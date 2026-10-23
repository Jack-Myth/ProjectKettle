using Kettle;
using Kettle.Core;

internal static class InstalledOnlyChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var home = Path.Combine(root, "installed-only-legacy");
        Directory.CreateDirectory(Path.Combine(home, "data", "library"));
        var executable = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(executable, "fixture, never executed");
        var settings = new PortableSettings(home);
        File.WriteAllText(settings.FilePath, """{"Username":"remembered","DefaultPage":"Library","DefaultSort":3,"ShowDemos":false,"ShowUnresolvedApps":false,"FutureOption":42}""");
        var sentinel = Path.Combine(home, "data", "library", "legacy-owned-cache.json");
        const string cached = """{"Apps":[{"AppId":999,"Name":"Never display cached ownership"}]}""";
        File.WriteAllText(sentinel, cached);
        var commands = new List<string>();
        bool empty = false;
        var terminal = new IdentityTerminal(command =>
        {
            lock (commands)
                commands.Add(command);
            return command switch
            {
                "library_folder_list" => $"Index 0, ContentID 1, Path \"{home}\", Label \"\", Apps 1, Mounted yes",
                "apps_installed" => empty ? "0 apps installed :" : $"1 apps installed :\nAppID 12 : \"Installed Demo\" : {home}\\steamapps\\common\\Fixture",
                _ => null
            };
        });
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync(executable, "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var originalSettings = File.ReadAllBytes(settings.FilePath);
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable]), new(session, "fixture", executable), a => a(), home, new EmptyHeaders());
        await SessionChecks.Until(() => !vm.Busy && vm.Games.Count == 1 && vm.SelectedSizeOnDiskBytes is not null);
        await Task.Delay(300);
        vm.SortIndex = vm.SortIndex;
        check(File.ReadAllBytes(settings.FilePath).SequenceEqual(originalSettings), "startup migration and initial sort binding never rewrite player settings");
        check(vm.PageTitle == "已安装" && vm.SortIndex == 0 && vm.Games.Single().AppId == "12", "legacy Library page, installed-first sort and owned filters migrate to native installed-only view");
        string[] ReadCommands()
        {
            lock (commands)
                return commands.ToArray();
        }

        check(ReadCommands().All(c => c.StartsWith("login ") || c is "info" or "library_folder_list" or "apps_installed" or "app_update_status 12" || c.StartsWith("app_status ")), "startup requests only identity, mounted libraries, installed set and selected status/update; no owned metadata or background sweep");
        check(ReadCommands().Count(c => c == "apps_installed") == 1 && ReadCommands().All(c => !c.StartsWith("app_info_print ")), "startup does not request batch or individual unselected app metadata");
        vm.RefreshCommand.Execute(null);
        await SessionChecks.Until(() => !vm.Busy && ReadCommands().Count(c => c == "apps_installed") == 2);
        check(ReadCommands().All(c => c != "licenses_print" && !c.StartsWith("package_info_print ") && !c.StartsWith("app_info_print ")), "manual refresh also never loads the full owned library");
        vm.SortIndex = 2;
        check(settings.Load().DefaultPage == "Installed" && settings.Load().DefaultSort == 2 && settings.Load().Username == "remembered" && settings.Load().Extra?.ContainsKey("FutureOption") == true, "sort persistence preserves unrelated legacy settings and normalizes removed startup page");
        check(File.ReadAllText(sentinel) == cached, "obsolete owned cache is preserved without controlling installed state");
        vm.Search = "missing";
        check(vm.IsEmpty && vm.SelectedGame is null && vm.EmptyTitle.Contains("匹配"), "installed search empty state clears selection and details");
        vm.Search = "";
        empty = true;
        await vm.LoadLibrary();
        check(vm.IsEmpty && !vm.HasNotice && vm.SelectedGame is null && vm.EmptyTitle == "暂无已安装游戏", "native zero-installed response is a useful empty state without stale cached games");
        foreach (var command in new[]
        {
            "licenses_print",
            "package_info_print 1"
        }

        )
        {
            try
            {
                await session.QueryMetadataAsync([command], TimeSpan.FromSeconds(1));
                throw new Exception("Removed full-library command accepted");
            }
            catch (ArgumentException)
            {
                check(true, "session metadata API rejects removed full-library request: " + command);
            }
        }

        try
        {
            await session.ExecuteCommandAsync("app_uninstall 12", TimeSpan.FromSeconds(1));
            throw new Exception("Removed uninstall command accepted");
        }
        catch (ArgumentException)
        {
            check(ReadCommands().All(c => !c.Contains("app_uninstall")), "removed uninstall cannot reach native command dispatch");
        }

        check(typeof(MainViewModel).GetProperty("UninstallCommand") is null && typeof(SteamSession).GetMethod("UninstallAsync") is null, "uninstall removed from view model and session API");
        check(typeof(SteamOwnedLibraryProvider).GetMethod("LoadAsync") is null, "legacy cache reader has no full-library acquisition implementation");
    }
}
