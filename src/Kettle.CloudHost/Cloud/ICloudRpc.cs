namespace Kettle.Cloud;
public interface ICloudRpc : IAsyncDisposable
{
    string RemoteRoot
    {
        get;
    }

    bool CloudEnabled
    {
        get;
    }

    Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token);
}

