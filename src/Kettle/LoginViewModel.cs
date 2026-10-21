using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Kettle.Core;

namespace Kettle;

public sealed record AuthenticatedSession(SteamSession Session, string AccountName, string SteamCmdPath, bool IgnoredLegacyLibraries = false);
public sealed class LoginViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Func<SteamSession> factory;
    private readonly Action<Action> dispatch;
    private readonly PortableSettings store;
    private string steamPath;
    private SteamSession? session;
    private LoginUpdate state = new(LoginState.Idle, "");
    private string username = "", password = "", guardCode = "", response = "", notice = "";
    private int generation;
    private bool completing, loadFailed, ignoredLegacyLibraries;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<AuthenticatedSession>? Authenticated;
    public Command LoginCommand
    {
        get;
    }
    public Command SubmitCommand
    {
        get;
    }
    public Command CancelCommand
    {
        get;
    }

    public LoginViewModel(LaunchOptions options, Func<SteamSession>? factory = null, Action<Action>? dispatcher = null, string? dataDirectory = null)
    {
        this.factory = factory ?? (() => new SteamSession());
        dispatch = dispatcher ?? (action =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                action();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(action);
        });
        store = new(dataDirectory ?? AppContext.BaseDirectory);
        UserSettings saved = new();
        try
        {
            saved = store.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            loadFailed = true;
            notice = L.Text(TextId.CouldNotReadSettingsDefaultsAreInUse);
        }

        ignoredLegacyLibraries = saved.HasIgnoredLegacyLibraries;
        username = saved.Username;
        steamPath = SteamCmdPath.Resolve(options.SteamCmd ?? saved.SteamCmd ?? SteamCmdPath.Default(AppContext.BaseDirectory), Environment.CurrentDirectory).FullPath;
        LoginCommand = new(() => _ = StartAsync(), () => !completing && !LoginActive && Regex.IsMatch(Username, @"\A[A-Za-z0-9_]{2,64}\z"));
        SubmitCommand = new(() => _ = SubmitAsync(), () => InputNeeded && Response.Length > 0);
        CancelCommand = new(() => _ = CancelAsync(), () => LoginActive && !completing);
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public string ResolvedSteamCmdPath => steamPath;

    public void SetSteamCmdPath(string executable)
    {
        var selected = SteamCmdPath.Resolve(executable, Environment.CurrentDirectory);
        if (!selected.IsValid)
            throw new IOException(selected.Message);
        steamPath = selected.FullPath;
        Notify(nameof(ResolvedSteamCmdPath));
    }

    public string Username
    {
        get => username;
        set
        {
            username = value ?? "";
            Notify();
            LoginCommand.Refresh();
        }
    }

    public string Password
    {
        get => password;
        set
        {
            password = value ?? "";
            Notify();
        }
    }

    public string GuardCode
    {
        get => guardCode;
        set
        {
            guardCode = value ?? "";
            Notify();
        }
    }

    private void ClearInitialInput()
    {
        Password = "";
        GuardCode = "";
    }

    public string Response
    {
        get => response;
        set
        {
            response = value ?? "";
            Notify();
            SubmitCommand.Refresh();
        }
    }

    public string Notice
    {
        get => notice;
        set
        {
            notice = value;
            Notify();
            Notify(nameof(HasNotice));
        }
    }

    public bool HasNotice => Notice.Length > 0;
    public string LoginMessage => state.Message;
    public bool HasLoginMessage => !string.IsNullOrWhiteSpace(state.Message);
    public bool LoginActive => state.IsActive || completing;
    public bool CanEditUsername => !LoginActive;
    public bool InputNeeded => state.NeedsInput;
    public string InputLabel => state.State switch
    {
        LoginState.Password => L.Text(TextId.SteamPassword),
        LoginState.GuardCode => L.Text(TextId.SteamGuardCode),
        _ => L.Text(TextId.SteamAccountName)
    };
    public char ResponsePasswordChar => state.State == LoginState.Username ? '\0' : '●';
    public string LoginButtonLabel => state.State is LoginState.Failed or LoginState.Cancelled or LoginState.Exited or LoginState.TimedOut ? L.Text(TextId.LogInAgain) : L.Text(TextId.LogIn);

    private void Apply(LoginUpdate update)
    {
        state = update;
        Response = "";
        foreach (var name in new[]
        {
            nameof(LoginMessage),
            nameof(HasLoginMessage),
            nameof(LoginActive),
            nameof(CanEditUsername),
            nameof(InputNeeded),
            nameof(InputLabel),
            nameof(ResponsePasswordChar),
            nameof(LoginButtonLabel)
        }

        )
            Notify(name);
        LoginCommand.Refresh();
        SubmitCommand.Refresh();
        CancelCommand.Refresh();
        if (update.State != LoginState.Authenticated || completing || session is null)
            return;
        completing = true;
        try
        {
            if (!loadFailed)
            {
                var saved = store.Load();
                store.Save(saved with
                {
                    Username = Username.Trim(),
                    SteamCmd = steamPath
                });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Notice = L.Text(TextId.CouldNotSaveTheAccountNameCheckThatThe);
        }

        var handoff = new AuthenticatedSession(session, Username.Trim(), steamPath, ignoredLegacyLibraries);
        if (Authenticated is not null)
        {
            session.Updated -= Updated;
            session = null;
            Authenticated(handoff);
        }
    }

    private void Updated(LoginUpdate update)
    {
        var current = generation;
        dispatch(() =>
        {
            if (current == generation)
                Apply(update);
        });
    }

    public async Task StartAsync()
    {
        if (!LoginCommand.CanExecute(null))
            return;
        var initialPassword = Password.ToCharArray();
        var initialCode = GuardCode.ToCharArray();
        ClearInitialInput();
        try
        {
            if (!SteamCmdPath.Resolve(steamPath, Environment.CurrentDirectory).IsValid)
            {
                Notice = L.Text(TextId.SteamCMDWasNotFoundSpecifyItsLocationWithSteamcmd);
                return;
            }

            var startGeneration = ++generation;
            Notice = "";
            Apply(new(LoginState.Starting, L.Text(TextId.Connecting2)));
            var oldSession = session;
            session = null;
            if (oldSession is not null)
            {
                oldSession.Updated -= Updated;
                await oldSession.DisposeAsync();
            }

            if (startGeneration != generation)
                return;
            try
            {
                if (startGeneration != generation)
                    return;
                session = factory();
                session.Updated += Updated;
                var starting = session;
                await starting.StartAsync(steamPath, Username.Trim(), initialPassword, initialCode);
                if (startGeneration == generation && starting.StartupCleanup?.Skipped is { Length: > 0 } skipped)
                    Notice = L.Text(TextId.PreLaunchCleanupSkipped) + string.Join("；", skipped);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                if (startGeneration == generation)
                {
                    Notice = e.Message;
                    Apply(new(LoginState.Failed, L.Text(TextId.LoginCouldNotBeCompletedRightNowRetry)));
                }
            }
        }
        finally
        {
            Array.Clear(initialPassword);
            Array.Clear(initialCode);
        }
    }

    private async Task SubmitAsync()
    {
        var input = Response.ToCharArray();
        Response = "";
        try
        {
            if (session is not null)
                await session.SubmitAsync(input);
        }
        finally
        {
            Array.Clear(input);
        }
    }

    public async Task CancelAsync()
    {
        generation++;
        Response = "";
        ClearInitialInput();
        if (session is not null)
        {
            session.Updated -= Updated;
            await session.DisposeAsync();
            session = null;
        }

        completing = false;
        Apply(new(LoginState.Cancelled, L.Text(TextId.LoginCancelled)));
    }

    public async ValueTask DisposeAsync()
    {
        generation++;
        Response = "";
        ClearInitialInput();
        if (session is not null)
        {
            session.Updated -= Updated;
            await session.DisposeAsync();
            session = null;
        }
    }
}
