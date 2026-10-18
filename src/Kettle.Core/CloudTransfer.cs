using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace Kettle.Core;

public sealed record CloudFile(string Name, string Sha1, long Size, long Timestamp, uint Platforms = uint.MaxValue);
public sealed record CloudSnapshot(ulong ChangeNumber, CloudFile[] Files);
public sealed record CloudUpload(string Name, byte[] Data, long Timestamp, string? ExpectedRemoteSha1, uint Platforms = uint.MaxValue);
public interface ICloudFiles
{
    Task<CloudSnapshot> ListAsync(CancellationToken token);
    Task<byte[]> DownloadAsync(CloudFile file, CancellationToken token);
    Task UploadAsync(IReadOnlyList<CloudUpload> files, Func<ulong, Task> saveBatch, CancellationToken token);
    Task CloseBatchAsync(ulong batch, bool success, CancellationToken token);
}

public sealed class CloudTransfer(uint appId, ICloudRpc rpc, HttpClient? client = null, AutoCloudPathPlan? autoCloud = null) : ICloudFiles
{
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly HttpClient http = client ?? SharedHttp;
    public const int MaxFileBytes = 64 * 1024 * 1024;
    private async Task<ProtoField[]> Call(string method, byte[] bytes, CancellationToken token) => CloudWire.Parse(await rpc.CallAsync("Cloud." + method + "#1", bytes, token));
    public async Task<CloudSnapshot> ListAsync(CancellationToken token)
    {
        var data = await Call("GetAppFileChangelist", CloudWire.Message((1, appId), (2, 0ul)), token);
        if (data.Number(3) != 0 || autoCloud is null && data.Any(f => f.Number == 4 && f.Data?.Length > 0))
            throw new NotSupportedException("此云存档使用增量或未验证路径前缀，无法安全处理。");
        var prefixes = data.Where(f => f.Number == 4).Select(f => System.Text.Encoding.UTF8.GetString(f.Data!)).ToArray();
        var files = new List<CloudFile>();
        foreach (var field in data.Where(f => f.Number == 2))
        {
            var f = CloudWire.Parse(field.Data!);
            var name = f.Text(1);
            if (autoCloud is null)
                ValidateName(name);
            if (f.Number(5) != 0 || f.Number(9) != 0 || f.Bytes(2).Length != 20 || f.Number(4) > MaxFileBytes)
                throw new NotSupportedException("云端包含删除、重上传或超出范围的文件状态。");
            if (f.Any(x => x.Number == 6) && (f.Number(6) & 1) == 0)
                continue;
            if (autoCloud is not null)
            {
                var index = f.Number(7);
                if (prefixes.Length > 0)
                    name = (index < (ulong)prefixes.Length ? prefixes[(int)index] : throw new InvalidDataException("云端路径前缀索引越界。")) + name;
                _ = autoCloud.ToLocal(AutoCloudWire.Key(name));
            }

            files.Add(new(name, Convert.ToHexString(f.Bytes(2)), checked((long)f.Number(4)), checked((long)f.Number(3)), f.Any(x => x.Number == 6) ? checked((uint)f.Number(6)) : uint.MaxValue));
        }

        if (files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("云端文件名重复。");
        if (files.Count > 256 || files.Sum(f => f.Size) > 256L * 1024 * 1024)
            throw new NotSupportedException("云存档目录超过支持范围。");
        if (autoCloud is not null)
            autoCloud.MapAll(files.Select(f => AutoCloudWire.Key(f.Name)));
        return new(data.Number(1), files.ToArray());
    }

    public async Task<byte[]> DownloadAsync(CloudFile file, CancellationToken token)
    {
        if (autoCloud is not null)
            _ = autoCloud.ToLocal(AutoCloudWire.Key(file.Name));
        var meta = await Call("ClientFileDownload", CloudWire.Message((1, appId), (2, file.Name)), token);
        if (meta.Number(1) != appId || meta.Number(6) != 0 || meta.Number(11) != 0 || meta.Number(9) == 0 || meta.Number(3) != (ulong)file.Size || !meta.Bytes(4).SequenceEqual(Convert.FromHexString(file.Sha1)))
            throw new IOException("云端下载版本已变化或加密格式不受支持。");
        using var request = new HttpRequestMessage(HttpMethod.Get, Address(meta.Text(7), meta.Text(8), true));
        AddHeaders(request, meta.Where(f => f.Number == 10));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var wire = await ReadBounded(response.Content, MaxFileBytes, token);
        if ((ulong)wire.Length != meta.Number(2))
            throw new IOException("云端下载长度不符。");
        byte[] raw;
        if (wire.Length >= 4 && wire[0] == 'P' && wire[1] == 'K' && wire[2] == 3 && wire[3] == 4)
        {
            using var zip = new ZipArchive(new MemoryStream(wire));
            if (zip.Entries.Count != 1 || zip.Entries[0].Length != file.Size)
                throw new InvalidDataException("不支持的云存档压缩格式。");
            using var stream = zip.Entries[0].Open();
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, token);
            raw = output.ToArray();
        }
        else
            raw = wire;
        if (raw.LongLength != file.Size || Hash(raw) != file.Sha1)
            throw new IOException("下载后的存档校验失败。");
        return raw;
    }

    public async Task UploadAsync(IReadOnlyList<CloudUpload> files, Func<ulong, Task> saveBatch, CancellationToken token)
    {
        if (files.Count == 0)
            return;
        if (files.Count > 256 || files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count || files.Sum(f => (long)f.Data.Length) > 256L * 1024 * 1024)
            throw new NotSupportedException("上传文件重复或超出范围。");
        foreach (var file in files)
        {
            if (autoCloud is null)
                ValidateName(file.Name);
            else
                _ = autoCloud.ToLocal(AutoCloudWire.Key(file.Name));
            if (file.Data.Length > MaxFileBytes || file.Timestamp < 0 || (file.Platforms & 1) == 0)
                throw new NotSupportedException("上传状态或平台不受支持。");
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
                throw new IOException("云上传批次未建立。");
            await saveBatch(batch);
            token.ThrowIfCancellationRequested();
            await CheckExpected(files, token);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (autoCloud is null)
                    ValidateName(file.Name);
                else
                    _ = autoCloud.ToLocal(AutoCloudWire.Key(file.Name));
                if (file.Data.Length > MaxFileBytes)
                    throw new NotSupportedException("存档超出上传大小限制。");
                await CheckExpected([file], token);
                var sha = SHA1.HashData(file.Data);
                bool fileCommitted = false, started = false;
                try
                {
                    var upload = await Call("ClientBeginFileUpload", CloudWire.Message((1, appId), (2, (uint)file.Data.Length), (3, (uint)file.Data.Length), (4, sha), (5, (ulong)file.Timestamp), (6, file.Name), (7, file.Platforms), (10, false), (13, batch)), token);
                    started = true;
                    if (upload.Number(1) != 0)
                        throw new NotSupportedException("服务器要求加密上传，当前不支持。");
                    var blocks = upload.Where(f => f.Number == 2).ToArray();
                    if (blocks.Length == 0)
                        throw new IOException("服务器未返回上传分块。");
                    foreach (var block in blocks)
                    {
                        var b = CloudWire.Parse(block.Data!);
                        var method = b.Number(4) switch
                        {
                            3 => HttpMethod.Post,
                            4 => HttpMethod.Put,
                            _ => throw new NotSupportedException("不支持服务器要求的上传方法。")
                        };
                        var body = b.Bytes(8);
                        if (!b.Any(f => f.Number == 8))
                        {
                            var offset = checked((int)b.Number(6));
                            var length = checked((int)b.Number(7));
                            if (offset < 0 || length < 0 || offset > file.Data.Length - length)
                                throw new InvalidDataException("上传分块越界。");
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
                        throw new IOException("云端没有确认文件提交。");
                    fileCommitted = true;
                    var info = await Call("GetSingleFileInfo", CloudWire.Message((1, appId), (2, file.Name)), token);
                    if (!info.Bytes(4).SequenceEqual(sha) || info.Number(6) != (ulong)file.Data.Length || info.Number(7) != 0 || autoCloud is not null && (info.Number(2) != appId || info.Text(3) != file.Name))
                        throw new IOException("云端文件提交后身份或哈希尚未确认。");
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
        if (files.Any(file => verify.Files.FirstOrDefault(f => f.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase))?.Sha1 != Hash(file.Data)))
            throw new IOException("上传结果未被新云端列表确认，请重试同步。");
    }

    private async Task CheckExpected(IReadOnlyList<CloudUpload> files, CancellationToken token)
    {
        var snapshot = await ListAsync(token);
        foreach (var file in files)
        {
            var current = snapshot.Files.FirstOrDefault(f => f.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase));
            if (current?.Sha1 != file.ExpectedRemoteSha1 || current is not null && (current.Platforms != file.Platforms || current.Name != file.Name))
                throw new CloudConflictException("上传前云端版本、文件名或平台已变化，已停止覆盖。", "");
        }
    }

    public async Task CloseBatchAsync(ulong batch, bool success, CancellationToken token)
    {
        await Call("CompleteAppUploadBatchBlocking", CloudWire.Message((1, appId), (2, batch), (3, success ? 1u : 2u)), token);
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data));
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') || name.EndsWith('.') || name.EndsWith(' ') || name.Length > 240 || System.Text.RegularExpressions.Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new NotSupportedException("当前只支持平坦、安全文件名的 RemoteStorage 存档。");
    }

    private static Uri Address(string host, string path, bool https)
    {
        if (!https || string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains('@') || !path.StartsWith('/'))
            throw new NotSupportedException("云端传输地址不安全或不是HTTPS。");
        var uri = new Uri("https://" + host + path);
        if (IPAddress.TryParse(uri.Host, out _) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("不支持的云传输地址。");
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
                    throw new InvalidDataException("不支持的传输头。");
            }
        }
    }

    private static async Task<byte[]> ReadBounded(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit)
            throw new IOException("云文件过大。");
        using var output = new MemoryStream();
        await using var input = await content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[16384];
        int n;
        while ((n = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + n > limit)
                throw new IOException("云文件过大。");
            await output.WriteAsync(buffer.AsMemory(0, n), token);
        }

        return output.ToArray();
    }
}
