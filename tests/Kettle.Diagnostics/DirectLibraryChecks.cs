using Kettle;
using Kettle.Core;

internal static class DirectLibraryChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var host = Path.Combine(root, "Direct CMD");
        Directory.CreateDirectory(Path.Combine(host, "steamapps"));
        var executable = Path.Combine(host, "steamcmd.exe");
        File.WriteAllText(executable, "fixture never executed");
        var config = Path.Combine(host, "steamapps", "libraryfolders.vdf");
        var original = "\"libraryfolders\" { \"0\" { \"path\" \"" + host.Replace("\\", "\\\\") + "\" \"keep\" \"original\" \"apps\" { } } }";
        File.WriteAllText(config, original);
        var folder = Path.Combine(root, "Direct Selected Library");
        Directory.CreateDirectory(folder);
        var sentinel = Path.Combine(folder, "game.txt");
        File.WriteAllText(sentinel, "keep");
        var terminal = new IdentityTerminal();
        await using var session = new SteamSession(() => terminal, true);
        await session.StartAsync(executable, "fixture_owner");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var added = SteamLibraryRegistration.RegisterDirect(executable, folder);
        check(added.Changed && session.IsAuthenticated && !terminal.Stopped && SteamLibraryRegistration.ReadConfigured(executable).Any(v => v.Root == folder), "direct addition writes and reads back config while keeping existing session alive");
        var bytes = File.ReadAllBytes(config);
        check(!SteamLibraryRegistration.RegisterDirect(executable, Path.Combine(folder, "steamapps")).Changed && File.ReadAllBytes(config).SequenceEqual(bytes), "duplicate root or steamapps request leaves configuration unchanged");
        check(added.BackupPath.Length == 0 && Directory.GetFiles(Path.GetDirectoryName(config)!, "*.kettle-backup-*").Length == 0, "direct write creates no persistent configuration backup");
        var removed = SteamLibraryRegistration.RemoveDirect(executable, folder);
        check(removed.Changed && !SteamLibraryRegistration.ReadConfigured(executable).Any(v => v.Root == folder) && File.Exists(sentinel) && session.IsAuthenticated && !terminal.Stopped, "direct removal changes only registration and neither deletes game files nor restarts session");
        check(!SteamLibraryRegistration.RemoveDirect(executable, folder).Changed, "duplicate removal is idempotent");
        try
        {
            SteamLibraryRegistration.RemoveDirect(executable, host);
            throw new Exception("Default removed");
        }
        catch (InvalidOperationException)
        {
            check(File.ReadAllText(config).Contains("original"), "direct writer protects default registration independently of UI");
        }

        var beforeFailure = File.ReadAllBytes(config);
        using (var locked = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                SteamLibraryRegistration.RegisterDirect(executable, folder);
                throw new Exception("Locked configuration written");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                check(File.ReadAllBytes(config).SequenceEqual(beforeFailure), "failed replacement preserves original config instead of returning success");
            }
        }

        check(SteamLibraryRegistration.RegisterDirect(executable, folder).Changed, "retry after write failure succeeds from actual file state without a queue");
        var home = Path.Combine(root, "direct-model");
        Directory.CreateDirectory(Path.Combine(home, "data"));
        File.WriteAllText(Path.Combine(home, "data", "pending-library-changes.json"), "old malformed outbox must never be applied");
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable]), new(session, "fixture_owner", executable), action => action(), home, new EmptyHeaders());
        await SessionChecks.Until(() => !vm.Busy);
        vm.SelectedLibrary = vm.Libraries.Single(entry => entry.Root == folder);
        await vm.RemoveLibraryAsync();
        check(vm.LocalLibraryMessage == "重新登录后生效" && !vm.Libraries.Any(entry => entry.Root == folder) && session.IsAuthenticated, "UI reflects written removal immediately and only displays restart hint");
        using (var locked = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.AddLocalLibraryAsync(folder);
            check(vm.LocalLibraryMessage.Contains("未能写入") && !vm.Libraries.Any(entry => entry.Root == folder), "UI reports failed write without adding a phantom row or claiming success");
        }

        await vm.AddLocalLibraryAsync(folder);
        check(vm.Libraries.Any(entry => entry.Root == folder) && vm.LocalLibraryMessage == "重新登录后生效", "UI retry reloads SteamCMD file and ignores obsolete queued-operation file");
        await vm.DisposeAsync();
        await session.DisposeAsync();
        var configBeforeLogin = File.ReadAllBytes(config);
        await using var login = new LoginViewModel(LaunchOptions.Parse(["--steamcmd", executable]), () => new SteamSession(() => new IdentityTerminal(), false), action => action(), home);
        login.Username = "fixture_user";
        await login.StartAsync();
        await SessionChecks.Until(() => login.LoginMessage == "登录成功");
        check(File.ReadAllBytes(config).SequenceEqual(configBeforeLogin), "login no longer reads or applies any old deferred operations");
    }
}
