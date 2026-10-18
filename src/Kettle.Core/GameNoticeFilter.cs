using System.Text;
using System.Text.RegularExpressions;

namespace Kettle.Core;

public sealed record GameNotice(int Start, int Length, GameRuntimeEvent Event, bool IdentityReliable = true);
public sealed record GameNoticeOutput(string Cleaned, GameNotice[] Notices, bool Incomplete, int RetainFrom);
/// <summary>Shared grammar for event delivery and command-response preprocessing.
/// Only complete typed notices are removed. A truncated/malformed notice stays data.</summary>
public static class GameNoticeFilter
{
    private static readonly Regex Start = new(@"(?:Steam>[ \t]*)*Game process (added|updated|removed)[ \t]*:[ \t]*AppID[ \t]+(\d+)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Complete = new("\\AGame process (added|updated|removed)[ \\t]*:[ \\t]*AppID[ \\t]+(\\d+)[ \\t]+(?:\"[\\s\\S]*\"|[^\\n]*)[ \\t]*,[ \\t]*ProcID[ \\t]+(\\d+)(?:[ \\t]*,[ \\t]*IP[ \\t]+[^\\s]+)?[ \\t]*\\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    // Never absorb a response field, prompt, command, or another notice as a wrapped path.
    private static readonly Regex Boundary = new(@"^[ \t]*(?:Steam>|Game process\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Failure = new(@"(?m)^[ \t]*(?:Steam>[ \t]*)*AppId[ \t]+(\d+)[ \t]+launch failed[^\n]*(?:\n|\z)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    public static GameNoticeOutput Process(string text, bool preserveNoticePrompts = false, bool deferTrailingIdentity = false, int terminalColumns = ConPtyTerminal.Columns)
    {
        var notices = new List<GameNotice>();
        bool incomplete = false;
        int retain = text.LastIndexOf('\n') + 1;
        // Events may interrupt any response position, including command echo and VDF values.
        foreach (Match start in Start.Matches(text))
        {
            if (notices.Any(n => start.Index < n.Start + n.Length))
                continue;
            int end = start.Index, lines = 0;
            bool found = false;
            while (lines++ < 32 && end - start.Index <= 8192)
            {
                int next = text.IndexOf('\n', end);
                if (next < 0)
                    break;
                if (end > start.Index && Boundary.IsMatch(text[end..next]))
                    break;
                end = next + 1;
                var original = text[start.Index..next];
                var candidate = original.TrimEnd();
                candidate = Regex.Replace(candidate, @"\A[ \t]*(?:Steam>[ \t]*)*", "");
                var match = Complete.Match(candidate);
                if (!match.Success || !uint.TryParse(match.Groups[2].Value, out var app) || app == 0 || !int.TryParse(match.Groups[3].Value, out var pid) || pid <= 0)
                    continue;
                bool reliable = true;
                // A ConPTY physical line can end INSIDE the PID. Never commit that
                // prefix as an event identity. Consume only digit-only continuations
                // immediately following this typed notice; unrelated numeric output stays.
                if (Regex.IsMatch(candidate, @"\bProcID[ \t]+\d+\z") && original.Length > 0 && char.IsAsciiDigit(original[^1]))
                {
                    int look = end;
                    bool terminated = false;
                    var physicalLine = original[(original.LastIndexOf('\n') + 1)..];
                    int prefixColumns = original.Contains('\n') ? 0 : start.Index - (start.Index == 0 ? 0 : text.LastIndexOf('\n', start.Index - 1) + 1);
                    bool wrapEdge = physicalLine.All(c => c is >= ' ' and <= '~') && physicalLine.Length + prefixColumns >= terminalColumns - 1 && physicalLine.Length + prefixColumns <= terminalColumns;
                    while (look < text.Length)
                    {
                        int newline = text.IndexOf('\n', look);
                        int lineEnd = newline < 0 ? text.Length : newline;
                        var following = text[look..lineEnd];
                        if (string.IsNullOrWhiteSpace(following))
                        {
                            if (newline < 0)
                                break;
                            look = newline + 1;
                            continue;
                        }

                        if (!Regex.IsMatch(following, @"\A[ \t]*\d+[ \t]*\z"))
                            break;
                        if (!wrapEdge)
                            break; // Not every integer after a notice is a continuation.
                        if (newline < 0)
                            break; // retain a partial chunk until its line is complete
                        reliable = false;
                        end = newline + 1;
                        look = end;
                        terminated = !char.IsAsciiDigit(following[^1]);
                        break; // A PID is at most ten digits: at this terminal width it wraps once.
                    }

                    while (look < text.Length)
                    {
                        int nl = text.IndexOf('\n', look);
                        if (nl < 0 || !string.IsNullOrWhiteSpace(text[look..nl]))
                            break;
                        look = nl + 1;
                    }

                    int boundaryEnd = text.IndexOf('\n', look);
                    if (boundaryEnd < 0)
                        boundaryEnd = text.Length;
                    bool boundary = look < text.Length && Boundary.IsMatch(text[look..boundaryEnd]);
                    // Away from the terminal wrap edge, the newline completes the event,
                    // even when the interrupted response resumes with digits.
                    if (deferTrailingIdentity && wrapEdge && !terminated && !boundary)
                        break;
                    if (wrapEdge && !terminated && !boundary)
                        reliable = false;
                }

                var kind = match.Groups[1].Value.ToLowerInvariant() switch
                {
                    "added" => GameEventKind.Added,
                    "removed" => GameEventKind.Removed,
                    _ => GameEventKind.Updated
                };
                var auxiliary = candidate.Contains(Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe"), StringComparison.OrdinalIgnoreCase);
                // A wrapped PID is not reconstructed/guessed. The authoritative running
                // table, not an uncertain notification, decides game/process lifetime.
                notices.Add(new(start.Index, end - start.Index, new(app, reliable ? pid : 0, kind, auxiliary), reliable));
                found = true;
                break;
            }

            if (!found)
            {
                incomplete = true;
                retain = Math.Min(retain, start.Index);
            }
        }

        foreach (Match failure in Failure.Matches(text))
            if (!notices.Any(n => failure.Index >= n.Start && failure.Index < n.Start + n.Length) && uint.TryParse(failure.Groups[1].Value, out var app) && app > 0)
                notices.Add(new(failure.Index, failure.Length, new(app, 0, GameEventKind.LaunchFailed)));
        var clean = new StringBuilder();
        int at = 0;
        foreach (var notice in notices.OrderBy(n => n.Start))
        {
            clean.Append(text[at..notice.Start]);
            // Once a validated ordinary response has received its trailing blank, the acknowledgement prompt can be
            // immediately followed by a notice. Preserve that prompt, not the notice.
            // Before that blank, a notice's redraw alone must never prove an empty result.
            if (preserveNoticePrompts)
            {
                var prompt = Regex.Match(text[notice.Start..(notice.Start + notice.Length)], @"\A[ \t]*(?:Steam>[ \t]*)+").Value;
                if (prompt.Length > 0)
                    clean.Append(prompt);
            }

            at = notice.Start + notice.Length;
        }

        clean.Append(text[at..]);
        return new(SteamBackgroundOutput.Remove(clean.ToString()), notices.OrderBy(n => n.Start).ToArray(), incomplete, retain);
    }
}
