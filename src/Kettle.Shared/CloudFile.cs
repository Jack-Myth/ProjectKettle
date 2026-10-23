namespace Kettle.Shared;

public sealed record CloudFile(string Name, string Sha1, long Size, long Timestamp, uint Platforms = uint.MaxValue)
{
    public bool ReuploadRequested { get; init; }
}
