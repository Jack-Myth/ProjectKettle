using System.Buffers;
using System.Net;

namespace Kettle.Cloud;

// A replayable range of an already locked, verified file. Does not own its source.
internal sealed class SaveBlockContent(Stream input, long offset, long length) : HttpContent
{
    protected override bool TryComputeLength(out long value) { value = length; return true; }
    protected override Task SerializeToStreamAsync(Stream output, TransportContext? context) => Copy(output, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream output, TransportContext? context, CancellationToken token) => Copy(output, token);

    private async Task Copy(Stream output, CancellationToken token)
    {
        input.Position = offset;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long remaining = length;
            while (remaining != 0)
            {
                int n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (n == 0) throw new EndOfStreamException("Staged upload was truncated.");
                await output.WriteAsync(buffer.AsMemory(0, n), token);
                remaining -= n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
