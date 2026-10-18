using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kettle.Core;

public sealed record LibraryCleanupFile(string Path, string BackupPath, string[] AppIds);
public sealed record LibraryCleanupResult(LibraryCleanupFile[] Files)
{
    public int RemovedEntries => Files.Sum(x => x.AppIds.Length);
    public string[] RecycledManifests { get; init; } = [];
    public string[] Skipped { get; init; } = [];
}

public sealed class SteamLibraryCleanupException(string message, Exception? inner = null) : IOException(message, inner);
public static class SteamLibraryCleanup
{
    // Standalone entry acquires the same ownership lease as login/update. Production
    // startup callers already hold that lease and call CleanOwned instead.
    public static LibraryCleanupResult CleanBeforeStart(string executable, IBackupRecycle? recycler = null)
    {
        executable = Path.GetFullPath(executable);
        using var owner = new Semaphore(1, 1, "Local\\Kettle.SteamCMD." + Hash(executable));
        if (!owner.WaitOne(0))
            throw new SteamLibraryCleanupException("SteamCMD 正在使用，未清理库配置。");
        try
        {
            return CleanOwned(executable, recycler);
        }
        finally
        {
            owner.Release();
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
    internal static LibraryCleanupResult CleanOwned(string executable, IBackupRecycle? recycler = null)
    {
        using var configLease = new Semaphore(1, 1, "Local\\Kettle.LibraryConfig." + Hash(executable));
        if (!configLease.WaitOne(0))
            throw new SteamLibraryCleanupException("库配置正在修改，未启动 SteamCMD，请稍后重试。");
        try
        {
            return Apply(executable, recycler);
        }
        finally
        {
            configLease.Release();
        }
    }

    private sealed record Plan(string Path, byte[] Before, byte[] After, string[] Apps);
    private sealed record Config(string Path, byte[] Before, Func<string, byte[]> Encode, KeyValuesDocument Document, IReadOnlyList<KeyValuesDocument.Node> Apps);
    private sealed record Manifest(string Path, byte[] Before, string AppId);
    private static LibraryCleanupResult Apply(string executable, IBackupRecycle? recycler = null)
    {
        var written = new List<Plan>();
        var recycled = new List<Manifest>();
        try
        {
            Idle(executable);
            string root = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(executable))!);
            var configs = new List<Config>();
            var blocked = new HashSet<uint>();
            var zeroIds = new HashSet<uint>();
            var skipped = new List<string>();
            bool confirmed = false;
            foreach (string relative in new[]
            {
                "steamapps",
                "config"
            }

            )
            {
                string path = Path.Combine(root, relative, "libraryfolders.vdf");
                if (!File.Exists(path))
                    continue;
                NoLink(path);
                byte[] before = File.ReadAllBytes(path);
                var (text, encode) = Decode(before);
                var document = KeyValuesDocument.Parse(text);
                var library = Unique(document.Children, "libraryfolders", true)!;
                if (library.Children is null || library.Conditional)
                    throw new InvalidDataException("libraryfolders 必须是明确的对象。");
                var zero = Unique(library.Children, "0");
                if (zero is null)
                    continue;
                if (zero.Children is null || zero.Conditional)
                    throw new InvalidDataException("0 号库必须是明确的对象。");
                var location = Unique(zero.Children, "path", true);
                if (location?.Value is null || location.Conditional || !Path.IsPathFullyQualified(location.Value) || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Value)).Equals(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("0 号库路径不是该 SteamCMD 默认目录，未清理任何配置或 ACF。");
                confirmed = true;
                var apps = Unique(zero.Children, "apps");
                if (apps is not null && (apps.Children is null || apps.Conditional))
                    throw new InvalidDataException("0 号库 apps 必须是明确的对象。");
                var entries = apps?.Children ?? [];
                configs.Add(new(path, before, encode, document, entries));
                foreach (var entry in entries)
                {
                    if (!uint.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0)
                        continue;
                    if (entry.Conditional || entry.Children is not null || entries.Count(x => uint.TryParse(x.Key, NumberStyles.None, CultureInfo.InvariantCulture, out uint other) && other == id) != 1 || !ulong.TryParse(entry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong size) || size != 0)
                        blocked.Add(id);
                    else
                        zeroIds.Add(id);
                }
            }

            var manifests = new List<Manifest>();
            string defaultApps = Path.Combine(root, "steamapps");
            if (confirmed && Directory.Exists(defaultApps))
            {
                NoLink(defaultApps);
                foreach (string path in Directory.EnumerateFiles(defaultApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(path);
                    string key = name[12..^4];
                    if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0 || key != id.ToString(CultureInfo.InvariantCulture))
                        continue;
                    if (blocked.Contains(id))
                        continue;
                    NoLink(path);
                    var bytes = File.ReadAllBytes(path);
                    var document = KeyValuesDocument.Parse(Decode(bytes).Text);
                    var state = Unique(document.Children, "AppState", true)!;
                    string? reason = null;
                    if (state.Children is null || state.Conditional)
                        throw new InvalidDataException("ACF AppState 结构无效：" + path);
                    string? Value(string field)
                    {
                        var node = Unique(state.Children, field);
                        if (node is not null && (node.Children is not null || node.Conditional))
                            throw new InvalidDataException("ACF 字段结构不明确：" + path + " / " + field);
                        return node?.Value;
                    }

                    if (!uint.TryParse(Value("appid"), NumberStyles.None, CultureInfo.InvariantCulture, out uint actual) || actual != id)
                        reason = "ACF AppID 与文件名不一致";
                    else if (!ulong.TryParse(Value("SizeOnDisk"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong size) || !uint.TryParse(Value("StateFlags"), NumberStyles.None, CultureInfo.InvariantCulture, out uint flags) || !ulong.TryParse(Value("buildid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong build))
                        reason = "ACF 安装状态不完整";
                    else if (size != 0 || build != 0 || (flags & ~3u) != 0)
                        reason = "ACF 显示非零内容或有效/进行中的安装状态";
                    else if (Value("StagingSize") is { } staging && (!ulong.TryParse(staging, NumberStyles.None, CultureInfo.InvariantCulture, out ulong staged) || staged != 0))
                        reason = "ACF 有暂存内容";
                    else
                    {
                        var depots = Unique(state.Children, "InstalledDepots");
                        if (depots is null || depots.Children is null || depots.Conditional || depots.Children.Count > 0)
                            reason = "ACF 仍有安装 depot";
                    }

                    if (reason is not null)
                    {
                        blocked.Add(id);
                        skipped.Add(id + "：" + reason + "，保留配置及 ACF。");
                        continue;
                    }

                    manifests.Add(new(path, bytes, key));
                }
            }

            var plans = new List<Plan>();
            foreach (var config in configs)
            {
                var remove = config.Apps.Where(x => uint.TryParse(x.Key, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && zeroIds.Contains(id) && !blocked.Contains(id)).ToArray();
                if (remove.Length == 0)
                    continue;
                string updated = config.Document.Remove(remove);
                _ = KeyValuesDocument.Parse(updated);
                plans.Add(new(config.Path, config.Before, config.Encode(updated), remove.Select(x => x.Key).ToArray()));
            }

            manifests = manifests.Where(x => !blocked.Contains(uint.Parse(x.AppId, CultureInfo.InvariantCulture))).ToList();
            // All parsing and installation checks precede any change. Only memory
            // holds rollback bytes; no persistent config or source backups.
            foreach (var manifest in manifests)
                Unchanged(manifest.Path, manifest.Before);
            foreach (var plan in plans)
            {
                Idle(executable);
                Unchanged(plan.Path, plan.Before);
                Replace(plan.Path, plan.After);
                written.Add(plan);
                Unchanged(plan.Path, plan.After);
            }

            foreach (var manifest in manifests)
            {
                Idle(executable);
                NoLink(manifest.Path);
                Unchanged(manifest.Path, manifest.Before);
                try
                {
                    (recycler ?? new WindowsBackupRecycle()).Recycle(manifest.Path);
                }
                catch
                {
                    if (!File.Exists(manifest.Path))
                        recycled.Add(manifest);
                    throw;
                }

                if (File.Exists(manifest.Path))
                    throw new IOException("ACF 回收未确认：" + manifest.Path);
                recycled.Add(manifest);
            }

            return new(plans.Select(x => new LibraryCleanupFile(x.Path, "", x.Apps)).ToArray())
            {
                RecycledManifests = recycled.Select(x => x.Path).ToArray(),
                Skipped = skipped.ToArray()
            };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            var rollback = new List<string>();
            foreach (var manifest in recycled.AsEnumerable().Reverse())
            {
                try
                {
                    Idle(executable);
                    if (File.Exists(manifest.Path))
                    {
                        Unchanged(manifest.Path, manifest.Before);
                        continue;
                    }

                    RestoreAbsent(manifest.Path, manifest.Before);
                }
                catch (Exception failure)
                {
                    rollback.Add(manifest.Path + "：" + failure.Message + "；原 ACF 仍可在回收站查找。");
                }
            }

            foreach (var plan in written.AsEnumerable().Reverse())
            {
                try
                {
                    Idle(executable);
                    Unchanged(plan.Path, plan.After);
                    Replace(plan.Path, plan.Before);
                }
                catch (Exception failure)
                {
                    rollback.Add(plan.Path + "：" + failure.Message);
                }
            }

            throw new SteamLibraryCleanupException("启动前清理 0 号库配置失败，未启动 SteamCMD：" + error.Message + (rollback.Count == 0 ? " 已保留或回滚原配置和 ACF。" : " 部分回滚未确认：" + string.Join("；", rollback)), error);
        }
    }

    private static void NoLink(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("默认库路径含链接，未清理：" + path);
        }
    }

    private static void RestoreAbsent(string path, byte[] bytes)
    {
        string temporary = path + ".kettle-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }

            File.Move(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static KeyValuesDocument.Node? Unique(IReadOnlyList<KeyValuesDocument.Node> nodes, string key, bool required = false)
    {
        var matches = nodes.Where(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1 || required && matches.Length == 0)
            throw new InvalidDataException("库配置 " + key + " 缺失或重复，无法安全清理。");
        return matches.SingleOrDefault();
    }

    private static void Idle(string executable)
    {
        if (SteamSession.HasConflict(executable))
            throw new IOException("该 SteamCMD 正在运行，未争写库配置。");
    }

    private static void Unchanged(string path, byte[] expected)
    {
        if (!File.ReadAllBytes(path).SequenceEqual(expected))
            throw new IOException("库配置已被其他操作修改：" + path);
    }

    private static void Replace(string path, byte[] bytes)
    {
        string temporary = path + ".kettle-zero-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }

            File.Replace(temporary, path, null);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static (string Text, Func<string, byte[]> Encode) Decode(byte[] bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("库配置过大。");
        Encoding encoding = new UTF8Encoding(false, true);
        int prefix = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            prefix = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            encoding = new UnicodeEncoding(false, false, true);
            prefix = 2;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            encoding = new UnicodeEncoding(true, false, true);
            prefix = 2;
        }

        var preamble = bytes[..prefix];
        string text = encoding.GetString(bytes, prefix, bytes.Length - prefix);
        return (text, value => preamble.Concat(encoding.GetBytes(value)).ToArray());
    }
}
