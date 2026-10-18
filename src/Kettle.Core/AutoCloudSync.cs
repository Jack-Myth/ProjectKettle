using System.Security.Cryptography;
using System.Text.Json;

namespace Kettle.Core;

internal sealed record AutoLocal(CloudFile File, string Path);
internal sealed record AutoBaseline(string Plan, Dictionary<string, string> Hashes, ulong ChangeNumber);
internal sealed record AutoReplace(string WireName, string? BeforeHash, string AfterHash, string? BeforeFile, string AfterFile, long BeforeTime, long AfterTime, string? TemporaryFile = null);
internal sealed record AutoTransaction(AutoBaseline ExpectedBaseline, AutoReplace[] Files);
/// <summary>AutoCloud uses KnownFolders and full wire names. No synthetic native index.</summary>
internal sealed class AutoCloudSync(string home, Func<CloudContext, CancellationToken, Task<ICloudRpc>> connect, Func<uint, ICloudRpc, ICloudFiles>? files, IAutoCloudRoots roots, IBackupRecycle? recycle = null)
{
    private string BaselineFile => Path.Combine(home, "autocloud-baseline.json");
    private string TransactionFile => Path.Combine(home, "autocloud-local-transaction.json");

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(KettleJson.Serialize(value))));
    public async Task SyncAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
    {
        if (context.Metadata.AutoCloudAppInfo is null)
            throw new NotSupportedException("缺少 AutoCloud 规则，请重新读取游戏信息后重试。");
        var definition = AutoCloudDefinition.Parse(context.AppId, context.Metadata.AutoCloudAppInfo);
        var plan = AutoCloudPathPlan.Compile(definition, new(context.AccountId, 76561197960265728ul + context.AccountId), AutoCloudPlatform.Windows, roots, context.InstallPath);
        if (plan.Rules.Count == 0)
            throw new NotSupportedException("当前平台没有适用的 AutoCloud 规则。");
        var signature = Digest(plan.Rules);
        Directory.CreateDirectory(home);
        using var retention = new CloudBackupRetention(home, recycle);
        retention.BeforeCreate();
        await using var syncLock = new FileStream(Path.Combine(home, "autocloud-sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var scratch = await JournalTemps(plan, token);
        var start = await Scan(plan, token, scratch);
        var backup = Path.Combine(home, "autocloud-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        CloudBackupRetention.MarkPending(backup);
        var before = await Backup(start, backup, token);
        // Save payloads and the potentially rewritten native index before SteamAPI.Init.
        var nativeRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(context.SteamCmd)!, "userdata", context.AccountId.ToString(), context.AppId.ToString()));
        var nativeBackup = await SaveBackup.CreateAsync(nativeRoot, Path.Combine(backup, "native-index-before"), token);
        await CloudSyncService.AtomicJson(Path.Combine(backup, "before-init.json"), new AutoBeforeInit(context.AppId, context.AccountId, plan.Rules, start, before, nativeBackup), token);
        if (!Same(start, await Scan(plan, token, scratch)))
            throw new IOException("备份期间存档发生变化，未初始化云接口。");
        retention.Commit(backup);
        await Recover(plan, token); // May roll back this app's interrupted local replacements, never game edits.
        start = await Scan(plan, token);
        AutoBaseline? baseline = File.Exists(BaselineFile) ? KettleJson.Deserialize<AutoBaseline>(await File.ReadAllTextAsync(BaselineFile, token)) : null;
        if (baseline is not null)
            baseline = baseline with
            {
                Hashes = new(baseline.Hashes, StringComparer.OrdinalIgnoreCase)
            };
        if (baseline is not null && baseline.Plan != signature)
            throw new IOException("AutoCloud 规则、安装目录或 KnownFolder 路径已变化，请先保留旧同步记录并确认迁移；未使用旧基线覆盖存档。");
        AutoBaseline? confirmed = null;
        try
        {
            await using (var rpc = await connect(context, token))
            {
                if (!Path.GetFullPath(rpc.RemoteRoot).Equals(Path.Combine(nativeRoot, "remote"), StringComparison.OrdinalIgnoreCase) || !rpc.CloudEnabled)
                    throw new IOException("云接口账号路径不匹配或云存档已关闭。");
                if (!Same(start, await Scan(plan, token)))
                    throw new IOException("原生接口初始化后真实存档已变化，已保留初始化前备份并停止同步。");
                var api = files?.Invoke(context.AppId, rpc) ?? new CloudTransfer(context.AppId, rpc, autoCloud: plan);
                var batchFile = Path.Combine(home, "autocloud-unfinished-batch.json");
                if (File.Exists(batchFile))
                {
                    var batch = KettleJson.Deserialize<ulong>(await File.ReadAllTextAsync(batchFile, token));
                    if (batch != 0)
                        await api.CloseBatchAsync(batch, false, token);
                    File.Delete(batchFile);
                }

                var remote = await api.ListAsync(token);
                var remoteMap = Remote(plan, remote);
                var version = Digest(new AutoTicket(start.OrderBy(p => p.Key).Select(p => new LocalHash(p.Key, p.Value.File.Sha1)).ToArray(), remote.Files.OrderBy(f => f.Name).ToArray(), remote.ChangeNumber));
                await CloudSyncService.AtomicJson(Path.Combine(backup, "local-cloud-difference.json"), new AutoDifference(version, start, remote, baseline), token);
                bool stale = choice != ConflictChoice.None && ticket != version;
                if (stale)
                    choice = ConflictChoice.None;
                var downloads = new List<CloudFile>();
                var uploads = new List<CloudUpload>();
                var conflicts = new List<CloudConflictFile>();
                var names = start.Keys.Union(remoteMap.Keys, StringComparer.OrdinalIgnoreCase).Union(baseline?.Hashes.Keys.AsEnumerable() ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var l = start.GetValueOrDefault(name)?.File;
                    var r = remoteMap.GetValueOrDefault(name);
                    var previous = baseline?.Hashes.GetValueOrDefault(name);
                    if (l is null && r is null && previous is not null)
                        throw new NotSupportedException("双方均缺失历史存档，删除同步尚未支持；未推进基线。请从备份恢复后重试。");
                    if (l?.Sha1 == r?.Sha1)
                        continue;
                    bool takeCloud = false, takeLocal = false;
                    if (!stale)
                    {
                        if (previous is not null && l is not null && r is not null)
                        {
                            takeCloud = l.Sha1 == previous;
                            takeLocal = r.Sha1 == previous;
                        }
                        else if (previous is null && l is null && r is not null)
                            takeCloud = true;
                        else if (previous is null && baseline is not null && l is not null && r is null)
                            takeLocal = true;
                        if (!takeCloud && !takeLocal)
                        {
                            takeCloud = choice == ConflictChoice.Cloud && r is not null;
                            takeLocal = choice == ConflictChoice.Local && l is not null;
                        }
                    }

                    if (takeCloud)
                        downloads.Add(r!);
                    else if (takeLocal)
                    {
                        var bytes = await AutoCloudDisk.Read(start[name].Path, token);
                        if (CloudTransfer.Hash(bytes) != l!.Sha1)
                            throw new IOException("上传准备时存档发生变化。");
                        uploads.Add(new(r?.Name ?? name, bytes, l.Timestamp, r?.Sha1, r?.Platforms ?? plan.PlatformsFor(AutoCloudWire.Key(name))));
                    }
                    else
                        conflicts.Add(new(name, l, r));
                }

                if (stale && conflicts.Count == 0)
                    throw new IOException("存档版本已变化，旧冲突选择未应用，请重新读取。");
                if (conflicts.Count > 0)
                {
                    var folder = Path.Combine(backup, "conflicts");
                    Directory.CreateDirectory(folder);
                    for (int i = 0; i < conflicts.Count; i++)
                    {
                        var conflict = conflicts[i];
                        if (conflict.Local is not null)
                            await File.WriteAllBytesAsync(Path.Combine(folder, i + ".local"), await AutoCloudDisk.Read(start[conflict.Name].Path, token), token);
                        if (conflict.Cloud is not null)
                            await StoreDownload(api, conflict.Cloud, Path.Combine(folder, i + ".cloud"), token);
                    }

                    await CloudSyncService.AtomicJson(Path.Combine(folder, "versions.json"), new AutoVersions(version, conflicts.ToArray(), backup), token);
                    throw new CloudConflictException((stale ? "双方版本已变化，旧选择未应用。" : "") + "AutoCloud 本地与云端版本不同，双方副本已保留。缺失文件不会传播删除；请选择保留的现存版本。", version, conflicts.ToArray(), folder);
                }

                // Preserve the exact cloud versions involved before any upload or local change.
                var cloudBefore = Path.Combine(backup, "cloud-before");
                Directory.CreateDirectory(cloudBefore);
                var preserved = downloads.Concat(uploads.Where(u => remoteMap.ContainsKey(u.Name)).Select(u => remoteMap[u.Name])).DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                for (int i = 0; i < preserved.Length; i++)
                    await StoreDownload(api, preserved[i], Path.Combine(cloudBefore, i + ".dat"), token);
                await CloudSyncService.AtomicJson(Path.Combine(cloudBefore, "manifest.json"), preserved.Select((f, i) => new CloudBackupEntry(i + ".dat", f)).ToArray(), token);
                if (!SameRemote(remote, await api.ListAsync(token)) || !Same(start, await Scan(plan, token)))
                    throw new IOException("传输准备期间双方版本变化，未覆盖存档，请重试。");
                await api.UploadAsync(uploads, async batch =>
                {
                    if (batch != 0)
                        await CloudSyncService.AtomicJson(batchFile, batch, CancellationToken.None);
                    else if (File.Exists(batchFile))
                        File.Delete(batchFile);
                }, token);
                var verified = await api.ListAsync(token);
                var verifiedMap = Remote(plan, verified);
                var expected = remoteMap.ToDictionary(p => p.Key, p => p.Value.Sha1, StringComparer.OrdinalIgnoreCase);
                foreach (var upload in uploads)
                    expected[upload.Name] = CloudTransfer.Hash(upload.Data);
                if (verified.ChangeNumber < remote.ChangeNumber || verifiedMap.Count != expected.Count || expected.Any(p => verifiedMap.GetValueOrDefault(p.Key)?.Sha1 != p.Value) || uploads.Any(u => verifiedMap.GetValueOrDefault(u.Name)?.Platforms != u.Platforms) || remoteMap.Any(p => !uploads.Any(u => u.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase)) && verifiedMap.GetValueOrDefault(p.Key)?.Platforms != p.Value.Platforms))
                    throw new IOException("提交后云端完整目录未确认或发生竞态，未推进基线。");
                if (!Same(start, await Scan(plan, token)))
                    throw new IOException("同步期间本地存档变化，未覆盖本地版本。");
                confirmed = new(signature, expected, verified.ChangeNumber);
                var items = downloads.Select(f => new AutoReplace(f.Name, start.GetValueOrDefault(f.Name)?.File.Sha1, f.Sha1, before.GetValueOrDefault(f.Name), Path.Combine(cloudBefore, Array.FindIndex(preserved, p => p.Name == f.Name) + ".dat"), start.GetValueOrDefault(f.Name)?.File.Timestamp ?? f.Timestamp, f.Timestamp, NewTemporary(plan, f.Name))).ToArray();
                // Recovery changed start, so capture originals for a new transaction from current files.
                if (items.Any(i => i.BeforeHash is not null && (i.BeforeFile is null || CloudTransfer.Hash(File.ReadAllBytes(i.BeforeFile)) != i.BeforeHash)))
                {
                    var currentBackup = await Backup(start, Path.Combine(backup, "after-recovery"), token);
                    items = items.Select(i => i with { BeforeFile = currentBackup.GetValueOrDefault(i.WireName) }).ToArray();
                }

                await CloudSyncService.AtomicJson(TransactionFile, new AutoTransaction(confirmed, items), token);
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    var destination = plan.ToLocal(AutoCloudWire.Key(item.WireName));
                    if (await HashAt(destination, token) != item.BeforeHash)
                        throw new IOException("写入前本地版本变化，停止覆盖。");
                    var bytes = await ReadOwned(item.AfterFile, item.AfterHash, token);
                    await AutoCloudDisk.Write(destination, bytes, item.AfterTime, token, item.TemporaryFile);
                }

                var end = await Scan(plan, token);
                if (!Matches(end, confirmed.Hashes) || !SameRemote(verified, await api.ListAsync(token)))
                    throw new IOException("同步末尾双方版本发生变化，未确认完成。");
            } // Shutdown must succeed before the application baseline advances.

            if (confirmed is null || !Matches(await Scan(plan, token), confirmed.Hashes))
                throw new IOException("原生接口退出后本地版本变化，未确认同步。");
            await CloudSyncService.AtomicJson(BaselineFile, confirmed, token);
            File.Delete(TransactionFile);
            await CloudSyncService.AtomicJson(Path.Combine(backup, "confirmed.json"), new AutoConfirmed(confirmed, false, false), token);
        }
        catch (Exception original)
        {
            // Roll back only values known to be ours. Concurrent game/user edits leave the
            // journal and original backups intact for a checked retry, never blind overwrite.
            try
            {
                await Recover(plan, CancellationToken.None);
            }
            catch (Exception recovery)
            {
                throw new IOException("同步未完成，部分本地事务仍待安全恢复；双方备份和记录已保留。" + original.Message, new AggregateException(original, recovery));
            }

            throw;
        }
    }

    private static Dictionary<string, CloudFile> Remote(AutoCloudPathPlan plan, CloudSnapshot snapshot)
    {
        if (snapshot.Files.Length > 256 || snapshot.Files.Sum(f => f.Size) > 256L * 1024 * 1024)
            throw new NotSupportedException("云目录超过支持范围。");
        plan.MapAll(snapshot.Files.Select(f => AutoCloudWire.Key(f.Name)));
        var result = new Dictionary<string, CloudFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in snapshot.Files)
        {
            if ((f.Platforms & 1) == 0 || f.Size < 0 || f.Size > CloudTransfer.MaxFileBytes || f.Timestamp < 0 || f.Timestamp > 253402300799 || f.Sha1.Length != 40 || !f.Sha1.All(Uri.IsHexDigit) || !result.TryAdd(f.Name, f))
                throw new InvalidDataException("AutoCloud 目录状态、平台或文件名无效。");
        }

        return result;
    }

    private static bool Same(Dictionary<string, AutoLocal> a, Dictionary<string, AutoLocal> b) => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key)?.File.Sha1 == p.Value.File.Sha1);
    private static bool Matches(Dictionary<string, AutoLocal> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value.File.Sha1);
    private static bool SameRemote(CloudSnapshot a, CloudSnapshot b) => a.ChangeNumber == b.ChangeNumber && Digest(a.Files.OrderBy(f => f.Name).ToArray()) == Digest(b.Files.OrderBy(f => f.Name).ToArray());
    private static async Task<Dictionary<string, AutoLocal>> Scan(AutoCloudPathPlan plan, CancellationToken token, HashSet<string>? scratch = null)
    {
        var result = new Dictionary<string, AutoLocal>(StringComparer.OrdinalIgnoreCase);
        int entries = 0;
        long total = 0;
        async Task Walk(AutoCloudCompiledRule rule, string directory)
        {
            token.ThrowIfCancellationRequested();
            using var lease = AutoCloudDisk.HoldDirectory(directory);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new IOException("AutoCloud 存档目录无效或是链接。");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 4096)
                    throw new NotSupportedException("存档目录条目过多。");
                attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("存档目录包含链接，未自动同步。");
                if (AutoCloudDisk.IsTemporary(path))
                {
                    if (scratch?.Contains(path) == true)
                        continue;
                    throw new IOException("存档目录存在未归属事务的临时文件，已停止同步，请保留文件后检查。");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (rule.Recursive)
                        await Walk(rule, path);
                    continue;
                }

                if (!AutoCloudPathSafety.Pattern(rule.Pattern).IsMatch(Path.GetFileName(path)))
                    continue;
                var key = plan.ToCloud(path);
                var name = AutoCloudWire.Name(key);
                var bytes = await AutoCloudDisk.Read(path, token);
                total += bytes.Length;
                if (result.Count >= 256 || total > 256L * 1024 * 1024 || !result.TryAdd(name, new(new(name, CloudTransfer.Hash(bytes), bytes.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds(), plan.PlatformsFor(key)), path)))
                    throw new InvalidDataException("存档映射重叠、重复或超出范围。");
            }
        }

        foreach (var rule in plan.Rules)
            await Walk(rule, rule.LocalDirectory);
        return result;
    }

    private static async Task<Dictionary<string, string>> Backup(Dictionary<string, AutoLocal> local, string folder, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (var p in local)
        {
            var bytes = await AutoCloudDisk.Read(p.Value.Path, token);
            if (CloudTransfer.Hash(bytes) != p.Value.File.Sha1)
                throw new IOException("备份期间存档变化。");
            var path = Path.Combine(folder, "local-" + i++ + ".dat");
            await File.WriteAllBytesAsync(path, bytes, token);
            if (CloudTransfer.Hash(await File.ReadAllBytesAsync(path, token)) != p.Value.File.Sha1)
                throw new IOException("本地备份校验失败。");
            result.Add(p.Key, path);
        }

        await CloudSyncService.AtomicJson(Path.Combine(folder, "local-manifest.json"), new AutoLocalManifest(local, result), token);
        return result;
    }

    private static async Task StoreDownload(ICloudFiles api, CloudFile file, string target, CancellationToken token)
    {
        var bytes = await api.DownloadAsync(file, token);
        if (bytes.LongLength != file.Size || CloudTransfer.Hash(bytes) != file.Sha1)
            throw new IOException("云副本校验失败。");
        await File.WriteAllBytesAsync(target, bytes, token);
        if (CloudTransfer.Hash(await File.ReadAllBytesAsync(target, token)) != file.Sha1)
            throw new IOException("云备份落盘校验失败。");
    }

    private static async Task<string?> HashAt(string path, CancellationToken token)
    {
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        return CloudTransfer.Hash(await AutoCloudDisk.Read(path, token));
    }

    private async Task<byte[]> ReadOwned(string path, string hash, CancellationToken token)
    {
        var canonical = Path.GetFullPath(path);
        if (!canonical.StartsWith(Path.GetFullPath(home) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("恢复文件超出同步目录。");
        var bytes = await AutoCloudDisk.Read(canonical, token);
        if (CloudTransfer.Hash(bytes) != hash)
            throw new IOException("恢复副本哈希不符。");
        return bytes;
    }

    private async Task Recover(AutoCloudPathPlan plan, CancellationToken token)
    {
        if (!File.Exists(TransactionFile))
            return;
        var transaction = KettleJson.Deserialize<AutoTransaction>(await File.ReadAllTextAsync(TransactionFile, token)) ?? throw new IOException("存档事务记录无效。");
        if (transaction.Files.Length > 256)
            throw new IOException("存档事务记录超出范围。");
        transaction = transaction with
        {
            Files = transaction.Files.Select(i => i with { TemporaryFile = i.TemporaryFile ?? NewTemporary(plan, i.WireName) }).ToArray()
        };
        await CloudSyncService.AtomicJson(TransactionFile, transaction, token);
        await JournalTemps(plan, token); // Validate exact temporary identities before touching them.
        foreach (var item in transaction.Files)
        {
            if (!File.Exists(item.TemporaryFile))
                continue;
            var bytes = await AutoCloudDisk.Read(item.TemporaryFile!, token);
            var preserved = Path.Combine(home, "recovered-scratch", Guid.NewGuid().ToString("N") + ".dat");
            Directory.CreateDirectory(Path.GetDirectoryName(preserved)!);
            await File.WriteAllBytesAsync(preserved, bytes, token);
            if (CloudTransfer.Hash(await File.ReadAllBytesAsync(preserved, token)) != CloudTransfer.Hash(bytes))
                throw new IOException("事务临时文件保留失败。");
            AutoCloudDisk.DeleteRollbackFile(item.TemporaryFile!);
        }

        if (File.Exists(BaselineFile) && Digest(KettleJson.Deserialize<AutoBaseline>(await File.ReadAllTextAsync(BaselineFile, token))) == Digest(transaction.ExpectedBaseline) && transaction.Files.All(i => File.Exists(plan.ToLocal(AutoCloudWire.Key(i.WireName)))))
        {
            bool complete = true;
            foreach (var item in transaction.Files)
                complete &= await HashAt(plan.ToLocal(AutoCloudWire.Key(item.WireName)), token) == item.AfterHash;
            if (complete)
            {
                File.Delete(TransactionFile);
                return;
            }
        }

        // Preflight every target before rolling any back, so unrelated edits never get overwritten.
        foreach (var item in transaction.Files)
        {
            var current = await HashAt(plan.ToLocal(AutoCloudWire.Key(item.WireName)), token);
            if (current != item.BeforeHash && current != item.AfterHash)
                throw new IOException("中断后存档被其他程序修改；已保留事务和双方备份，请先处理后重试。");
        }

        foreach (var item in transaction.Files.Reverse())
        {
            var target = plan.ToLocal(AutoCloudWire.Key(item.WireName));
            var current = await HashAt(target, token);
            if (current == item.BeforeHash)
                continue;
            if (current != item.AfterHash)
                throw new IOException("回滚期间存档变化，已停止。");
            if (item.BeforeHash is null)
                AutoCloudDisk.DeleteRollbackFile(target);
            else
                await AutoCloudDisk.Write(target, await ReadOwned(item.BeforeFile ?? throw new IOException("缺少原始副本。"), item.BeforeHash, token), item.BeforeTime, token, item.TemporaryFile);
        }

        File.Delete(TransactionFile);
    }

    private static string NewTemporary(AutoCloudPathPlan plan, string wire) => Path.Combine(Path.GetDirectoryName(plan.ToLocal(AutoCloudWire.Key(wire)))!, ".kettle-" + Guid.NewGuid().ToString("N") + ".tmp");
    private async Task<HashSet<string>> JournalTemps(AutoCloudPathPlan plan, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(TransactionFile))
            return result;
        var transaction = KettleJson.Deserialize<AutoTransaction>(await File.ReadAllTextAsync(TransactionFile, token)) ?? throw new IOException("事务记录无效。");
        if (transaction.Files.Length > 256)
            throw new IOException("事务记录过大。");
        foreach (var item in transaction.Files)
        {
            if (item.TemporaryFile is null)
                continue;
            if (!AutoCloudDisk.IsTemporary(item.TemporaryFile) || Path.GetDirectoryName(AutoCloudPathSafety.Absolute(item.TemporaryFile)) != Path.GetDirectoryName(plan.ToLocal(AutoCloudWire.Key(item.WireName))) || !result.Add(item.TemporaryFile))
                throw new IOException("事务临时路径不匹配。");
        }

        return result;
    }
}
