
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
internal static class CommandOutputText
{
    public static string Limit(string text) => text.Length <= 8192 ? text : text[^8192..];
}

// Bounded post-login raw samples, including terminal control sequences.
internal sealed class TerminalLayoutCapture
{
    private readonly Queue<TerminalChunkLayout> chunks = new();
    public long Count { get; private set; }
    public TerminalChunkLayout[] Snapshot() => chunks.ToArray();
    public void Append(string text)
    {
        long start = Count;
        Count += text.Length;
        chunks.Enqueue(new(start, text.Length, text.Length <= 512 ? text : text[^512..], text.Length > 512));
        while (chunks.Count > 16)
            chunks.Dequeue();
    }
}