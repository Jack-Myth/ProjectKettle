using System.Text;

namespace Kettle.Core;

public enum GameEventKind
{
    Added,
    Updated,
    Removed,
    LaunchFailed
}

public sealed record GameRuntimeEvent(uint AppId, int ProcessId, GameEventKind Kind, bool IsAuxiliary = false);
public sealed class GameEventParser(int terminalColumns = ConPtyTerminal.Columns)
{
    private readonly StringBuilder pending = new();
    private readonly TerminalTextNormalizer normalizer = new();
    public GameRuntimeEvent[] Feed(string chunk) => FeedNormalized(normalizer.Feed(chunk));
    internal GameRuntimeEvent[] FeedNormalized(string chunk)
    {
        TerminalTextNormalizer.Append(pending, chunk);
        var parsed = GameNoticeFilter.Process(pending.ToString(), deferTrailingIdentity: true, terminalColumns: terminalColumns);
        foreach (var notice in parsed.Notices.OrderByDescending(n => n.Start))
            pending.Remove(notice.Start, notice.Length);
        var remaining = GameNoticeFilter.Process(pending.ToString(), deferTrailingIdentity: true, terminalColumns: terminalColumns);
        if (remaining.RetainFrom > 0)
            pending.Remove(0, remaining.RetainFrom);
        // Hints have bounded history. Command capture separately retains malformed data.
        if (pending.Length > 16384)
            pending.Clear();
        return parsed.Notices.Where(n => n.IdentityReliable).Select(n => n.Event).ToArray();
    }
}
