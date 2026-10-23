namespace Kettle.Shared;

public enum CloudSupport
{
    None,
    RemoteStorage,
    UnsupportedAutoCloud,
    AutoCloud
}

public sealed record AppLaunchMetadata(CloudSupport Cloud, string? Executable, string? CloudDescription)
{
    public GameLaunchOption[]? LaunchEntries { get; init; }
    public string? AutoCloudAppInfo
    {
        get; init;
    }

}
