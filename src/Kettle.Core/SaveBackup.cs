using System.Security.Cryptography;
using System.Text.Json;

namespace Kettle.Core;

public static class SaveBackup
{
    public static async Task<string> CreateAsync(string source, string backupBase, CancellationToken token, bool pendingRetention = false)
    {
        var output = Path.Combine(backupBase, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        if (pendingRetention)
            CloudBackupRetention.MarkPending(output);
        var entries = new List<BackupEntry>();
        long total = 0;
        if (Directory.Exists(source))
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("存档路径是链接，暂不支持自动同步。");
            foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("存档中包含链接，无法创建可靠备份。");
                if ((attrs & FileAttributes.Directory) != 0)
                    continue;
                total += new FileInfo(path).Length;
                if (total > 512 * 1024 * 1024)
                    throw new IOException("存档备份超过当前支持范围。");
                var relative = Path.GetRelativePath(source, path);
                var target = Path.Combine(output, "files", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var bytes = await File.ReadAllBytesAsync(path, token);
                await File.WriteAllBytesAsync(target, bytes, token);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target, token))) != hash)
                    throw new IOException("存档备份校验失败。");
                if (!SHA256.HashData(await File.ReadAllBytesAsync(path, token)).SequenceEqual(SHA256.HashData(bytes)))
                    throw new IOException("备份时存档仍在变化，请关闭游戏后重试。");
                entries.Add(new BackupEntry(relative, bytes.Length, hash));
            }
        }

        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), KettleJson.Serialize(new BackupManifest(source, entries), new JsonSerializerOptions { WriteIndented = true }), token);
        return output;
    }
}
