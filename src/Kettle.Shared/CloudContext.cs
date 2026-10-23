namespace Kettle.Shared;

public enum ConflictChoice
{
    None,
    Local,
    Cloud
}

public sealed record CloudContext(uint AppId, uint AccountId, string SteamCmd, AppLaunchMetadata Metadata)
{
    public string? InstallPath
    {
        get; init;
    }
}

public sealed record CloudRunSnapshot(Dictionary<string, string> Local, Dictionary<string, string> Cloud);
public sealed record CloudPreparation(CloudContext Context, string? RemoteRoot, bool Applicable)
{
    public CloudRunSnapshot? Snapshot { get; init; }
}
