using System.Net;
using System.Text.Json;

namespace Kettle.Core;

public sealed record HeaderAsset(byte[] Data, bool Cached);
public interface IGameHeaderProvider
{
    Task<HeaderAsset?> LoadAsync(uint appId, CancellationToken token = default);
}

/// <summary>Fetches one selected game's official store header. Public artwork only, no account API.</summary>
public sealed class SteamHeaderService(string appDirectory, HttpClient? client = null) : IGameHeaderProvider
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private readonly HttpClient http = client ?? SharedClient;
    private const int MaxImage = 3 * 1024 * 1024;
    private readonly string cache = Path.Combine(appDirectory, "data", "images", "headers");
    public async Task<HeaderAsset?> LoadAsync(uint appId, CancellationToken token = default)
    {
        if (appId == 0)
            return null;
        var file = Path.Combine(cache, appId + ".img");
        byte[]? previous = null;
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length <= MaxImage)
            {
                previous = await File.ReadAllBytesAsync(file, token);
                if (!IsImage(previous))
                    previous = null;
                if (previous is not null && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-7))
                    return new(previous, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            using var metadata = await http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic", HttpCompletionOption.ResponseHeadersRead, token);
            metadata.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await ReadLimited(metadata.Content, 1024 * 1024, token));
            if (!json.RootElement.TryGetProperty(appId.ToString(), out var item) || !item.TryGetProperty("success", out var success) || !success.GetBoolean() || !item.TryGetProperty("data", out var data) || !data.TryGetProperty("header_image", out var header) || !Uri.TryCreate(header.GetString(), UriKind.Absolute, out var imageUri))
                return previous is null ? null : new(previous, true);
            if (imageUri.Scheme != "https" || !imageUri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected image host.");
            using var response = await http.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimited(response.Content, MaxImage, token);
            if (!IsImage(bytes))
                throw new InvalidDataException("Invalid header image.");
            // Cache errors do not prevent viewing public artwork, and never redirect to AppData.
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(cache);
                temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, file, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                if (temporary is not null)
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }

            return new(bytes, false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            return previous is null ? null : new(previous, true);
        }
    }

    private static bool IsImage(byte[] bytes) => bytes.Length >= 8 && (bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff || bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
    private static async Task<byte[]> ReadLimited(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit)
            throw new InvalidDataException("Header response too large.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var result = new MemoryStream();
        var buffer = new byte[16384];
        int size;
        while ((size = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (result.Length + size > limit)
                throw new InvalidDataException("Header response too large.");
            await result.WriteAsync(buffer.AsMemory(0, size), token);
        }

        return result.ToArray();
    }
}
