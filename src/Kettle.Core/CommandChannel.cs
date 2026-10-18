namespace Kettle.Core;

public enum CommandChannelState
{
    Ready,
    WaitingForLateResponse,
    ReconnectRequired
}

public sealed record SteamCommandBoundaryTrace(string Command, string Stage, DateTime Utc, string ResponseTail)
{
    public SteamCommandOutputDiagnostic? Output
    {
        get; init;
    }
}

public sealed record CommandChannelStatus(CommandChannelState State, string Message, string[] Commands, DateTime UpdatedUtc)
{
    public string? AppsRunningResponseTail
    {
        get; init;
    }
    public string? LaunchResponseTail
    {
        get; init;
    }
    public bool CanRead => State == CommandChannelState.Ready;
    public bool NeedsReconnect => State == CommandChannelState.ReconnectRequired;

    public static CommandChannelStatus Ready() => new(CommandChannelState.Ready, "", [], DateTime.UtcNow);
}

public sealed class SteamCommandChannelException(CommandChannelStatus status) : InvalidOperationException(status.Message)
{
    public CommandChannelStatus Status { get; } = status;
}
