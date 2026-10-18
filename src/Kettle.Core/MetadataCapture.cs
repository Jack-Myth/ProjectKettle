using System.Text;
using System.Text.RegularExpressions;

namespace Kettle.Core;
// Shared bounded accumulator for a command or the authenticated idle interval.
internal sealed class ConsoleOutputBuffer
{
    internal readonly object Sync = new();
    private readonly StringBuilder text = new();
    private readonly TerminalTextNormalizer normalizer = new();
    private bool incomplete;
    internal TerminalLayoutCapture Layout = new();
    internal DateTime Changed = DateTime.UtcNow;
    internal string Text => text.ToString();
    internal int Length => text.Length;
    internal bool IncompleteControl => incomplete;

    internal void Append(string chunk)
    {
        AppendNormalized(normalizer.Feed(chunk), chunk, normalizer.IncompleteControl);
    }

    internal void AppendNormalized(string normalized, string raw, bool incompleteControl)
    {
        lock (Sync)
        {
            Changed = DateTime.UtcNow;
            Layout.Append(raw);
            incomplete = incompleteControl;
            TerminalTextNormalizer.Append(text, normalized);
            if (text.Length > 32 * 1024 * 1024)
                throw new IOException("终端输出超出读取上限。");
        }
    }

    internal void Clear()
    {
        lock (Sync)
        {
            text.Clear();
            normalizer.Reset();
            incomplete = false;
            Layout = new();
            Changed = DateTime.UtcNow;
        }
    }

    internal SteamCommandOutputDiagnostic Diagnostic(string command, string stage, bool blank, string state, string? echo = null)
    {
        lock (Sync)
        {
            var raw = Text;
            var classified = GameNoticeFilter.Process(raw);
            return new(command, stage, DateTime.UtcNow, CommandOutputPrivacy.Sanitize(raw), CommandOutputPrivacy.Sanitize(classified.Cleaned), classified.Notices.TakeLast(32).Select(n => n.Event).ToArray(), classified.Incomplete || IncompleteControl, blank, raw.Length > 8192)
            {
                UncertainNoticeIdentities = classified.Notices.Count(n => !n.IdentityReliable),
                RawChunkLayouts = Layout.Snapshot(),
                RawCharacterCount = Layout.Count,
                BoundaryState = state,
                CommandEcho = echo is null ? null : echo[..Math.Min(echo.Length, 512)],
                CommandEchoTruncated = echo?.Length > 512
            };
        }
    }
}

// Transport framing only: echo, terminal prompts, and asynchronous game notices.
// No command grammar, VDF, application IDs, counts, or business parsers belong here.
internal sealed class CommandResponseCapture(IReadOnlyList<string> commands)
{
    private readonly string command = commands.Count == 1 ? commands[0] : throw new ArgumentException("One command per capture.");
    private readonly ConsoleOutputBuffer buffer = new();
    private System.Text.RegularExpressions.Regex? echoPattern;
    private bool blankSent;
    private string state = "AwaitingEcho";
    public void MarkBlankSent()
    {
        lock (buffer.Sync)
        {
            blankSent = true;
            state = "AwaitingPrompts";
        }
    }

    private System.Text.RegularExpressions.Match Echo(string text)
    {
        const string gap = @"(?:\n(?:[ \t\n]|Steam>)*)?";
        echoPattern ??= new System.Text.RegularExpressions.Regex(string.Join(gap, command.Select(c => System.Text.RegularExpressions.Regex.Escape(c.ToString()))), System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        try
        {
            for (var match = echoPattern.Match(text); match.Success; match = match.NextMatch())
            {
                int end = match.Index + match.Length;
                if (end == text.Length || char.IsWhiteSpace(text[end]) || text.AsSpan(end).StartsWith("Steam>", StringComparison.Ordinal))
                    return match;
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
        }

        return System.Text.RegularExpressions.Match.Empty;
    }

    public void AppendNormalized(string text, string raw, bool incomplete) => buffer.AppendNormalized(text, raw, incomplete);
    public bool TryComplete(out string result)
    {
        lock (buffer.Sync)
        {
            result = "";
            // A short terminal settling interval consumes redraws/duplicate prompts;
            // it never inspects the command's business response.
            if (DateTime.UtcNow - buffer.Changed < TimeSpan.FromMilliseconds(250))
                return false;
            var output = buffer.Text;
            var echo = Echo(output);
            if (!echo.Success)
            {
                state = "AwaitingEcho";
                return false;
            }

            var prefix = GameNoticeFilter.Process(output[..echo.Index]);
            var classified = GameNoticeFilter.Process(output[(echo.Index + echo.Length)..], preserveNoticePrompts: true);
            if (buffer.IncompleteControl)
            {
                state = "AwaitingOutputFragment";
                return false;
            }

            if (!ConsolePrompts.IsNoise(prefix.Cleaned))
            {
                state = "UnclaimedBeforeEcho";
                return false;
            }

            var tail = classified.Cleaned;
            var prompts = ConsolePrompts.Find(tail);
            // SteamCMD may collapse the empty-input prompt or print its prompt
            // before asynchronous text (recorded app_status). A second prompt
            // and a whitespace-only tail are not protocol guarantees.
            if (!blankSent || prompts.Length == 0)
            {
                state = "AwaitingPrompt";
                return false;
            }

            result = ConsolePrompts.Remove(tail);
            state = "Complete";
            return true;
        }
    }

    public void Clear() => buffer.Clear();
    public string DiagnosticTail()
    {
        lock (buffer.Sync)
        {
            var value = buffer.Text;
            return value.Length <= 16384 ? value : value[^16384..];
        }
    }

    public SteamCommandOutputDiagnostic Diagnostic(string stage)
    {
        lock (buffer.Sync)
        {
            var echo = Echo(GameNoticeFilter.Process(buffer.Text, preserveNoticePrompts: true).Cleaned);
            return buffer.Diagnostic(command, stage, blankSent, state, echo.Success ? echo.Value : null);
        }
    }
}

public interface ISteamMetadataCommands
{
    bool IsAuthenticated
    {
        get;
    }

    Task<string> QueryMetadataAsync(IReadOnlyList<string> commands, TimeSpan timeout, CancellationToken token = default);
}
