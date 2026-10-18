namespace Kettle.Core;

public interface IBackupDelete
{
    void Delete(string path);
}

// Callers select only obsolete owned backups or validated empty-install manifests.
public sealed class BackupDelete : IBackupDelete
{
    public void Delete(string path)
    {
        path = Path.GetFullPath(path);
        if (Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.Text(TextId.CannotDeleteADriveRoot));
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.Text(TextId.CannotDeleteALinkedPath) + current);
        bool directory = Directory.Exists(path);
        if (directory)
        {
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var current))
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException(L.Text(TextId.CannotDeleteABackupContainingLinks) + entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                }
            Directory.Delete(path, recursive: true);
        }
        else
            File.Delete(path);
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException(L.Text(TextId.DeletionIncomplete) + path);
    }
}
