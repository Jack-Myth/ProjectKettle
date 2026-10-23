namespace Kettle.Shared;

public sealed record CloudConflictFile(string Name, CloudFile? Local, CloudFile? Cloud);
public sealed record CloudConflictPrompt(string GameName, uint AppId, string Message, string Ticket, CloudConflictFile[] Files, string? PreservedCopies, SaveError? Error = null);
public sealed class CloudConflictException(string message, string ticket, CloudConflictFile[]? files = null, string? preservedCopies = null) : IOException(message)
{
    public string Ticket { get; } = ticket;
    public CloudConflictFile[] Files { get; } = files ?? [];
    public string? PreservedCopies { get; } = preservedCopies;
}
