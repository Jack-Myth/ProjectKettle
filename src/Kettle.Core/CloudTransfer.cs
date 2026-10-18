using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace Kettle.Core;

public sealed record CloudFile(string Name, string Sha1, long Size, long Timestamp, uint Platforms = uint.MaxValue)
{
    public bool ReuploadRequested { get; init; }
}
public sealed record CloudSnapshot(ulong ChangeNumber, CloudFile[] Files)
{
    public string[] DeletedNames { get; init; } = [];
}
public sealed record CloudUpload(string Name, byte[] Data, long Timestamp, string? ExpectedRemoteSha1, uint Platforms = uint.MaxValue)
{
    public string? SourcePath { get; init; }
    public string? SourceSha1 { get; init; }
    public long Size => SourcePath is null ? Data.LongLength : new FileInfo(SourcePath).Length;
    public string Hash => SourcePath is null ? CloudTransfer.Hash(Data) : SourceSha1 ?? throw new InvalidDataException("Missing staged hash.");
    public static async Task<CloudUpload> Stage(CloudUpload upload, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllBytesAsync(path, upload.Data, token);
        return upload with { SourcePath = path, SourceSha1 = CloudTransfer.Hash(upload.Data), Data = [] };
    }
}
public sealed record CloudDelete(string Name, string ExpectedRemoteSha1);
public interface ICloudFiles
{
    Task<CloudSnapshot> ListAsync(CancellationToken token);
    Task<byte[]> DownloadAsync(CloudFile file, CancellationToken token);
    Task UploadAsync(IReadOnlyList<CloudUpload> files, Func<ulong, Task> saveBatch, CancellationToken token);
    Task CloseBatchAsync(ulong batch, bool success, CancellationToken token);
    Task DeleteAsync(IReadOnlyList<CloudDelete> files, Func<ulong, Task> saveBatch, CancellationToken token) => files.Count == 0 ? Task.CompletedTask : Task.FromException(new NotSupportedException("Cloud deletion is not supported by this provider."));
}

public sealed class CloudTransfer(uint appId, ICloudRpc rpc, HttpClient? client = null, AutoCloudPathPlan? autoCloud = null) : ICloudFiles
{
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly HttpClient http = client ?? SharedHttp;
    private CloudSnapshot? snapshot;
    private readonly SemaphoreSlim listGate = new(1, 1);
    public const int MaxFileBytes = 64 * 1024 * 1024;
    private async Task<ProtoField[]> Call(string method, byte[] bytes, CancellationToken token) => CloudWire.Parse(await rpc.CallAsync("Cloud." + method + "#1", bytes, token));
    public async Task<CloudSnapshot> ListAsync(CancellationToken token)
    {
        await listGate.WaitAsync(token);
        try { return await ReadList(token); }
        finally { listGate.Release(); }
    }

    private async Task<CloudSnapshot> ReadList(CancellationToken token)
    {
        var data = await Call("GetAppFileChangelist", CloudWire.Message((1, appId), (2, snapshot?.ChangeNumber ?? 0ul)), token);
        if (data.Number(3) != 0 && snapshot is null)
            throw new InvalidDataException(L.Text(TextId.TheServerReturnedOnlyADeltaWithoutAFull));
        if (snapshot is not null && data.Number(1) < snapshot.ChangeNumber)
            throw new InvalidDataException(L.Text(TextId.TheCloudDirectoryVersionMovedBackwardsPleaseRetrySyncing));
        var prefixes = data.Where(f => f.Number == 4).Select(f => System.Text.Encoding.UTF8.GetString(f.Data!)).ToArray();
        var files = data.Number(3) != 0 ? snapshot!.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, CloudFile>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = data.Number(3) != 0 ? snapshot!.DeletedNames.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in data.Where(f => f.Number == 2))
        {
            var f = CloudWire.Parse(field.Data!);
            var name = f.Text(1);
            var index = f.Number(7);
            if (prefixes.Length > 0)
                name = (index < (ulong)prefixes.Length ? prefixes[(int)index] : throw new InvalidDataException(L.Text(TextId.CloudPathPrefixIndexIsOutOfBounds))) + name;
            else if (index != 0)
                throw new InvalidDataException(L.Text(TextId.CloudPathPrefixIndexIsOutOfBounds));
            if (f.Any(x => x.Number == 6) && (f.Number(6) & 1) == 0)
            {
                files.Remove(name);
                deleted.Remove(name);
                continue;
            }
            if (autoCloud is null)
                ValidateName(name);
            else
                _ = autoCloud.ToLocal(AutoCloudWire.Key(name));
            if (!seen.Add(name)) throw new InvalidDataException(L.Text(TextId.DuplicateCloudFilenames));
            if (f.Number(5) is 1 or 2)
            {
                files.Remove(name); // Forgotten/deleted entries have no downloadable contents.
                if (f.Number(5) == 2) deleted.Add(name); else deleted.Remove(name);
                continue;
            }
            if (f.Number(5) != 0 || f.Bytes(2).Length != 20 || f.Number(4) > MaxFileBytes)
                throw new NotSupportedException(L.Text(TextId.TheCloudFileStateOrSizeIsUnsupported));
            files[name] = new(name, Convert.ToHexString(f.Bytes(2)), checked((long)f.Number(4)), checked((long)f.Number(3)), f.Any(x => x.Number == 6) ? checked((uint)f.Number(6)) : uint.MaxValue) { ReuploadRequested = f.Number(9) != 0 };
            deleted.Remove(name);
        }

        if (files.Count > 4096 || files.Values.Sum(f => f.Size) > 4L * 1024 * 1024 * 1024)
            throw new NotSupportedException(L.Text(TextId.CloudSaveDirectoryExceedsSupportedLimits));
        if (autoCloud is null)
            foreach (var name in files.Keys)
                for (int slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                    if (files.ContainsKey(name[..slash]))
                        throw new InvalidDataException(L.Text(TextId.CloudFileAndDirectoryPathsConflict));
        if (autoCloud is not null)
            autoCloud.MapAll(files.Values.Select(f => AutoCloudWire.Key(f.Name)));
        return snapshot = new(data.Number(1), files.Values.ToArray()) { DeletedNames = deleted.ToArray() };
    }

    public async Task<byte[]> DownloadAsync(CloudFile file, CancellationToken token)
    {
        if (autoCloud is not null)
            _ = autoCloud.ToLocal(AutoCloudWire.Key(file.Name));
        var meta = await Call("ClientFileDownload", CloudWire.Message((1, appId), (2, file.Name)), token);
        if (meta.Number(11) != 0)
            throw new NotSupportedException(L.Text(TextId.ThisCloudSaveIsEncryptedAndAPlaintextCopy));
        if (meta.Number(1) != appId || meta.Number(6) != 0 || meta.Number(9) == 0 || meta.Number(3) != (ulong)file.Size || !meta.Bytes(4).SequenceEqual(Convert.FromHexString(file.Sha1)))
            throw new IOException(L.Text(TextId.CloudDownloadVersionOrTransferParametersChangedPleaseRetry));
        using var request = new HttpRequestMessage(HttpMethod.Get, Address(meta.Text(7), meta.Text(8), true));
        AddHeaders(request, meta.Where(f => f.Number == 10));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var wire = await ReadBounded(response.Content, MaxFileBytes, token);
        if ((ulong)wire.Length != meta.Number(2))
            throw new IOException(L.Text(TextId.CloudDownloadLengthMismatch));
        byte[] raw;
        if (wire.Length >= 4 && wire[0] == 'P' && wire[1] == 'K' && wire[2] == 3 && wire[3] == 4)
        {
            using var zip = new ZipArchive(new MemoryStream(wire));
            if (zip.Entries.Count != 1 || zip.Entries[0].Length != file.Size)
                throw new InvalidDataException(L.Text(TextId.UnsupportedCloudSaveCompressionFormat));
            using var stream = zip.Entries[0].Open();
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, token);
            raw = output.ToArray();
        }
        else
            raw = wire;
        if (raw.LongLength != file.Size || Hash(raw) != file.Sha1)
            throw new IOException(L.Text(TextId.DownloadedSaveVerificationFailed));
        return raw;
    }

    public async Task UploadAsync(IReadOnlyList<CloudUpload> files, Func<ulong, Task> saveBatch, CancellationToken token)
    {
        if (files.Count == 0)
            return;
        if (files.Count > 4096 || files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count || files.Sum(f => f.Size) > 4L * 1024 * 1024 * 1024)
            throw new NotSupportedException(L.Text(TextId.UploadFilesAreDuplicatedOrExceedSupportedLimits));
        foreach (var file in files)
        {
            if (autoCloud is null)
                ValidateName(file.Name);
            else
                _ = autoCloud.ToLocal(AutoCloudWire.Key(file.Name));
            if (file.Size > MaxFileBytes || file.Timestamp < 0 || (file.Platforms & 1) == 0)
                throw new NotSupportedException(L.Text(TextId.UnsupportedUploadStateOrPlatform));
        }

        // Fresh server read reduces conflicts, but this protocol has NO atomic compare-and-swap.
        await CheckExpected(files, token);
        var fields = new List<(int, object)>
        {
            (1, appId),
            (2, Environment.MachineName)
        };
        fields.AddRange(files.Select(f => (3, (object)f.Name)));
        ulong batch = 0;
        bool committed = false;
        try
        {
            var begin = await Call("BeginAppUploadBatch", CloudWire.Message(fields.ToArray()), token);
            batch = begin.Number(1);
            if (batch == 0)
                throw new IOException(L.Text(TextId.TheCloudUploadBatchWasNotEstablished));
            await saveBatch(batch);
            token.ThrowIfCancellationRequested();
            await CheckExpected(files, token);
            foreach (var source in files)
            {
                var file = source.SourcePath is null ? source : source with { Data = await AutoCloudDisk.Read(source.SourcePath, token), SourcePath = null };
                if (file.Hash != source.Hash) throw new IOException(L.Text(TextId.SavesChangedWhilePreparingTheUpload));
                token.ThrowIfCancellationRequested();
                if (autoCloud is null)
                    ValidateName(file.Name);
                else
                    _ = autoCloud.ToLocal(AutoCloudWire.Key(file.Name));
                if (file.Data.Length > MaxFileBytes)
                    throw new NotSupportedException(L.Text(TextId.TheSaveExceedsTheUploadSizeLimit));
                await CheckExpected([file], token);
                var sha = SHA1.HashData(file.Data);
                bool fileCommitted = false, started = false;
                try
                {
                    var upload = await Call("ClientBeginFileUpload", CloudWire.Message((1, appId), (2, (uint)file.Data.Length), (3, (uint)file.Data.Length), (4, sha), (5, (ulong)file.Timestamp), (6, file.Name), (7, file.Platforms), (10, false), (13, batch)), token);
                    started = true;
                    if (upload.Number(1) != 0)
                        throw new NotSupportedException(L.Text(TextId.TheServerRequiresEncryptedUploadsWhichAreNotSupported));
                    var blocks = upload.Where(f => f.Number == 2).ToArray();
                    if (blocks.Length == 0)
                        throw new IOException(L.Text(TextId.TheServerDidNotReturnUploadChunks));
                    foreach (var block in blocks)
                    {
                        var b = CloudWire.Parse(block.Data!);
                        var method = b.Number(4) switch
                        {
                            3 => HttpMethod.Post,
                            4 => HttpMethod.Put,
                            _ => throw new NotSupportedException(L.Text(TextId.TheUploadMethodRequiredByTheServerIsNot))
                        };
                        var body = b.Bytes(8);
                        if (!b.Any(f => f.Number == 8))
                        {
                            var offset = checked((int)b.Number(6));
                            var length = checked((int)b.Number(7));
                            if (offset < 0 || length < 0 || offset > file.Data.Length - length)
                                throw new InvalidDataException(L.Text(TextId.UploadChunkIsOutOfBounds));
                            body = file.Data.AsSpan(offset, length).ToArray();
                        }

                        using var request = new HttpRequestMessage(method, Address(b.Text(1), b.Text(2), b.Number(3) != 0))
                        {
                            Content = new ByteArrayContent(body)
                        };
                        AddHeaders(request, b.Where(f => f.Number == 5));
                        using var response = await http.SendAsync(request, token);
                        response.EnsureSuccessStatusCode();
                    }

                    var commit = await Call("ClientCommitFileUpload", CloudWire.Message((1, true), (2, appId), (3, sha), (4, file.Name)), CancellationToken.None);
                    if (commit.Number(1) != 1)
                        throw new IOException(L.Text(TextId.TheCloudDidNotConfirmTheFileCommit));
                    fileCommitted = true;
                    var info = await Call("GetSingleFileInfo", CloudWire.Message((1, appId), (2, file.Name)), token);
                    if (!info.Bytes(4).SequenceEqual(sha) || info.Number(6) != (ulong)file.Data.Length || info.Number(7) != 0 || autoCloud is not null && (info.Number(2) != appId || info.Text(3) != file.Name))
                        throw new IOException(L.Text(TextId.FileIdentityOrHashHasNotBeenConfirmedAfter));
                }
                finally
                {
                    if (started && !fileCommitted)
                    {
                        try
                        {
                            await Call("ClientCommitFileUpload", CloudWire.Message((1, false), (2, appId), (3, sha), (4, file.Name)), CancellationToken.None);
                        }
                        catch
                        { /* original failure remains, batch still closed below */
                        }
                    }
                }
            }

            committed = true;
        }
        finally
        {
            if (batch != 0)
            {
                await CloseBatchAsync(batch, committed, CancellationToken.None);
                await saveBatch(0);
            }
        }

        var verify = await ListAsync(token);
        if (files.Any(file => verify.Files.FirstOrDefault(f => f.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase))?.Sha1 != file.Hash))
            throw new IOException(L.Text(TextId.TheNewCloudListingDidNotConfirmTheUpload));
    }

    private async Task CheckExpected(IReadOnlyList<CloudUpload> files, CancellationToken token)
    {
        var snapshot = await ListAsync(token);
        foreach (var file in files)
        {
            var current = snapshot.Files.FirstOrDefault(f => f.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase));
            if (current?.Sha1 != file.ExpectedRemoteSha1 || current is not null && (current.Platforms != file.Platforms || current.Name != file.Name))
                throw new CloudConflictException(L.Text(TextId.CloudVersionsFilenamesOrPlatformsChangedBeforeUploadOverwrite), "");
        }
    }

    public async Task CloseBatchAsync(ulong batch, bool success, CancellationToken token)
    {
        await Call("CompleteAppUploadBatchBlocking", CloudWire.Message((1, appId), (2, batch), (3, success ? 1u : 2u)), token);
    }

    public async Task DeleteAsync(IReadOnlyList<CloudDelete> files, Func<ulong, Task> saveBatch, CancellationToken token)
    {
        if (files.Count == 0) return;
        if (files.Count > 4096 || files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("Invalid cloud deletion batch.");
        foreach (var file in files)
            if (autoCloud is null) ValidateName(file.Name); else _ = autoCloud.ToLocal(AutoCloudWire.Key(file.Name));
        async Task Verify(IEnumerable<CloudDelete> expected)
        {
            var current = await ListAsync(token);
            if (expected.Any(f => current.Files.SingleOrDefault(c => c.Name == f.Name)?.Sha1 != f.ExpectedRemoteSha1))
                throw new CloudConflictException(L.Text(TextId.CloudVersionsFilenamesOrPlatformsChangedBeforeUploadOverwrite), "");
        }
        await Verify(files);
        var fields = new List<(int, object)> { (1, appId), (2, Environment.MachineName) };
        fields.AddRange(files.Select(f => (4, (object)f.Name)));
        ulong batch = 0;
        bool success = false;
        try
        {
            batch = (await Call("BeginAppUploadBatch", CloudWire.Message(fields.ToArray()), token)).Number(1);
            if (batch == 0) throw new IOException(L.Text(TextId.TheCloudUploadBatchWasNotEstablished));
            await saveBatch(batch);
            foreach (var file in files)
            {
                await Verify([file]);
                await Call("ClientDeleteFile", CloudWire.Message((1, appId), (2, file.Name), (3, true), (4, batch)), token);
            }
            success = true;
        }
        finally
        {
            if (batch != 0)
            {
                await CloseBatchAsync(batch, success, CancellationToken.None);
                await saveBatch(0);
            }
        }
        var final = await ListAsync(token);
        if (files.Any(f => final.Files.Any(c => c.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))))
            throw new IOException(L.Text(TextId.CloudDeletionHasNotBeenConfirmedPleaseRetrySyncing));
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data));
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' ') || part.Length > 240 || System.Text.RegularExpressions.Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new NotSupportedException(L.Text(TextId.ACloudSaveContainsAnUnsafeRelativePath));
    }

    private static Uri Address(string host, string path, bool https)
    {
        if (!https || string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains('@') || !path.StartsWith('/'))
            throw new NotSupportedException(L.Text(TextId.CloudTransferAddressIsUnsafeOrDoesNotUse));
        var uri = new Uri("https://" + host + path);
        if (IPAddress.TryParse(uri.Host, out _) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(L.Text(TextId.UnsupportedCloudTransferAddress));
        return uri;
    }

    private static void AddHeaders(HttpRequestMessage request, IEnumerable<ProtoField> headers)
    {
        foreach (var field in headers)
        {
            var h = CloudWire.Parse(field.Data!);
            var name = h.Text(1);
            var value = h.Text(2);
            if (name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
                throw new InvalidDataException();
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                if (request.Content is null || !request.Content.Headers.TryAddWithoutValidation(name, value))
                    throw new InvalidDataException(L.Text(TextId.UnsupportedTransferHeader));
            }
        }
    }

    private static async Task<byte[]> ReadBounded(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit)
            throw new IOException(L.Text(TextId.CloudFileIsTooLarge));
        using var output = new MemoryStream();
        await using var input = await content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[16384];
        int n;
        while ((n = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + n > limit)
                throw new IOException(L.Text(TextId.CloudFileIsTooLarge));
            await output.WriteAsync(buffer.AsMemory(0, n), token);
        }

        return output.ToArray();
    }
}
