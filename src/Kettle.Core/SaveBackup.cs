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
                throw new IOException(L.Text(TextId.TheSavePathIsALinkAutomaticSyncIs));
            foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(L.Text(TextId.SavesContainLinksAReliableBackupCannotBeCreated));
                if ((attrs & FileAttributes.Directory) != 0)
                    continue;
                total += new FileInfo(path).Length;
                if (total > 4L * 1024 * 1024 * 1024)
                    throw new IOException(L.Text(TextId.TheSaveBackupExceedsSupportedLimits));
                var relative = Path.GetRelativePath(source, path);
                var target = Path.Combine(output, "files", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var bytes = await AutoCloudDisk.Read(path, token);
                await File.WriteAllBytesAsync(target, bytes, token);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target, token))) != hash)
                    throw new IOException(L.Text(TextId.SaveBackupVerificationFailed));
                if (!SHA256.HashData(await AutoCloudDisk.Read(path, token)).SequenceEqual(SHA256.HashData(bytes)))
                    throw new IOException(L.Text(TextId.SavesAreStillChangingDuringBackupCloseTheGame));
                entries.Add(new BackupEntry(relative, bytes.Length, hash));
            }
        }

        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), KettleJson.Serialize(new BackupManifest(source, entries), new JsonSerializerOptions { WriteIndented = true }), token);
        return output;
    }
}
