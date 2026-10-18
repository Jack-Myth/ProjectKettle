using Kettle.Core;

// Offline synchronization tests exercise retention without invoking Windows Shell.
internal sealed class TestBackupRecycle(string root) : IBackupRecycle
{
    public void Recycle(string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(path);
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Test recycling is restricted to its fixture directory.");
        var destination = Path.Combine(fullRoot, "mock-recycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(path))
            Directory.Move(path, destination);
        else
            File.Move(path, destination);
    }
}
