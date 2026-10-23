using System.IO.Compression;
using System.Net;
using Kettle.Cloud;

internal static class StreamingSaveChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(root, "streaming");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "large.dat");
        const int size = 32 * 1024 * 1024;
        var buffer = new byte[64 * 1024];
        new Random(42).NextBytes(buffer);
        using (var output = File.Create(source))
            for (int n = 0; n < size; n += buffer.Length) output.Write(buffer);
        var expected = await AutoCloudDisk.Digest(source, default);
        var zipPath = Path.Combine(folder, "wire.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(source, "save.dat");
        // Warm pooled buffers and crypto before measuring cumulative managed allocation.
        await AutoCloudDisk.Digest(source, default);
        long before = GC.GetTotalAllocatedBytes(true);
        var backup = await SaveBackup.CreateAsync(folder, Path.Combine(root, "streaming-backups"), default);
        var staged = await CloudUpload.Stage("save.dat", source, expected.Sha1, 123, null, uint.MaxValue, Path.Combine(root, "streaming-upload"), default);
        using (var input = AutoCloudDisk.OpenRead(staged.SourcePath!))
        using (var block = new SaveBlockContent(input.Stream, buffer.Length, size - buffer.Length))
        {
            check(block.Headers.ContentLength == size - buffer.Length, "streamed upload advertises the exact block length");
            await block.CopyToAsync(Stream.Null);
            input.Verify();
        }
        var file = new CloudFile("save.dat", expected.Sha1, size, 123);
        using var transport = new DownloadFixture(source, file);
        using var http = new HttpClient(transport);
        var transfer = new CloudTransfer(42, transport, http);
        await SaveStreams.Download(transfer, file, Path.Combine(root, "streamed-raw.dat"), default);
        transport.Wire = zipPath;
        await SaveStreams.Download(transfer, file, Path.Combine(root, "streamed-zip.dat"), default);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Console.WriteLine($"Streaming 32 MiB save: backup, staging, upload block, raw and ZIP download allocated {allocated / 1024.0 / 1024:F2} MiB managed memory.");
        check(allocated < 16 * 1024 * 1024, "large save processing does not allocate a full payload or cumulative copies");
        check(SaveStreams.HashFile(Path.Combine(root, "streamed-zip.dat")) == expected.Sha1 && File.Exists(Path.Combine(backup, "manifest.json")), "large compressed download and backup preserve verified bytes");
        var wrong = file with { Sha1 = new string('0', 40) };
        transport.File = wrong;
        bool rejected = false;
        try { await SaveStreams.Download(transfer, wrong, Path.Combine(root, "corrupt-stream.dat"), default); }
        catch (IOException) { rejected = true; }
        check(rejected, "streamed decompression rejects a mismatched final hash");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        rejected = false;
        try { await AutoCloudDisk.Copy(source, Path.Combine(root, "cancelled-stream.dat"), cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected, "streamed file copying observes cancellation");
        var destination = Path.Combine(root, "unchanged.dat");
        File.WriteAllText(destination, "original");
        rejected = false;
        try { await AutoCloudDisk.Write(destination, source, wrong.Sha1, 123, default); }
        catch (IOException) { rejected = true; }
        check(rejected && File.ReadAllText(destination) == "original", "staged hash failure cannot replace a game-visible save");
    }

    private sealed class DownloadFixture(string wire, CloudFile file) : HttpMessageHandler, ICloudRpc
    {
        public string Wire = wire;
        public CloudFile File = file;
        public string RemoteRoot => "unused";
        public bool CloudEnabled => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token)
        {
            if (method != "Cloud.ClientFileDownload#1") throw new InvalidOperationException(method);
            return Task.FromResult(CloudWire.Message((1, 42u), (2, (ulong)new FileInfo(Wire).Length), (3, (ulong)File.Size),
                (4, Convert.FromHexString(File.Sha1)), (7, "fixture.example"), (8, "/save"), (9, true)));
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(System.IO.File.OpenRead(Wire)) });
    }
}
