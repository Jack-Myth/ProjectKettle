namespace Kettle.Core;

public enum SteamSignal
{
    Ready,
    Username,
    Password,
    GuardCode,
    MobileConfirmation,
    Connecting,
    Accepted,
    UserInfo,
    Success,
    Failure,
    Warning,
    ClientConfig,
    UserInfoPending,
    Unknown,
    Completion
}

/// <summary>Streaming recognizer with pattern offsets and a short-lived input-echo matcher.
/// The matcher is zeroed after use/reset; no terminal transcript or credentials are persisted.</summary>
public sealed class SteamProtocol
{
    private sealed class Pattern(string text, SteamSignal signal)
    {
        public readonly string Text = text.ToLowerInvariant();
        public readonly SteamSignal Signal = signal;
        public int Offset;
        public readonly int[] Prefix = Prefixes(text.ToLowerInvariant());
        private static int[] Prefixes(string text)
        {
            var table = new int[text.Length];
            for (int i = 1, j = 0; i < text.Length; i++)
            {
                while (j > 0 && text[i] != text[j])
                    j = table[j - 1];
                if (text[i] == text[j])
                    j++;
                table[i] = j;
            }

            return table;
        }

        public bool Push(char c)
        {
            while (Offset > 0 && Text[Offset] != c)
                Offset = Prefix[Offset - 1];
            if (Text[Offset] == c)
                Offset++;
            if (Offset != Text.Length)
                return false;
            Offset = Prefix[Offset - 1];
            return true;
        }
    }

    private readonly Pattern[] patterns = [new("Steam>", SteamSignal.Ready), new("username:", SteamSignal.Username), new("account name:", SteamSignal.Username), new("login:", SteamSignal.Username), new("password:", SteamSignal.Password), new("enter your password", SteamSignal.Password), new("Steam Guard code:", SteamSignal.GuardCode), new("two-factor code:", SteamSignal.GuardCode), new("two-factor auth code:", SteamSignal.GuardCode), new("two-factor authentication code:", SteamSignal.GuardCode), new("authenticator code:", SteamSignal.GuardCode), new("enter the code", SteamSignal.GuardCode), new("enter the current code", SteamSignal.GuardCode), new("please confirm the login", SteamSignal.MobileConfirmation), new("waiting for confirmation", SteamSignal.MobileConfirmation), new("confirm this sign in", SteamSignal.MobileConfirmation), new("confirm the sign in", SteamSignal.MobileConfirmation), new("logging in user", SteamSignal.Connecting), new("logging in using", SteamSignal.Connecting), new("waiting for client config", SteamSignal.ClientConfig), new("waiting for user info", SteamSignal.UserInfoPending), new("to Steam Public...OK", SteamSignal.Accepted), new("waiting for user info...OK", SteamSignal.UserInfo), new("waiting for user info…OK", SteamSignal.UserInfo), new("waiting for user info... OK", SteamSignal.UserInfo), new("waiting for user info… OK", SteamSignal.UserInfo), new("logged in OK", SteamSignal.Success), new("logged in successfully", SteamSignal.Success), new("FAILED", SteamSignal.Warning), new("invalid password", SteamSignal.Failure), new("invalid login", SteamSignal.Failure), new("incorrect password", SteamSignal.Failure), new("invalid code", SteamSignal.Failure), new("two-factor code mismatch", SteamSignal.Failure), new("rate limit", SteamSignal.Failure), new("account logon denied", SteamSignal.Failure), new("ERROR!", SteamSignal.Warning), new("login failure", SteamSignal.Failure)];
    private readonly TerminalTextNormalizer normalizer = new();
    private bool suppressEcho;
    private int completionLine;
    private char[]? expectedEcho;
    private int echoOffset;
    private void ClearEcho()
    {
        if (expectedEcho is not null)
            Array.Clear(expectedEcho);
        expectedEcho = null;
        echoOffset = 0;
    }

    public void SuppressInputEcho(ReadOnlySpan<char> response)
    {
        lock (patterns)
        {
            Reset();
            expectedEcho = response.ToArray();
            suppressEcho = true;
        }
    }

    public void SuppressInputEcho()
    {
        lock (patterns)
        {
            Reset();
            suppressEcho = true;
        }
    }

    public IReadOnlyList<SteamSignal> Feed(string chunk)
    {
        lock (patterns)
            return FeedCore(normalizer.Feed(chunk));
    }

    internal IReadOnlyList<SteamSignal> FeedNormalized(string chunk)
    {
        lock (patterns)
            return FeedCore(chunk);
    }

    private IReadOnlyList<SteamSignal> FeedCore(string chunk)
    {
        var signals = new List<SteamSignal>();
        foreach (var raw in chunk)
        {
            if (suppressEcho)
            {
                if (expectedEcho is null)
                {
                    if (raw is '\r' or '\n')
                        suppressEcho = false;
                    else
                        continue;
                }
                else
                {
                    if (raw is '\r' or '\n')
                    {
                        if (echoOffset < expectedEcho.Length)
                            for (int i = 0; i < echoOffset; i++)
                                Push(expectedEcho[i], signals);
                        ClearEcho();
                        suppressEcho = false;
                        continue;
                    }

                    if (echoOffset < expectedEcho.Length && raw == expectedEcho[echoOffset])
                    {
                        echoOffset++;
                        continue;
                    }

                    // No matching echo: preserve the first server response even when
                    // secret input was not echoed and no newline preceded that response.
                    for (int i = 0; i < echoOffset; i++)
                        Push(expectedEcho[i], signals);
                    ClearEcho();
                    suppressEcho = false;
                }
            }

            // A standalone OK line completes the currently pending login stage.
            // Keep only a three-state recognizer, never retain console or input text.
            if (raw is '\r' or '\n')
            {
                if (completionLine == 2)
                    signals.Add(SteamSignal.Completion);
                completionLine = 0;
                continue;
            }

            if (raw is ' ' or '\t')
            {
                if (completionLine == 1)
                    completionLine = -1;
            }
            else if (raw >= ' ')
                completionLine = completionLine == 0 && raw is 'o' or 'O' ? 1 : completionLine == 1 && raw is 'k' or 'K' ? 2 : -1;
            if (raw is '\b' || raw < ' ')
                continue;
            Push(raw, signals);
        }

        return signals;
    }

    private void Push(char raw, List<SteamSignal> signals)
    {
        var c = char.ToLowerInvariant(raw);
        bool matched = false;
        foreach (var pattern in patterns)
            if (pattern.Push(c))
            {
                signals.Add(pattern.Signal);
                matched = true;
            }

        if (!matched && c is ':' or '?')
            signals.Add(SteamSignal.Unknown);
    }

    public void Reset()
    {
        lock (patterns)
        {
            foreach (var pattern in patterns)
                pattern.Offset = 0;
            completionLine = 0;
            normalizer.Reset();
            suppressEcho = false;
            ClearEcho();
        }
    }
}

public enum LoginState
{
    Idle,
    Starting,
    Connecting,
    Username,
    Password,
    GuardCode,
    MobileConfirmation,
    Unknown,
    Authenticated,
    Failed,
    Cancelled,
    TimedOut,
    Exited
}

public sealed record LoginUpdate(LoginState State, string Message)
{
    public bool NeedsInput => State is LoginState.Username or LoginState.Password or LoginState.GuardCode;
    public bool IsActive => State is LoginState.Starting or LoginState.Connecting or LoginState.Username or LoginState.Password or LoginState.GuardCode or LoginState.MobileConfirmation or LoginState.Unknown;
}

public interface ISteamTerminal : IAsyncDisposable
{
    bool SupportsPromptBoundaries => false;

    SessionProcessSafety InspectProcesses() => new(false, L.Text(TextId.CouldNotVerifySessionProcessesTheSessionWasNot));
    Task StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
    IAsyncEnumerable<string> ReadAsync(CancellationToken cancellationToken);
    Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken cancellationToken);
    Task StopAsync();
}

public sealed record SessionProcessSafety(bool CanDisconnect, string Message);
