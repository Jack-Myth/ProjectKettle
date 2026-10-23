using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Kettle.Core;

// The native process, not this transport, owns ConPTY and the SteamCMD process tree.
public sealed class CoreSessionTerminal(string? existingToken = null) : ISteamTerminal
{
    public string Token { get; } = existingToken ?? Guid.NewGuid().ToString("N");
    private NamedPipeClientStream? raw;
    private readonly SemaphoreSlim writes = new(1, 1);
    private bool handedOff;
    private readonly object stopLock = new();
    private Task? stopping;
    public bool SupportsPromptBoundaries => true;
    public async Task StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (raw is not null) throw new InvalidOperationException("Already connected.");
        if (existingToken is null)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Kettle.CoreSession.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var value in new[] { "--broker", Token, Path.GetFullPath(executable), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) }) start.ArgumentList.Add(value);
            start.ArgumentList.Add("--language");
            start.ArgumentList.Add(L.Language);
            using var process = Process.Start(start) ?? throw new IOException("Cannot start native assistant.");
        }
        else
        {
            if (!Guid.TryParseExact(Token, "N", out _)) throw new IOException("Invalid assistant identity.");
            await ControlAsync("ATTACH", token);
        }
        raw = new NamedPipeClientStream(".", "Kettle.CoreSession." + Token + ".raw", PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(15000, token);
    }
    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken token)
    {
        var bytes = new byte[4096]; var chars = new char[4096]; var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (raw is not null)
            {
                var size = await raw.ReadAsync(bytes, token); if (size == 0) break;
                var count = decoder.GetChars(bytes, 0, size, chars, 0, false);
                yield return new string(chars, 0, count);
                Array.Clear(bytes); Array.Clear(chars);
            }
        }
        finally { Array.Clear(bytes); Array.Clear(chars); }
    }
    public async Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
    {
        await writes.WaitAsync(token); byte[]? bytes = null;
        try
        {
            if (handedOff || raw is null) throw new IOException("Native assistant owns this session.");
            bytes = new byte[Encoding.UTF8.GetByteCount(line.Span) + 1];
            Encoding.UTF8.GetBytes(line.Span, bytes); bytes[^1] = 13;
            await raw.WriteAsync(bytes, token); await raw.FlushAsync(token);
        }
        finally { if (bytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); writes.Release(); }
    }
    public async Task<string> ControlAsync(string command, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", "Kettle.CoreSession." + Token + ".control", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), deadline.Token).ConfigureAwait(false);
        var response = new StringBuilder(); var buffer = new byte[4096];
        var complete = false;
        while (response.Length < 262144)
        {
            var count = await pipe.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (count == 0) break;
            var newline = Array.IndexOf(buffer, (byte)10, 0, count);
            response.Append(Encoding.UTF8.GetString(buffer, 0, newline < 0 ? count : newline));
            if (newline >= 0) { complete = true; break; }
        }
        if (!complete) throw new IOException("Incomplete CoreSession response.");
        var result = response.ToString();
        if (result.StartsWith("ERROR ", StringComparison.Ordinal)) throw new IOException(result);
        return result;
    }
    public SessionProcessSafety InspectProcesses()
    {
        try { return new(ControlAsync("SAFETY", CancellationToken.None).GetAwaiter().GetResult() == "OK", "Native assistant controls the SteamCMD session."); }
        catch { return new(false, "Cannot confirm native assistant state. Keep the session open."); }
    }
    public async Task HandoffAsync(CoreSessionJob job, CancellationToken token)
    {
        var directory = Path.Combine(job.AppDirectory, "data", "assistant", job.RunId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "job.json"), JsonSerializer.Serialize(job, CoreSessionJson.Default.CoreSessionJob), token);
        string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        await File.WriteAllTextAsync(Path.Combine(directory, "job.ini"), "[job]\r\nAppId=" + job.Context.AppId +
            "\r\nName=" + Clean(job.GameName) + "\r\nLaunchIndex=" + job.LaunchOptionIndex + "\r\nToken=" + Token + "\r\n", Encoding.Unicode, token);
        // No further frontend commands may be submitted once the transfer begins.
        handedOff = true;
        try { await ControlAsync("RUN " + Path.GetFullPath(directory), token); }
        catch
        {
            // Ambiguous acknowledgement: never fall back to launching in the frontend.
            throw new IOException("Assistant handoff is unconfirmed. Check its window; do not launch again.");
        }
    }
    public Task StopAsync()
    {
        lock (stopLock)
            return stopping ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        if (handedOff || raw is null) return;
        await ControlAsync("CLOSE", CancellationToken.None).ConfigureAwait(false);
        var connection = raw;
        raw = null;
        await connection.DisposeAsync().ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
