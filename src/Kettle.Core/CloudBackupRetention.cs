using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kettle.Core;

public sealed record BackupRetentionReport(string Scope, string[] Kept, string[] Deleted, string[] Protected, string[] Incomplete, string? Error = null);
public sealed class CloudBackupRetention : IDisposable
{
    public const int Limit = 3;
    private const string Pending = ".kettle-backup-pending.json";
    private readonly string home;
    private readonly IBackupDelete deletion;
    private readonly Semaphore ownership;
    public CloudBackupRetention(string scope, IBackupDelete? deleter = null)
    {
        home = Path.GetFullPath(scope);
        deletion = deleter ?? new BackupDelete();
        for (var ancestor = new DirectoryInfo(home); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists)
                NoLink(ancestor.FullName);
            if (ancestor.Name == "data")
                break;
        }

        ownership = new Semaphore(1, 1, "Local\\Kettle.CloudRetention." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home.ToUpperInvariant()))));
        if (!ownership.WaitOne(0))
        {
            ownership.Dispose();
            throw new IOException(L.Text(TextId.CloudSaveBackupsAreInUseCleanupAndNew));
        }
    }

    public void Dispose()
    {
        ownership.Release();
        ownership.Dispose();
    }

    public static void MarkPending(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Pending), "{\"Owner\":\"Kettle.CloudBackup\",\"Version\":1}");
    }

    public void BeforeCreate()
    {
        _ = Enforce();
        var references = References();
        if (Candidates().Count(x => references.Any(p => Within(x, p))) >= Limit)
            throw new IOException(L.Text(TextId.AllThreeBackupsAreReferencedByRecoveryOrConflict));
    }

    public void Commit(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!Candidates().Contains(directory, StringComparer.OrdinalIgnoreCase) || !IsComplete(directory))
            throw new IOException(L.Text(TextId.TheNewBackupHasNotBeenFullyVerifiedRetention));
        _ = Enforce(directory);
        File.Delete(Path.Combine(directory, Pending));
    }

    public BackupRetentionReport Enforce(string? committing = null, bool preview = false)
    {
        var candidates = Candidates();
        var references = References();
        var pinned = candidates.Where(x => references.Any(p => Within(x, p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (committing is not null)
            pinned.Add(committing);
        var deleted = new List<string>();
        var incomplete = new List<string>();
        var complete = new List<string>();
        foreach (var path in candidates)
        {
            if (File.Exists(Path.Combine(path, Pending)) && !path.Equals(committing, StringComparison.OrdinalIgnoreCase))
            {
                if (pinned.Contains(path))
                {
                    incomplete.Add(path);
                    continue;
                }

                // Only newly marked, abandoned Kettle creations can be deleted as partials.
                using var marker = ReadJson(Path.Combine(path, Pending));
                if (marker.RootElement.GetProperty("Owner").GetString() != "Kettle.CloudBackup")
                {
                    incomplete.Add(path);
                    continue;
                }

                if (!preview)
                    Delete(path);
                deleted.Add(path);
                continue;
            }

            if (IsComplete(path))
                complete.Add(path);
            else
                incomplete.Add(path);
        }

        var protectedComplete = complete.Where(pinned.Contains).ToArray();
        if (protectedComplete.Length > Limit)
            throw new IOException(L.Text(TextId.MoreThanThreeBackupsAreReferencedByActiveRecovery));
        var keep = protectedComplete.Concat(complete.Where(x => !pinned.Contains(x)).OrderByDescending(x => Path.GetFileName(x)[..15], StringComparer.Ordinal).ThenByDescending(Directory.GetCreationTimeUtc).ThenByDescending(Path.GetFileName, StringComparer.Ordinal).Take(Limit - protectedComplete.Length)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in complete.Where(x => !keep.Contains(x)).OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal))
        {
            if (!preview)
                Delete(path);
            deleted.Add(path);
        }

        return new(home, keep.ToArray(), deleted.ToArray(), pinned.ToArray(), incomplete.ToArray());
    }

    private void Delete(string path)
    {
        ValidateTree(path);
        deletion.Delete(path);
        if (Directory.Exists(path))
            throw new IOException(L.Text(TextId.OldBackupsWereNotDeletedCleanupCouldNotBe) + path);
    }

    private List<string> Candidates()
    {
        var result = new List<string>();
        foreach (string kind in new[]
        {
            "backups",
            "autocloud-backups"
        }

        )
        {
            string root = Path.Combine(home, kind);
            if (!Directory.Exists(root))
                continue;
            NoLink(root);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                NoLink(directory);
                string name = Path.GetFileName(directory);
                if (name.Length == 48 && name[15] == '-' && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && Guid.TryParseExact(name[16..], "N", out _))
                    result.Add(Path.GetFullPath(directory));
            }
        }

        return result;
    }

    private HashSet<string> References()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in value.EnumerateObject())
                    Scan(p.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in value.EnumerateArray())
                    Scan(v);
            }
            else if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && Path.IsPathFullyQualified(text))
                paths.Add(Path.GetFullPath(text));
        }

        foreach (string file in new[]
        {
            "autocloud-local-transaction.json",
            "backup-recovery.json"
        }

        )
            if (File.Exists(Path.Combine(home, file)))
            {
                using var doc = ReadJson(Path.Combine(home, file));
                Scan(doc.RootElement);
            }

        // Persistent run prompts identify the currently active conflict copies.
        var runs = Path.Combine(Directory.GetParent(home)!.Parent!.Parent!.Parent!.FullName, "runs");
        if (Directory.Exists(runs))
        {
            NoLink(runs);
            foreach (var file in Directory.EnumerateFiles(runs, "*.json"))
            {
                using var doc = ReadJson(file);
                Scan(doc.RootElement);
            }
        }

        foreach (var conflict in paths.Where(x => Within(Path.Combine(home, "conflicts"), x)).ToArray())
        {
            string versions = Path.Combine(conflict, "versions.json");
            if (File.Exists(versions))
            {
                using var doc = ReadJson(versions);
                Scan(doc.RootElement);
            }
        }

        return paths;
    }

    private static bool Within(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void NoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L.Text(TextId.BackupRetentionDoesNotProcessLinks) + path);
    }

    private static void ValidateTree(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        int count = 0;
        while (pending.TryPop(out var current))
        {
            NoLink(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                if (++count > 50000)
                    throw new IOException(L.Text(TextId.BackupDirectoryIsTooLargeItHasBeenKept));
                NoLink(path);
                if (Directory.Exists(path))
                    pending.Push(path);
            }
        }
    }

    private static JsonDocument ReadJson(string path)
    {
        NoLink(path);
        if (new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new IOException(L.Text(TextId.BackupRecordIsTooLarge));
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    private static bool IsComplete(string path)
    {
        try
        {
            ValidateTree(path);
            if (Path.GetFileName(Path.GetDirectoryName(path)) == "backups")
                return RemoteComplete(path);
            string before = Path.Combine(path, "before-init.json"), manifest = Path.Combine(path, "local-manifest.json");
            if (!File.Exists(before) || !File.Exists(manifest))
                return false;
            using var init = ReadJson(before);
            string native = Path.GetFullPath(init.RootElement.GetProperty("NativeBackup").GetString()!);
            if (!Within(path, native) || !RemoteComplete(native))
                return false;
            using var local = ReadJson(manifest);
            var files = local.RootElement.GetProperty("Files");
            var copies = local.RootElement.GetProperty("Backups");
            if (files.EnumerateObject().Count() != copies.EnumerateObject().Count())
                return false;
            foreach (var file in files.EnumerateObject())
            {
                string copy = Path.GetFullPath(copies.GetProperty(file.Name).GetString()!);
                if (!Within(path, copy))
                    return false;
                var item = file.Value.GetProperty("File");
                if (new FileInfo(copy).Length != item.GetProperty("Size").GetInt64() || !Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(copy))).Equals(item.GetProperty("Sha1").GetString(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return false;
        }
    }

    private static bool RemoteComplete(string path)
    {
        using var manifest = ReadJson(Path.Combine(path, "manifest.json"));
        foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
        {
            string copy = Path.GetFullPath(Path.Combine(path, "files", file.GetProperty("Path").GetString()!));
            if (!Within(Path.Combine(path, "files"), copy))
                return false;
            if (new FileInfo(copy).Length != file.GetProperty("Size").GetInt64() || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy))).Equals(file.GetProperty("SHA256").GetString(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public static BackupRetentionReport[] MaintainAll(string appDirectory, IBackupDelete? deleter = null, bool preview = false)
    {
        var reports = new List<BackupRetentionReport>();
        string root = Path.Combine(Path.GetFullPath(appDirectory), "data", "cloud");
        if (!Directory.Exists(root))
            return [];
        NoLink(root);
        foreach (var account in Directory.EnumerateDirectories(root).Where(p => uint.TryParse(Path.GetFileName(p), out _)))
        {
            NoLink(account);
            foreach (var app in Directory.EnumerateDirectories(account).Where(p => uint.TryParse(Path.GetFileName(p), out _)))
            {
                NoLink(app);
                foreach (var scope in Directory.EnumerateDirectories(app).Where(p => Path.GetFileName(p).Length == 16 && Path.GetFileName(p).All(Uri.IsHexDigit)))
                {
                    try
                    {
                        NoLink(scope);
                        using var retention = new CloudBackupRetention(scope, deleter);
                        reports.Add(retention.Enforce(preview: preview));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                    {
                        reports.Add(new(scope, [], [], [], [], error.Message));
                    }
                }
            }
        }

        return reports.ToArray();
    }
}
