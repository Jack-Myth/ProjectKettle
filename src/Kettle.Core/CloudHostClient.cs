using System.Diagnostics;
using System.Text.Json;
using System.Text;

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
    private readonly StringBuilder stderrTail = new();
    private Task stderrDrain = Task.CompletedTask;
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
            throw new IOException(L.Text(TextId.TheCloudSyncComponentIsMissing));
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
        return await StartAsync(start, token);
    }

    internal static async Task<CloudHostClient> StartAsync(ProcessStartInfo start, CancellationToken token)
    {
        DebugTrace.Write("cloudhost Process.Start begin");
        var client = new CloudHostClient(Process.Start(start) ?? throw new IOException(L.Text(TextId.CouldNotConnectToTheCloudSyncComponent)));
        DebugTrace.Write("cloudhost Process.Start complete pid=" + client.ProcessId);
        client.RecordDiagnostic("start", reset: true);
        client.stderrDrain = client.DrainStderr();
        try
        {
            var line = await client.process.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(25), token);
            using var json = JsonDocument.Parse(line ?? "{}");
            if (!json.RootElement.TryGetProperty("Ready", out var ready) || !ready.GetBoolean())
                throw new IOException(L.Text(TextId.TheCloudSyncComponentCouldNotVerifyTheCurrent) + (json.RootElement.TryGetProperty("Error", out var detail) ? " " + detail.GetString() : ""));
            client.RemoteRoot = json.RootElement.GetProperty("RemoteRoot").GetString()!;
            client.CloudEnabled = json.RootElement.GetProperty("CloudEnabled").GetBoolean();
            DebugTrace.Write("cloudhost handshake complete");
            return client;
        }
        catch (Exception error)
        {
            client.RecordDiagnostic("connect", error);
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

    private async Task DrainStderr()
    {
        var buffer = new char[2048];
        try
        {
            int length;
            while ((length = await process.StandardError.ReadAsync(buffer)) > 0)
            {
                lock (stderrTail)
                {
                    stderrTail.Append(buffer, 0, length);
                    if (stderrTail.Length > 8192)
                        stderrTail.Remove(0, stderrTail.Length - 8192);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void RecordDiagnostic(string stage, Exception? error = null, bool reset = false)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "data", "diagnostics");
            Directory.CreateDirectory(directory);
            string stderr;
            lock (stderrTail) stderr = stderrTail.ToString();
            var path = Path.Combine(directory, "cloudhost-client-latest.log");
#if DEBUG
            if (reset && File.Exists(path))
                File.Move(path, Path.Combine(directory, "cloudhost-client-previous.log"), true);
#endif
            if (reset || File.Exists(path) && new FileInfo(path).Length > 16 * 1024)
                File.WriteAllText(path, reset ? "" : "Trace truncated.\n");
            File.AppendAllText(path, $"{DateTime.UtcNow:O} {stage} pid={process.Id} exit={LastExitCode} forced={ExitForced}\n{error}\nstderr tail:\n{stderr}\n");
        }
        catch { /* Keep the original operation result if diagnostics cannot be written. */ }
    }

    public async Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token)
    {
        await requests.WaitAsync(token);
        try
        {
            if (unusable || disposed)
                throw new IOException(L.Text(TextId.CloudSyncDisconnectedRecheckPendingBatches));
            DebugTrace.Write("cloudhost request begin " + method);
            token.ThrowIfCancellationRequested();
            await process.StandardInput.WriteLineAsync(KettleJson.Serialize(new CloudHostRequest(method, Convert.ToBase64String(request))));
            await process.StandardInput.FlushAsync();
            // Once issued, drain exactly this response even if the UI cancels. Otherwise a later
            // batch-close request could consume an earlier response and falsely acknowledge it.
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25));
            if (line is null)
            {
                unusable = true;
                var error = new IOException(L.Text(TextId.CloudSyncDisconnectedRecheckPendingBatches) + " " + method + " (EOF)");
                RecordDiagnostic("request " + method, error);
                throw error;
            }
            DebugTrace.Write($"cloudhost response received method={method} chars={line.Length}");
            using var result = JsonDocument.Parse(line);
            if (!result.RootElement.TryGetProperty("Result", out var code) || code.GetInt32() != 1)
            {
                var detail = result.RootElement.TryGetProperty("Error", out var hostError) ? "Error=" + hostError.GetString()
                    : code.ValueKind == JsonValueKind.Number ? "Result=" + code.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "Result missing";
                if (result.RootElement.TryGetProperty("ResultName", out var name) && name.ValueKind == JsonValueKind.String)
                    detail += " (" + name.GetString() + ")";
                if (detail.Length > 2048) detail = detail[..2048];
                var error = new IOException(L.Text(TextId.CloudRequestFailed) + method + " — " + detail);
                RecordDiagnostic("request " + method, error);
                throw error;
            }
            return Convert.FromBase64String(result.RootElement.GetProperty("Payload").GetString()!);
        }
        catch (TimeoutException)
        {
            unusable = true;
            throw new IOException(L.Text(TextId.CloudResponseTimedOutTheResultIsUnconfirmedThe));
        }
        catch (System.Text.Json.JsonException)
        {
            unusable = true;
            throw new IOException(L.Text(TextId.InvalidCloudSyncResponseTheResultIsUnconfirmed));
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
                throw new IOException(L.Text(TextId.TheCloudHelperDidNotExitNormallyPreserveLocal));
        }
        finally
        {
            ShutdownMilliseconds = shutdown.Elapsed.TotalMilliseconds;
            try { await stderrDrain.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { }
            RecordDiagnostic("shutdown");
            DebugTrace.Write($"cloudhost shutdown exit={LastExitCode} forced={ExitForced}");
            process.Dispose();
        }
    }
}
