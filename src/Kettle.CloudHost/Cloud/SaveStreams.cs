using System.Buffers;
using System.Security.Cryptography;

namespace Kettle.Cloud;

internal sealed record SaveDigest(long Size, string Sha1, string Sha256);

internal static class SaveStreams
{
    // Return buffers even on failure: a short-lived no-GC worker must reuse them.
    public static async Task<SaveDigest> Copy(Stream input, Stream? output, long limit, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        try
        {
            int n;
            while ((n = await input.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                size += n;
                if (size > limit) throw new IOException("Save payload exceeds its expected size.");
                sha1.AppendData(buffer, 0, n);
                sha256.AppendData(buffer, 0, n);
                if (output is not null) await output.WriteAsync(buffer.AsMemory(0, n), token);
            }
            return new(size, Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(sha256.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static string HashFile(string path, bool sha256 = false)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(sha256 ? SHA256.HashData(input) : SHA1.HashData(input));
    }

    public static async Task Download(ICloudFiles api, CloudFile file, string path, CancellationToken token)
    {
        // Only private staging/backup paths are passed here, never game-visible saves.
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            await api.DownloadAsync(file, output, token);
            output.Position = 0;
            var digest = await Copy(output, null, CloudTransfer.MaxFileBytes, token);
            if (digest.Size != file.Size || digest.Sha1 != file.Sha1)
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CloudCopyVerificationFailed)), SaveErrorCode.CloudCopyVerificationFailed);
            output.Flush(true);
        }
    }
}
