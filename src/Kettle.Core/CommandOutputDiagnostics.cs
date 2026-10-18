using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kettle.Core;

public sealed record SteamCommandOutputDiagnostic(string Command, string Stage, DateTime Utc, string Raw, string Cleaned, GameRuntimeEvent[] Notices, bool IncompleteNotice, bool ExtraBlankRequested, bool Truncated)
{
    public string? Payload
    {
        get; init;
    }
    public int UncertainNoticeIdentities
    {
        get; init;
    }
    public TerminalChunkLayout[] RawChunkLayouts { get; init; } = [];
    public long RawCharacterCount
    {
        get; init;
    }
    public int TerminalColumns { get; init; } = ConPtyTerminal.Columns;
    public string BoundaryState { get; init; } = "AwaitingEcho";
    public string? CommandEcho
    {
        get; init;
    }
    public bool CommandEchoTruncated
    {
        get; init;
    }
}

public sealed record TerminalChunkLayout(long Start, int Length, string Layout, bool Truncated);
internal static class CommandOutputPrivacy
{
    // Command-only evidence, never the login stream. Unknown/credential-bearing lines are
    // fingerprints, not transcripts. Game command arguments and config values are private.
    public static string Sanitize(string text)
    {
        var result = new StringBuilder();
        bool pidContinuation = false;
        foreach (var raw in text.Replace("\r", "").Split('\n').TakeLast(160))
        {
            var line = raw.Length > 1024 ? raw[..1024] : raw;
            if (Regex.IsMatch(line, @"(?i)password|token|authorization|login|guard|ticket|cookie|secret"))
            {
                line = "<sensitive line omitted>";
                pidContinuation = false;
            }
            else if (Regex.IsMatch(line, @"(?i)\bcommandline\b"))
            {
                line = "   commandline <arguments omitted>";
                pidContinuation = false;
            }
            else if (Regex.IsMatch(line, @"(?i)Game process (?:added|updated|removed).*\bAppID\s+\d+"))
            {
                // Safe identity suffix may lie past the displayed path limit at wide
                // terminal sizes. Never preserve the omitted command arguments.
                var tail = Regex.Match(raw, @"\bProcID[ \t]+\d+(?:[ \t]*,[ \t]*IP[ \t]+[0-9a-f.:]+)?[ \t]*\z", RegexOptions.IgnoreCase);
                pidContinuation = tail.Success && char.IsAsciiDigit(raw[^1]);
                line = Regex.Replace(line, @"(?i)(Game process (?:added|updated|removed)\s*:\s*AppID\s+\d+)\b.*", "$1 <details omitted>") + (tail.Success ? ", " + tail.Value : "");
            }
            else if (pidContinuation && Regex.IsMatch(line, @"\A[ \t]*\d+[ \t]*\z"))
                pidContinuation = char.IsAsciiDigit(line[^1]);
            else if (Regex.IsMatch(line, @"\A\s*AppID\s+\d+\b", RegexOptions.IgnoreCase))
                line = Regex.Match(line, @"AppID\s+\d+", RegexOptions.IgnoreCase).Value + " <status title omitted>";
            else if (!Regex.IsMatch(line, @"\A\s*(?:(?:Steam>)*\s*(?:apps_running|app_status \d+|app_launch \d+|app_stop \d+ 0|find __kettle_boundary_[a-f0-9]+)?|(?:Steam>)*\s*ConVars:|Commands:|-\s*AppID\s+\d+,\s*GameID\s+\d+,\s*ProcessID\s+\d+,\s*IP:Port\s+[0-9a-f.:]+|extra info\s+""""|associated PIDs\s*\([\d\s{},:parent]*\)|dwLastIsRunningCheck\s+\d+|-\s*size on disk\s*:\s*\d+\s+bytes(?:,\s*BuildID\s+\d+)?|[{}])\s*\z", RegexOptions.IgnoreCase))
                line = "<unclassified chars=" + raw.Length + " sha256=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16] + ">";
            result.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(raw) && !Regex.IsMatch(raw, @"\A[ \t]*\d+[ \t]*\z") && !raw.Contains("Game process", StringComparison.OrdinalIgnoreCase))
                pidContinuation = false;
        }

        var value = result.ToString();
        return value.Length <= 8192 ? value : value[^8192..];
    }
}

// Preserve exact chunk offsets, CR/LF/backspace and ANSI commands, but mask visible
// text (including OSC titles). This is not a credential or command-line transcript.
internal sealed class TerminalLayoutCapture
{
    private readonly Queue<TerminalChunkLayout> chunks = new();
    private int escape;
    public long Count
    {
        get; private set;
    }

    public TerminalChunkLayout[] Snapshot() => chunks.ToArray();
    public void Append(string text)
    {
        var layout = new StringBuilder();
        long start = Count;
        Count += text.Length;
        foreach (char c in text)
        {
            if (escape == 1)
            {
                layout.Append(c is '[' or ']' ? c : 'x');
                escape = c == '[' ? 2 : c == ']' ? 3 : 0;
                continue;
            }

            if (escape == 2)
            {
                layout.Append(c < 32 || c is >= ' ' and <= '~' ? c : 'x');
                if (c is >= '@' and <= '~' || c > 126)
                    escape = 0;
                continue;
            }

            if (escape == 3)
            {
                layout.Append(c < 32 ? c : 'x');
                if (c == '\a')
                    escape = 0;
                else if (c == '\x1b')
                    escape = 4;
                continue;
            }

            if (escape == 4)
            {
                layout.Append(c == '\\' || c < 32 ? c : 'x');
                escape = c == '\\' ? 0 : 3;
                continue;
            }

            if (c == '\x1b')
            {
                layout.Append(c);
                escape = 1;
            }
            else
                layout.Append(c < 32 || c == 127 || c == ' ' ? c : 'x');
        }

        var value = layout.ToString();
        chunks.Enqueue(new(start, text.Length, value.Length <= 512 ? value : value[^512..], value.Length > 512));
        while (chunks.Count > 16)
            chunks.Dequeue();
    }
}
