using System.Diagnostics;
using System.Text.Json;

namespace Kettle.Core;

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

public sealed class CloudHostClient : ICloudRpc
{
    private readonly Process process;
    private readonly SemaphoreSlim requests = new(1, 1);
    private bool unusable, disposed;
    public int ProcessId => process.Id;
    public int? LastExitCode
    {
        get; private set;
    }
    public bool ExitForced
    {
        get; private set;
    }
    public double ShutdownMilliseconds
    {
        get; private set;
    }
    public string RemoteRoot { get; private set; } = "";
    public bool CloudEnabled
    {
        get; private set;
    }

    private CloudHostClient(Process process)
    {
        this.process = process;
    }

    public static async Task<CloudHostClient> ConnectAsync(string executable, uint accountId, uint appId, CancellationToken token)
    {
        var host = Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe");
        if (!File.Exists(host))
            throw new IOException("缺少云同步组件。");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add(appId.ToString());
        start.ArgumentList.Add(Path.GetDirectoryName(executable)!);
        start.ArgumentList.Add(accountId.ToString());
        var client = new CloudHostClient(Process.Start(start) ?? throw new IOException("无法连接云同步组件。"));
        _ = client.process.StandardError.ReadToEndAsync(); // Never persist native stderr or signed URLs.
        try
        {
            var line = await client.process.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(25), token);
            using var json = JsonDocument.Parse(line ?? "{}");
            if (!json.RootElement.TryGetProperty("Ready", out var ready) || !ready.GetBoolean())
                throw new IOException("云同步组件无法验证当前 SteamCMD 账号或接口。");
            client.RemoteRoot = json.RootElement.GetProperty("RemoteRoot").GetString()!;
            client.CloudEnabled = json.RootElement.GetProperty("CloudEnabled").GetBoolean();
            return client;
        }
        catch
        {
            try
            {
                await client.DisposeAsync();
            }
            catch
            { /* preserve the failed initialization reason */
            }

            throw;
        }
    }

    public async Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token)
    {
        await requests.WaitAsync(token);
        try
        {
            if (unusable || disposed)
                throw new IOException("云同步连接已中断，请重新检查未完成批次。");
            token.ThrowIfCancellationRequested();
            await process.StandardInput.WriteLineAsync(KettleJson.Serialize(new CloudHostRequest(method, Convert.ToBase64String(request))));
            await process.StandardInput.FlushAsync();
            // Once issued, drain exactly this response even if the UI cancels. Otherwise a later
            // batch-close request could consume an earlier response and falsely acknowledge it.
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25));
            using var result = JsonDocument.Parse(line ?? "{}");
            if (!result.RootElement.TryGetProperty("Result", out var code) || code.GetInt32() != 1)
                throw new IOException("云端请求未成功：" + method);
            return Convert.FromBase64String(result.RootElement.GetProperty("Payload").GetString()!);
        }
        catch (TimeoutException)
        {
            unusable = true;
            throw new IOException("云端应答超时，结果未确认；连接已停止复用。");
        }
        catch (System.Text.Json.JsonException)
        {
            unusable = true;
            throw new IOException("云同步应答无效，结果未确认。");
        }
        finally
        {
            requests.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        var shutdown = Stopwatch.StartNew();
        try
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("quit");
                    process.StandardInput.Close();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
                }
            }
            catch
            {
                if (!process.HasExited)
                {
                    ExitForced = true;
                    process.Kill(true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
                }
            }

            LastExitCode = process.HasExited ? process.ExitCode : null;
            if (ExitForced || LastExitCode != 0)
                throw new IOException("云辅助进程未正常结束，请保留本地存档并重新检查；未确认同步完成。");
        }
        finally
        {
            ShutdownMilliseconds = shutdown.Elapsed.TotalMilliseconds;
            process.Dispose();
        }
    }
}
