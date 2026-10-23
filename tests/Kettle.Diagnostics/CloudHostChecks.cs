using Kettle.Core;
internal static class CloudHostChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var home = Path.Combine(Path.GetTempPath(), "Kettle-worker-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var reports = await CloudHostTasks.MaintainAsync(home);
            check(reports.Length == 0, "worker handles an empty backup tree");
            var source = Path.Combine(home, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "synthetic save");
            var backup = await CloudHostTasks.BackupAsync(source, Path.Combine(home, "backups"), default);
            check(await File.ReadAllTextAsync(Path.Combine(backup, "files", "save.dat")) == "synthetic save", "worker creates and verifies the backup");
            check(File.Exists(Path.Combine(backup, "manifest.json")), "worker writes the backup manifest");
            using var locked = new FileStream(Path.Combine(source, "save.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var originalLanguage = L.Language;
            try
            {
                foreach (var language in new[] { "en", "zh-CN" })
                {
                    L.Initialize(language);
                    try
                    {
                        await CloudHostTasks.BackupAsync(source, Path.Combine(home, "failed-backups"), default);
                        throw new Exception("Locked save unexpectedly accepted.");
                    }
                    catch (IOException error)
                    {
                        check(error.Message.Contains(L.Text(TextId.CouldNotReadTheSaveFile)), "frontend localizes real worker failure: " + language);
                    }
                }
            }
            finally { L.Initialize(originalLanguage); }

        }
        finally { Directory.Delete(home, true); }
    }
}
