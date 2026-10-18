namespace Kettle.Core;
// Interactive authentication only. Ordinary command output never enters this handler.
internal sealed class SteamLoginHandler(Action<LoginUpdate> publish, Action ready, TimeSpan? timeout)
{
    private readonly SteamProtocol protocol = new();
    private bool accepted, connecting, authenticated, fetchingConfig, fetchingUserInfo;
    private readonly object secretLock = new();
    private char[]? initialPassword, initialCode;
    public static bool ValidInput(ReadOnlySpan<char> value, int maximum = 256)
    {
        if (value.Length > maximum)
            return false;
        foreach (char c in value)
            if (char.IsControl(c) || c is '\u2028' or '\u2029')
                return false;
        return true;
    }

    public void ConfigureInitialInput(ReadOnlySpan<char> password, ReadOnlySpan<char> code)
    {
        lock (secretLock)
        {
            ClearInitialInput();
            initialPassword = password.IsEmpty ? null : password.ToArray();
            initialCode = code.IsEmpty ? null : code.ToArray();
        }
    }

    private void ClearInitialInput()
    {
        lock (secretLock)
        {
            if (initialPassword is not null)
                Array.Clear(initialPassword);
            if (initialCode is not null)
                Array.Clear(initialCode);
            initialPassword = initialCode = null;
        }
    }

    public char[]? TakeInitialInput()
    {
        lock (secretLock)
        {
            char[]? value = null;
            if (Current.State == LoginState.Password)
            {
                value = initialPassword;
                initialPassword = null;
            }
            else if (Current.State == LoginState.GuardCode)
            {
                value = initialCode;
                initialCode = null;
            }

            return value;
        }
    }

    // The native positional form cannot express a token with an omitted password.
    // Keep values requiring ambiguous console escaping on the prompt-input path.
    private static bool NativeToken(char[]? value) => value is { Length: > 0 } && value.All(c => char.IsAsciiLetterOrDigit(c) || "!@#$%^&*_-+=,.?~".Contains(c));
    public char[] CreateLoginCommand(string username)
    {
        lock (secretLock)
        {
            bool password = NativeToken(initialPassword), code = password && NativeToken(initialCode);
            string prefix = "login " + username;
            var command = new char[prefix.Length + (password ? 1 + initialPassword!.Length : 0) + (code ? 1 + initialCode!.Length : 0)];
            prefix.CopyTo(0, command, 0, prefix.Length);
            int at = prefix.Length;
            // Retain a one-shot prompt fallback until acceptance. If SteamCMD still
            // requests a provided field without rejecting login, no retyping is needed.
            if (password)
            {
                command[at++] = ' ';
                initialPassword!.CopyTo(command, at);
                at += initialPassword.Length;
            }

            if (code)
            {
                command[at++] = ' ';
                initialCode!.CopyTo(command, at);
            }

            // Ignore command echo as authentication text, without retaining a string.
            protocol.SuppressInputEcho(command);
            return command;
        }
    }

    public LoginUpdate Current { get; private set; } = new(LoginState.Idle, "输入 Steam 账户名以继续。");
    public DateTime Deadline
    {
        get; private set;
    }

    public void Update(LoginState state, string message)
    {
        if (state is LoginState.Authenticated or LoginState.Failed or LoginState.Cancelled or LoginState.TimedOut or LoginState.Exited)
            ClearInitialInput();
        Current = new(state, message);
        Deadline = DateTime.UtcNow + (timeout ?? (accepted || Current.NeedsInput || state == LoginState.MobileConfirmation ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(90)));
        publish(Current);
    }

    public void BeginLogin()
    {
        protocol.Reset();
        accepted = connecting = authenticated = fetchingConfig = fetchingUserInfo = false;
        Update(LoginState.Connecting, "正在登录…");
    }

    public void PrepareInput(ReadOnlySpan<char> response)
    {
        protocol.SuppressInputEcho(response);
        Update(LoginState.Connecting, "正在登录…");
    }

    public void FeedNormalized(string chunk)
    {
        foreach (var signal in protocol.FeedNormalized(chunk))
        {
            if (Current.State is LoginState.Failed or LoginState.Cancelled or LoginState.TimedOut or LoginState.Exited)
                return;
            if (signal == SteamSignal.Ready)
            {
                ready();
                continue;
            }

            if (authenticated)
                continue;
            switch (signal)
            {
                case SteamSignal.Username:
                    if (!accepted)
                        Update(LoginState.Username, "请输入 Steam 账户名。");
                    break;
                case SteamSignal.Password:
                    if (!accepted)
                        Update(LoginState.Password, "请输入密码");
                    break;
                case SteamSignal.GuardCode:
                    if (!accepted)
                        Update(LoginState.GuardCode, "请输入 Steam 令牌验证码");
                    break;
                case SteamSignal.MobileConfirmation:
                    if (!accepted)
                        Update(LoginState.MobileConfirmation, "请在手机上确认登录");
                    break;
                case SteamSignal.Connecting:
                    connecting = true;
                    if (!accepted)
                        Update(LoginState.Connecting, "正在连接…");
                    break;
                case SteamSignal.Accepted:
                    if (connecting && !accepted)
                    {
                        accepted = true;
                        Update(LoginState.Connecting, "Steam 已接受登录，正在获取账户信息…");
                    }

                    break;
                case SteamSignal.UserInfo:
                    if (accepted)
                        Authenticate();
                    break;
                case SteamSignal.Completion:
                    if (fetchingUserInfo)
                        Authenticate();
                    break;
                case SteamSignal.Success:
                    Authenticate();
                    break;
                // These stages are authoritative even when a mobile challenge or
                // progress text interrupted the earlier "to Steam Public...OK".
                case SteamSignal.ClientConfig:
                    if (!fetchingUserInfo)
                    {
                        accepted = true;
                        fetchingConfig = true;
                        ClearInitialInput();
                        Update(LoginState.Connecting, "正在获取账户配置…");
                    }

                    break;
                case SteamSignal.UserInfoPending:
                    accepted = true;
                    fetchingUserInfo = true;
                    fetchingConfig = false;
                    ClearInitialInput();
                    Update(LoginState.Connecting, "正在获取用户信息…");
                    break;
                case SteamSignal.Warning:
                    if (accepted && fetchingConfig && !Current.NeedsInput)
                        Update(LoginState.Connecting, "获取账户配置较慢，请稍候…");
                    break;
                case SteamSignal.Failure:
                    Update(LoginState.Failed, "登录失败，请重试。");
                    break;
                case SteamSignal.Unknown:
                    if (!accepted && Current.State is (LoginState.Connecting or LoginState.Starting or LoginState.Unknown))
                        Update(LoginState.Unknown, "");
                    break;
            }
        }
    }

    private void Authenticate()
    {
        authenticated = true;
        Update(LoginState.Authenticated, "登录成功");
    }

    public void TimedOut() => Update(LoginState.TimedOut, accepted ? "Steam 已接受登录，但账户信息等待超时。请稍后重试，无需清除登录数据。" : "连接超时，请重试。");
    public void Reset()
    {
        protocol.Reset();
        ClearInitialInput();
    }
}
