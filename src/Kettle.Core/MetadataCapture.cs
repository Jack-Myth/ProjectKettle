using System.Text;
using System.Text.RegularExpressions;

namespace Kettle.Core;
// Shared bounded accumulator for a command or the authenticated idle interval.
internal sealed class ConsoleOutputBuffer(bool rolling = false)
{
    internal const int ResponseCapacity = 1024 * 1024 / sizeof(char);
    internal const int IdleCapacity = 64 * 1024 / sizeof(char);
    internal readonly object Sync = new();
    private char[] text = new char[4096];
    private int start, length;
    private bool truncated;
    private int CapacityLimit => rolling ? IdleCapacity : ResponseCapacity;
    private readonly TerminalTextNormalizer normalizer = new();
    private bool incomplete;
    internal TerminalLayoutCapture Layout = new();
    internal DateTime Changed = DateTime.UtcNow;
    internal string Text => string.Create(length, this, static (span, buffer) => buffer.CopyTo(span));
    internal int Length => length;
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
            foreach (char c in normalized)
            {
                if (c == '\b')
                {
                    if (length > 0)
                        text[(start + --length) % text.Length] = '\0';
                    continue;
                }
                if (length == text.Length && text.Length < CapacityLimit)
                {
                    var expanded = new char[Math.Min(text.Length * 2, CapacityLimit)];
                    CopyTo(expanded);
                    text = expanded;
                    start = 0;
                }
                if (length == text.Length)
                {
                    if (!rolling)
                        throw new IOException("SteamCMD 单次响应超过 1 MiB，无法完整解析。");
                    start = (start + 1) % text.Length;
                    length--;
                    truncated = true;
                }
                text[(start + length++) % text.Length] = c;
            }
        }
    }

    internal void Clear()
    {
        lock (Sync)
        {
            Array.Clear(text);
            start = length = 0;
            truncated = false;
            normalizer.Reset();
            incomplete = false;
            Layout = new();
            Changed = DateTime.UtcNow;
        }
    }

    private void CopyTo(Span<char> destination)
    {
        int first = Math.Min(length, text.Length - start);
        text.AsSpan(start, first).CopyTo(destination);
        text.AsSpan(0, length - first).CopyTo(destination[first..]);
    }

    internal SteamCommandOutputDiagnostic Diagnostic(string command, string stage, bool blank, string state, string? echo = null)
    {
        lock (Sync)
        {
            var raw = Text;
            var classified = GameNoticeFilter.Process(raw);
            return new(command, stage, DateTime.UtcNow, CommandOutputText.Limit(raw), CommandOutputText.Limit(classified.Cleaned), classified.Notices.TakeLast(32).Select(n => n.Event).ToArray(), classified.Incomplete || IncompleteControl, blank, truncated || raw.Length > 8192)
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
internal sealed class CommandResponseCapture(IReadOnlyList<string> commands, ConsoleOutputBuffer? reusableBuffer = null)
{
    private readonly string command = commands.Count == 1 ? commands[0] : throw new ArgumentException("One command per capture.");
    private readonly ConsoleOutputBuffer buffer = reusableBuffer ?? new();
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
        // Some terminal hosts omit argument spaces in the rendered echo. Relax only
        // separators in numeric commands, never command letters, IDs or quoted paths.
        if (echoPattern is null)
        {
            bool numericArguments = Regex.IsMatch(command, @"\A[a-z_]+(?: [0-9]+)+\z", RegexOptions.CultureInvariant);
            echoPattern = new System.Text.RegularExpressions.Regex(string.Join(gap, command.Select(c => c == ' ' && numericArguments ? @"[ \t]*" : System.Text.RegularExpressions.Regex.Escape(c.ToString()))), System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        }
        try
        {
            for (var match = echoPattern.Match(text); match.Success; match = match.NextMatch())
            {
                int lineStart = match.Index == 0 ? 0 : text.LastIndexOf('\n', match.Index - 1) + 1;
                if (!ConsolePrompts.IsNoise(text[lineStart..match.Index]))
                    continue;
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

            // Output preceding this command's echo belongs to the idle interval,
            // including logs racing with the send. It is retained in diagnostics.
            var classified = GameNoticeFilter.Process(output[(echo.Index + echo.Length)..], preserveNoticePrompts: true);
            if (buffer.IncompleteControl)
            {
                state = "AwaitingOutputFragment";
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
