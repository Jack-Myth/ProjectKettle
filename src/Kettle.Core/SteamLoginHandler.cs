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

    public LoginUpdate Current { get; private set; } = new(LoginState.Idle, L.Text(TextId.EnterYourSteamAccountNameToContinue));
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
        Update(LoginState.Connecting, L.Text(TextId.LoggingIn));
    }

    public void PrepareInput(ReadOnlySpan<char> response)
    {
        protocol.SuppressInputEcho(response);
        Update(LoginState.Connecting, L.Text(TextId.LoggingIn));
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
                        Update(LoginState.Username, L.Text(TextId.EnterYourSteamAccountName));
                    break;
                case SteamSignal.Password:
                    if (!accepted)
                        Update(LoginState.Password, L.Text(TextId.EnterPassword));
                    break;
                case SteamSignal.GuardCode:
                    if (!accepted)
                        Update(LoginState.GuardCode, L.Text(TextId.EnterYourSteamGuardCode));
                    break;
                case SteamSignal.MobileConfirmation:
                    if (!accepted)
                        Update(LoginState.MobileConfirmation, L.Text(TextId.ConfirmLoginOnYourPhone));
                    break;
                case SteamSignal.Connecting:
                    connecting = true;
                    if (!accepted)
                        Update(LoginState.Connecting, L.Text(TextId.Connecting));
                    break;
                case SteamSignal.Accepted:
                    if (connecting && !accepted)
                    {
                        accepted = true;
                        Update(LoginState.Connecting, L.Text(TextId.SteamAcceptedTheLoginFetchingAccountInformation));
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
                        Update(LoginState.Connecting, L.Text(TextId.FetchingAccountConfiguration));
                    }

                    break;
                case SteamSignal.UserInfoPending:
                    accepted = true;
                    fetchingUserInfo = true;
                    fetchingConfig = false;
                    ClearInitialInput();
                    Update(LoginState.Connecting, L.Text(TextId.FetchingUserInformation));
                    break;
                case SteamSignal.Warning:
                    if (accepted && fetchingConfig && !Current.NeedsInput)
                        Update(LoginState.Connecting, L.Text(TextId.AccountConfigurationIsTakingLongerToLoadPleaseWait));
                    break;
                case SteamSignal.Failure:
                    Update(LoginState.Failed, L.Text(TextId.LoginFailedRetry));
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
        Update(LoginState.Authenticated, L.Text(TextId.LoggedInSuccessfully));
    }

    public void TimedOut() => Update(LoginState.TimedOut, accepted ? L.Text(TextId.SteamAcceptedTheLoginButAccountInformationTimedOut) : L.Text(TextId.ConnectionTimedOutRetry));
    public void Reset()
    {
        protocol.Reset();
        ClearInitialInput();
    }
}
