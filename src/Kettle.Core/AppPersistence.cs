namespace Kettle.Core;
public static class AppPersistence
{
    public static async Task AtomicJson<T>(string file, T value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file + ".tmp", KettleJson.Serialize(value), token);
        File.Move(file + ".tmp", file, true);
    }
}
