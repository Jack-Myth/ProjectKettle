using System.Security.Cryptography;
using System.Text.Json;

namespace Kettle.Cloud;

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
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.TheSavePathIsALinkAutomaticSyncIs)), SaveErrorCode.TheSavePathIsALinkAutomaticSyncIs);
            foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.SavesContainLinksAReliableBackupCannotBeCreated)), SaveErrorCode.SavesContainLinksAReliableBackupCannotBeCreated);
                if ((attrs & FileAttributes.Directory) != 0)
                    continue;
                total += new FileInfo(path).Length;
                if (total > 4L * 1024 * 1024 * 1024)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.TheSaveBackupExceedsSupportedLimits)), SaveErrorCode.TheSaveBackupExceedsSupportedLimits);
                var relative = Path.GetRelativePath(source, path);
                var target = Path.Combine(output, "files", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var bytes = await AutoCloudDisk.Read(path, token);
                await File.WriteAllBytesAsync(target, bytes, token);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target, token))) != hash)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.SaveBackupVerificationFailed)), SaveErrorCode.SaveBackupVerificationFailed);
                if (!SHA256.HashData(await AutoCloudDisk.Read(path, token)).SequenceEqual(SHA256.HashData(bytes)))
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.SavesAreStillChangingDuringBackupCloseTheGame)), SaveErrorCode.SavesAreStillChangingDuringBackupCloseTheGame);
                entries.Add(new BackupEntry(relative, bytes.Length, hash));
            }
        }

        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), CloudJson.Serialize(new BackupManifest(source, entries), new JsonSerializerOptions { WriteIndented = true }), token);
        return output;
    }
}
