using System.Security.Cryptography;
using System.Text.Json;
using Kettle.Core;

internal static class BackupRetentionChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var trash = new Trash(Path.Combine(root, "recycle-fixture"));
        for (int count = 0; count <= 4; count++)
        {
            string home = Home(Path.Combine(root, "count-" + count));
            var originals = Enumerable.Range(1, count).Select(i => Backup(home, i)).ToArray();
            using (var retention = new CloudBackupRetention(home, trash))
            {
                var report = retention.Enforce();
                check(report.Kept.Length == Math.Min(3, count) && report.Recycled.Length == Math.Max(0, count - 3), "zero through four complete backups are bounded to three");
                check(report.Kept.All(Directory.Exists) && report.Recycled.All(p => !Directory.Exists(p)) && (count < 4 || report.Recycled.Single() == originals[0]), "newest complete versions retained; oldest moved recoverably");
                check(retention.Enforce().Recycled.Length == 0, "retention is idempotent across restart passes");
            }
        }

        string app = Path.Combine(root, "isolation");
        var scopes = new[]
        {
            Home(app, "1", "12", "AAAAAAAAAAAAAAAA"),
            Home(app, "2", "12", "AAAAAAAAAAAAAAAA"),
            Home(app, "1", "13", "AAAAAAAAAAAAAAAA"),
            Home(app, "1", "12", "BBBBBBBBBBBBBBBB")
        };
        foreach (var scope in scopes)
            for (int i = 1; i <= 4; i++)
                Backup(scope, i, i % 2 == 0);
        var reports = CloudBackupRetention.MaintainAll(app, trash);
        check(reports.Length == 4 && reports.All(r => r.Kept.Length == 3 && r.Recycled.Length == 1 && r.Error is null), "accounts, games and SteamCMD-path scopes have independent combined RemoteStorage/AutoCloud quotas");
        string homePinned = Home(Path.Combine(root, "pinned"));
        var pinned = Enumerable.Range(1, 4).Select(i => Backup(homePinned, i)).ToArray();
        File.WriteAllText(Path.Combine(homePinned, "autocloud-local-transaction.json"), JsonSerializer.Serialize(new
        {
            Files = new[] { new { BeforeFile = Path.Combine(pinned[0], "files", "save.dat") } }
        }));
        using (var retention = new CloudBackupRetention(homePinned, trash))
        {
            var report = retention.Enforce();
            check(Directory.Exists(pinned[0]) && report.Kept.Length == 3 && report.Recycled.Single() == pinned[1], "old active recovery backup consumes a slot and is never removed");
            var current = report.Kept;
            File.WriteAllText(Path.Combine(homePinned, "autocloud-local-transaction.json"), JsonSerializer.Serialize(new
            {
                Paths = current
            }));
            bool blocked = false;
            try
            {
                retention.BeforeCreate();
            }
            catch (IOException)
            {
                blocked = true;
            }

            check(blocked && current.All(Directory.Exists), "three recovery references prevent creating a fourth complete backup");
        }

        string conflictApp = Path.Combine(root, "conflict"), conflictHome = Home(conflictApp);
        var old = Backup(conflictHome, 1);
        for (int i = 2; i <= 4; i++)
            Backup(conflictHome, i);
        string conflict = Path.Combine(conflictHome, "conflicts", "active");
        Directory.CreateDirectory(conflict);
        File.WriteAllText(Path.Combine(conflict, "versions.json"), JsonSerializer.Serialize(new
        {
            LocalBackup = old
        }));
        Directory.CreateDirectory(Path.Combine(conflictApp, "data", "runs"));
        File.WriteAllText(Path.Combine(conflictApp, "data", "runs", "run.json"), JsonSerializer.Serialize(new
        {
            Status = new
            {
                Conflict = new
                {
                    PreservedCopies = conflict
                }
            }
        }));
        using (var retention = new CloudBackupRetention(conflictHome, trash))
        {
            var report = retention.Enforce();
            check(Directory.Exists(old) && report.Kept.Length == 3, "persisted active run conflict resolves versions.json and protects its LocalBackup");
        }

        string failedHome = Home(Path.Combine(root, "failed"));
        var good = Enumerable.Range(1, 3).Select(i => Backup(failedHome, i)).ToArray();
        string partial = Path.Combine(failedHome, "backups", Name(4));
        CloudBackupRetention.MarkPending(partial);
        File.WriteAllText(Path.Combine(partial, "half-copy.dat"), "fixture");
        string unknown = Path.Combine(failedHome, "backups", Name(5));
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "manifest.json"), "broken");
        using (var retention = new CloudBackupRetention(failedHome, trash))
        {
            var report = retention.Enforce();
            check(good.All(Directory.Exists) && !Directory.Exists(partial) && Directory.Exists(unknown) && report.Incomplete.Contains(unknown), "failed owned creation is recycled; incomplete unknown backup cannot evict a good complete version");
        }

        string createHome = Home(Path.Combine(root, "create"));
        for (int i = 1; i <= 3; i++)
            Backup(createHome, i);
        using (var retention = new CloudBackupRetention(createHome, trash))
        {
            retention.BeforeCreate();
            string newest = Backup(createHome, 4);
            CloudBackupRetention.MarkPending(newest);
            retention.Commit(newest);
            check(!File.Exists(Path.Combine(newest, ".kettle-backup-pending.json")) && retention.Enforce().Kept.Length == 3, "new verified backup commits only after bounded retention succeeds");
        }

        string deniedHome = Home(Path.Combine(root, "recycle-denied"));
        var denied = Enumerable.Range(1, 4).Select(i => Backup(deniedHome, i)).ToArray();
        using (var retention = new CloudBackupRetention(deniedHome, new Denied()))
        {
            bool blocked = false;
            try
            {
                retention.Enforce();
            }
            catch (IOException)
            {
                blocked = true;
            }

            check(blocked && denied.All(Directory.Exists), "failed recycling leaves original backups and never falls back to permanent deletion");
        }

        string locked = Home(Path.Combine(root, "locked"));
        Backup(locked, 1);
        using (var lease = new CloudBackupRetention(locked, trash))
        {
            var report = CloudBackupRetention.MaintainAll(Path.Combine(root, "locked"), trash);
            check(report.Single().Error is not null, "startup maintenance skips an active synchronization scope");
        }

        string corrupt = Home(Path.Combine(root, "corrupt-reference"));
        var before = Enumerable.Range(1, 4).Select(i => Backup(corrupt, i)).ToArray();
        File.WriteAllText(Path.Combine(corrupt, "autocloud-local-transaction.json"), "broken");
        using (var retention = new CloudBackupRetention(corrupt, trash))
        {
            bool blocked = false;
            try
            {
                retention.Enforce();
            }
            catch (JsonException)
            {
                blocked = true;
            }

            check(blocked && before.All(Directory.Exists), "malformed recovery references prevent deletion");
        }

        check(trash.Moves.All(x => Directory.Exists(x.Value) && !Directory.Exists(x.Key)), "all mock removals remain recoverable in the fixture recycle store");
        await Task.CompletedTask;
    }

    private static string Home(string app, string account = "1", string game = "12", string hash = "AAAAAAAAAAAAAAAA")
    {
        string home = Path.Combine(app, "data", "cloud", account, game, hash);
        Directory.CreateDirectory(home);
        return home;
    }

    private static string Name(int number) => "202610" + number.ToString("D2") + "-120000-" + Guid.NewGuid().ToString("N");
    private static string Backup(string home, int day, bool auto = false)
    {
        string path = Path.Combine(home, auto ? "autocloud-backups" : "backups", Name(day));
        Directory.CreateDirectory(path);
        byte[] bytes = [1, 2, 3, (byte)day];
        if (!auto)
        {
            Directory.CreateDirectory(Path.Combine(path, "files"));
            File.WriteAllBytes(Path.Combine(path, "files", "save.dat"), bytes);
            File.WriteAllText(Path.Combine(path, "manifest.json"), JsonSerializer.Serialize(new
            {
                Source = "fixture",
                Files = new[] { new { Path = "save.dat", Size = bytes.Length, SHA256 = Convert.ToHexString(SHA256.HashData(bytes)) } }
            }));
        }
        else
        {
            string copy = Path.Combine(path, "local-0.dat");
            File.WriteAllBytes(copy, bytes);
            File.WriteAllText(Path.Combine(path, "local-manifest.json"), JsonSerializer.Serialize(new
            {
                Files = new Dictionary<string, object> { { "save", new { File = new { Sha1 = Convert.ToHexString(SHA1.HashData(bytes)), Size = bytes.Length } } } },
                Backups = new Dictionary<string, string> { { "save", copy } }
            }));
            string native = Path.Combine(path, "native-index-before", "snapshot");
            Directory.CreateDirectory(native);
            File.WriteAllText(Path.Combine(native, "manifest.json"), "{\"Files\":[]}");
            File.WriteAllText(Path.Combine(path, "before-init.json"), JsonSerializer.Serialize(new
            {
                NativeBackup = native
            }));
        }

        return path;
    }

    private sealed class Trash(string home) : IBackupRecycle
    {
        public Dictionary<string, string> Moves = [];
        public void Recycle(string path)
        {
            Directory.CreateDirectory(home);
            string target = Path.Combine(home, Guid.NewGuid().ToString("N"));
            Directory.Move(path, target);
            Moves.Add(path, target);
        }
    }

    private sealed class Denied : IBackupRecycle
    {
        public void Recycle(string path) => throw new IOException("fixture recycle unavailable");
    }
}
