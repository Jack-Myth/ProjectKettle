using Kettle;
using Kettle.Core;

internal static class LoginChecks
{
    public static async Task Run(Action<bool, string> check, string executable, string root)
    {
        var home = Path.Combine(root, "login-checks");
        await using (var names = new LoginViewModel(LaunchOptions.Parse(["--steamcmd", executable]), () => new SteamSession(() => new FakeTerminal("username"), false), action => action(), home))
        {
            names.Username = "player_76561198012345678";
            await names.StartAsync();
            await SessionChecks.Until(() => names.InputNeeded);
            check(names.Username == "player_76561198012345678" && names.ResponsePasswordChar == '\0', "login model keeps ordinary account and ID visible");
            await names.CancelAsync();
            check(!names.LoginActive && names.Response == "", "cancel cleans login state");
        }

        var terminal = new FakeTerminal("guard");
        await using var vm = new LoginViewModel(LaunchOptions.Parse(["--steamcmd", executable]), () => new SteamSession(() => terminal, false), action => action(), home);
        AuthenticatedSession? result = null;
        vm.Authenticated += context => result = context;
        vm.Username = "fixture_user";
        await vm.StartAsync();
        await SessionChecks.Until(() => vm.InputNeeded);
        check(result is null && vm.ResponsePasswordChar == '●', "login stays independent and does not hand off before password");
        vm.Response = "fixture-sensitive-input";
        vm.SubmitCommand.Execute(null);
        check(vm.Response.Length == 0, "login clears secret immediately on submit");
        await SessionChecks.Until(() => vm.InputNeeded);
        check(result is null && vm.ResponsePasswordChar == '●', "guard step cannot open main window");
        vm.Response = "fixture-code";
        vm.SubmitCommand.Execute(null);
        await SessionChecks.Until(() => result is not null);
        check(result!.Session.IsAuthenticated && !File.ReadAllText(new PortableSettings(home).FilePath).Contains("fixture-sensitive-input"), "only authenticated session is handed off and no secret saved");
        await vm.DisposeAsync();
        check(result.Session.IsAuthenticated && !terminal.Stopped, "closing login model after handoff keeps shared session alive");
        await result.Session.LogoutAsync();
        int starts = 0;
        await using (var restored = new LoginViewModel(LaunchOptions.Parse(["--steamcmd", executable]), () =>
        {
            starts++;
            return new SteamSession(() => new FakeTerminal("guard"), false);
        }, action => action(), home))
        {
            check(restored.Username == "fixture_user" && restored.Response == "", "new login model restores the persisted recent username without secret input");
            await Task.Delay(30);
            check(starts == 0 && !restored.LoginActive && restored.LoginCommand.CanExecute(null), "restoring username never automatically starts authentication");
        }

        var failed = new FakeTerminal("failure");
        await using var failVm = new LoginViewModel(LaunchOptions.Parse(["--steamcmd", executable]), () => new SteamSession(() => failed, false), action => action(), home);
        bool handedOff = false;
        failVm.Authenticated += _ => handedOff = true;
        failVm.Username = "fixture_user";
        await failVm.StartAsync();
        await SessionChecks.Until(() => failVm.InputNeeded);
        failVm.Response = "fixture-sensitive-input";
        failVm.SubmitCommand.Execute(null);
        await SessionChecks.Until(() => failVm.LoginButtonLabel == "重新登录");
        check(!handedOff, "failed login never creates an authenticated handoff");
        var configTerminal = new FreshLoginChecks.Terminal("cached", 7);
        await using var configVm = new LoginViewModel(LaunchOptions.Parse(["--steamcmd", executable]), () => new SteamSession(() => configTerminal, false), action => action(), home);
        var messages = new List<string>();
        configVm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(configVm.LoginMessage))
                messages.Add(configVm.LoginMessage);
        };
        configVm.Username = "fixture_user";
        await configVm.StartAsync();
        await SessionChecks.Until(() => configVm.LoginMessage == "获取账户配置较慢，请稍候…");
        check(messages.Contains("正在获取账户配置…") && configVm.HasLoginMessage, "existing login status binding exposes configuration acquisition and slow notice");
        check(configVm.LoginActive && !configVm.InputNeeded && configVm.LoginButtonLabel == "登录", "slow configuration remains an active login, not a failure or input prompt");
        configTerminal.BeginInfo();
        await SessionChecks.Until(() => configVm.LoginMessage.Contains("正在获取用户信息"));
        check(configVm.HasLoginMessage && !configVm.LoginMessage.Contains("较慢"), "next login phase replaces configuration notice in the UI");
        configTerminal.Reject();
        await SessionChecks.Until(() => configVm.LoginButtonLabel == "重新登录");
        check(configVm.LoginMessage == "登录失败，请重试。" && !configVm.LoginActive, "true failure replaces configuration notice in the UI");
        var filledTerminal = new InitialCredentialChecks.Terminal("full");
        await using var filled = new LoginViewModel(LaunchOptions.Parse(["--steamcmd", executable]), () => new SteamSession(() => filledTerminal, false), a => a(), Path.Combine(root, "filled-login"));
        check(filled.Password == "" && filled.GuardCode == "" && filled.CanEditUsername, "initial credential properties start empty and optional");
        AuthenticatedSession? filledResult = null;
        filled.Authenticated += value => filledResult = value;
        filled.Username = "fixture_user";
        filled.Password = InitialCredentialChecks.Password;
        filled.GuardCode = InitialCredentialChecks.Code;
        var submitted = filled.StartAsync();
        check(filled.Password == "" && filled.GuardCode == "", "clicking login clears both bound secret fields immediately");
        await submitted;
        await SessionChecks.Until(() => filled.LoginMessage == "正在获取用户信息…");
        filledTerminal.Complete();
        await SessionChecks.Until(() => filledResult is not null);
        string saved = File.ReadAllText(new PortableSettings(Path.Combine(root, "filled-login")).FilePath);
        using var savedJson = System.Text.Json.JsonDocument.Parse(saved);
        var savedStrings = savedJson.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String).Select(p => p.Value.GetString()).ToArray();
        check(filledResult!.Session.IsAuthenticated && !savedStrings.Contains(InitialCredentialChecks.Password) && !savedStrings.Contains(InitialCredentialChecks.Code), "filled-login handoff persists username/path only, never password/token");
        await filledResult.Session.DisposeAsync();
        var retries = new Queue<InitialCredentialChecks.Terminal>(new[] { new InitialCredentialChecks.Terminal("failure"), new InitialCredentialChecks.Terminal("cached") });
        await using var retryVm = new LoginViewModel(LaunchOptions.Parse(["--steamcmd", executable]), () => new SteamSession(() => retries.Dequeue(), false), a => a(), Path.Combine(root, "credential-retry"));
        retryVm.Username = "fixture_user";
        retryVm.Password = InitialCredentialChecks.Password;
        retryVm.GuardCode = InitialCredentialChecks.Code;
        await retryVm.StartAsync();
        await SessionChecks.Until(() => retryVm.LoginButtonLabel == "重新登录");
        check(retryVm.Password == "" && retryVm.GuardCode == "" && !retryVm.InputNeeded && retryVm.CanEditUsername, "failure restores empty optional fields without retaining credentials");
        AuthenticatedSession? retried = null;
        retryVm.Authenticated += value => retried = value;
        await retryVm.StartAsync();
        await SessionChecks.Until(() => retried is not null);
        check(retried!.Session.IsAuthenticated, "retry with both fields empty supports saved login");
        await retried.Session.DisposeAsync();
    }
}
