using System.Diagnostics;
using Kettle.Shared;
using Kettle.Cloud;
using Steamworks;

// The full sync worker uses Steam directly, without recursively spawning another managed host.
internal sealed class NativeCloudRpc : ICloudRpc
{
    private readonly Action verify;
    private bool initialized;
    private bool disposed;
    public string RemoteRoot { get; }
    public bool CloudEnabled { get; }
    public NativeCloudRpc(CloudContext context, Action verify)
    {
        this.verify = verify;
        try
        {
            Environment.SetEnvironmentVariable("SteamAppID", context.AppId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", context.AppId.ToString());
            var root = Path.GetDirectoryName(Path.GetFullPath(context.SteamCmd))!;
            initialized = SteamAPI.Init();
            if (!initialized || !SteamUser.BLoggedOn() || (uint)SteamUser.GetSteamID().GetAccountID() != context.AccountId)
                throw new IOException("Steam context/account mismatch.");
            var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().First(m => m.ModuleName.Equals("steamclient.dll", StringComparison.OrdinalIgnoreCase));
            if (!Path.GetFullPath(module.FileName).Equals(Path.Combine(root, "steamclient.dll"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Wrong Steam client context.");
            var expected = Path.Combine(root, "userdata", context.AccountId.ToString(), context.AppId.ToString());
            if (!SteamUser.GetUserDataFolder(out var folder, 4096) ||
                !Path.GetFullPath(folder).StartsWith(expected + "\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected application data folder.");
            if (SteamClient.GetISteamUnifiedMessages(SteamAPI.GetHSteamUser(), SteamAPI.GetHSteamPipe(), "STEAMUNIFIEDMESSAGES_INTERFACE_VERSION001") == IntPtr.Zero)
                throw new IOException("Unified Messages unavailable.");
            RemoteRoot = Path.Combine(expected, "remote");
            CloudEnabled = SteamRemoteStorage.IsCloudEnabledForAccount() && SteamRemoteStorage.IsCloudEnabledForApp();
        }
        catch { if (initialized) SteamAPI.Shutdown(); initialized = false; throw; }
    }
    public Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token)
    {
        verify();
        token.ThrowIfCancellationRequested();
        if (disposed || request.Length > 4 * 1024 * 1024 || method is not (
            "Cloud.GetAppFileChangelist#1" or "Cloud.GetSingleFileInfo#1" or "Cloud.ClientFileDownload#1" or
            "Cloud.ClientDeleteFile#1" or "Cloud.BeginAppUploadBatch#1" or "Cloud.ClientBeginFileUpload#1" or
            "Cloud.ClientCommitFileUpload#1" or "Cloud.CompleteAppUploadBatchBlocking#1"))
            throw new IOException("Invalid cloud request.");
        var handle = SteamUnifiedMessages.SendMethod(method, request, (uint)request.Length, 0);
        if (handle.Equals(default(ClientUnifiedMessageHandle))) throw new IOException("RPC unavailable.");
        try
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                SteamAPI.RunCallbacks();
                if (SteamUnifiedMessages.GetMethodResponseInfo(handle, out var size, out var result))
                {
                    if (result != EResult.k_EResultOK || size > 8 * 1024 * 1024)
                        throw new IOException($"Cloud request failed: {method}: {result}");
                    var data = new byte[size];
                    if (size != 0 && !SteamUnifiedMessages.GetMethodResponseData(handle, data, size, false))
                        throw new IOException("RPC read failed.");
                    verify();
                    return Task.FromResult(data);
                }
                Thread.Sleep(20);
            }
            throw new IOException("Cloud request timed out; result is unconfirmed.");
        }
        finally { SteamUnifiedMessages.ReleaseMethod(handle); }
    }
    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            if (initialized) SteamAPI.Shutdown();
            verify();
        }
        return ValueTask.CompletedTask;
    }
}
