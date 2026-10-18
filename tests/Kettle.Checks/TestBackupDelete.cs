using Kettle.Core;

// Offline synchronization tests exercise retention without invoking Windows Shell.
internal sealed class TestBackupDelete(string root) : IBackupDelete
{
    public void Delete(string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(path);
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Test deletion is restricted to its fixture directory.");
        new BackupDelete().Delete(path);
    }
}
