using System.Security.Cryptography;
using System.Text.Json;

namespace Kettle.Core;

internal sealed record AutoLocal(CloudFile File, string Path);
internal sealed record AutoBaseline(string Plan, Dictionary<string, string> Hashes, ulong ChangeNumber);
internal sealed record AutoReplace(string WireName, string? BeforeHash, string? AfterHash, string? BeforeFile, string? AfterFile, long BeforeTime, long AfterTime, string? TemporaryFile = null);
internal sealed record AutoTransaction(AutoBaseline ExpectedBaseline, AutoReplace[] Files);
/// <summary>AutoCloud uses KnownFolders and full wire names. No synthetic native index.</summary>
internal sealed class AutoCloudSync(string home, Func<CloudContext, CancellationToken, Task<ICloudRpc>> connect, Func<uint, ICloudRpc, ICloudFiles>? files, IAutoCloudRoots roots, IBackupDelete? deletion = null)
{
    private string BaselineFile => Path.Combine(home, "autocloud-baseline.json");
    private string TransactionFile => Path.Combine(home, "autocloud-local-transaction.json");

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(KettleJson.Serialize(value))));
    public async Task SyncAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
    {
        if (context.Metadata.AutoCloudAppInfo is null)
            throw new NotSupportedException(L.Text(TextId.AutoCloudRulesAreMissingReadTheGameInformationAgain));
        var definition = AutoCloudDefinition.Parse(context.AppId, context.Metadata.AutoCloudAppInfo);
        var plan = AutoCloudPathPlan.Compile(definition, new(context.AccountId, 76561197960265728ul + context.AccountId), AutoCloudPlatform.Windows, roots, context.InstallPath);
        if (plan.Rules.Count == 0)
            throw new NotSupportedException(L.Text(TextId.NoAutoCloudRulesApplyToThisPlatform));
        var signature = Digest(plan.Rules);
        Directory.CreateDirectory(home);
        using var retention = new CloudBackupRetention(home, deletion);
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
            throw new IOException(L.Text(TextId.SavesChangedDuringBackupTheCloudInterfaceWasNot));
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
            throw new IOException(L.Text(TextId.AutoCloudRulesInstallDirectoryOrKnownFolderPathsChangedPreserve));
        AutoBaseline? confirmed = null;
        try
        {
            await using (var rpc = await connect(context, token))
            {
                if (!Path.GetFullPath(rpc.RemoteRoot).Equals(Path.Combine(nativeRoot, "remote"), StringComparison.OrdinalIgnoreCase) || !rpc.CloudEnabled)
                    throw new IOException(L.Text(TextId.CloudInterfaceAccountPathMismatchOrCloudSavesAre));
                if (!Same(start, await Scan(plan, token)))
                    throw new IOException(L.Text(TextId.RealSavesChangedAfterNativeInterfaceInitializationThePre));
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
                var localDeletes = new List<string>();
                var cloudDeletes = new List<CloudDelete>();
                var names = start.Keys.Union(remoteMap.Keys, StringComparer.OrdinalIgnoreCase).Union(baseline?.Hashes.Keys.AsEnumerable() ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var l = start.GetValueOrDefault(name)?.File;
                    var r = remoteMap.GetValueOrDefault(name);
                    var previous = baseline?.Hashes.GetValueOrDefault(name);
                    if (l is null && r is null) continue;
                    if (!stale && r is null && l is not null && (choice == ConflictChoice.Cloud || previous == l.Sha1 && remote.DeletedNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
                    {
                        localDeletes.Add(name);
                        continue;
                    }
                    if (!stale && l is null && r is not null && (choice == ConflictChoice.Local || previous == r.Sha1))
                    {
                        cloudDeletes.Add(new(r.Name, r.Sha1));
                        continue;
                    }
                    if (l?.Sha1 == r?.Sha1 && r?.ReuploadRequested != true)
                        continue;
                    bool takeCloud = false, takeLocal = l is not null && l.Sha1 == r?.Sha1 && r.ReuploadRequested;
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

                    if (!stale && l is not null && l.Sha1 == r?.Sha1 && r.ReuploadRequested) { takeCloud = false; takeLocal = true; }
                    if (takeCloud)
                        downloads.Add(r!);
                    else if (takeLocal)
                    {
                        var bytes = await AutoCloudDisk.Read(start[name].Path, token);
                        if (CloudTransfer.Hash(bytes) != l!.Sha1)
                            throw new IOException(L.Text(TextId.SavesChangedWhilePreparingTheUpload));
                        var upload = new CloudUpload(r?.Name ?? name, bytes, l.Timestamp, r?.Sha1, r?.Platforms ?? plan.PlatformsFor(AutoCloudWire.Key(name)));
                        uploads.Add(api is CloudTransfer ? await CloudUpload.Stage(upload, Path.Combine(backup, "uploads"), token) : upload);
                    }
                    else
                        conflicts.Add(new(name, l, r));
                }

                if (stale && conflicts.Count == 0)
                    throw new IOException(L.Text(TextId.SaveVersionsChangedThePreviousConflictChoiceWasNot));
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
                    throw new CloudConflictException((stale ? L.Text(TextId.BothVersionsChangedThePreviousChoiceWasNotApplied) : "") + L.Text(TextId.AutoCloudLocalAndCloudVersionsDifferBothCopiesAre), version, conflicts.ToArray(), folder);
                }

                // Preserve the exact cloud versions involved before any upload or local change.
                var cloudBefore = Path.Combine(backup, "cloud-before");
                Directory.CreateDirectory(cloudBefore);
                var preserved = downloads.Concat(uploads.Where(u => remoteMap.ContainsKey(u.Name)).Select(u => remoteMap[u.Name])).Concat(cloudDeletes.Select(d => remoteMap[d.Name])).DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                for (int i = 0; i < preserved.Length; i++)
                    await StoreDownload(api, preserved[i], Path.Combine(cloudBefore, i + ".dat"), token);
                await CloudSyncService.AtomicJson(Path.Combine(cloudBefore, "manifest.json"), preserved.Select((f, i) => new CloudBackupEntry(i + ".dat", f)).ToArray(), token);
                if (!SameRemote(remote, await api.ListAsync(token)) || !Same(start, await Scan(plan, token)))
                    throw new IOException(L.Text(TextId.BothVersionsChangedWhilePreparingTheTransferSavesWere));
                async Task SaveBatch(ulong batch)
                {
                    if (batch != 0)
                        await CloudSyncService.AtomicJson(batchFile, batch, CancellationToken.None);
                    else if (File.Exists(batchFile))
                        File.Delete(batchFile);
                }
                await api.UploadAsync(uploads, SaveBatch, token);
                await api.DeleteAsync(cloudDeletes, SaveBatch, token);
                var verified = await api.ListAsync(token);
                var verifiedMap = Remote(plan, verified);
                var expected = remoteMap.ToDictionary(p => p.Key, p => p.Value.Sha1, StringComparer.OrdinalIgnoreCase);
                foreach (var upload in uploads)
                    expected[upload.Name] = upload.Hash;
                foreach (var file in cloudDeletes) expected.Remove(file.Name);
                if (verified.ChangeNumber < remote.ChangeNumber || verifiedMap.Count != expected.Count || expected.Any(p => verifiedMap.GetValueOrDefault(p.Key)?.Sha1 != p.Value) || uploads.Any(u => verifiedMap.GetValueOrDefault(u.Name)?.Platforms != u.Platforms) || remoteMap.Any(p => expected.ContainsKey(p.Key) && !uploads.Any(u => u.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase)) && verifiedMap.GetValueOrDefault(p.Key)?.Platforms != p.Value.Platforms))
                    throw new IOException(L.Text(TextId.TheFullCloudListingWasNotConfirmedAfterCommit));
                if (!Same(start, await Scan(plan, token)))
                    throw new IOException(L.Text(TextId.LocalSavesChangedDuringSyncTheLocalVersionWas));
                confirmed = new(signature, expected, verified.ChangeNumber);
                var items = downloads.Select(f => new AutoReplace(f.Name, start.GetValueOrDefault(f.Name)?.File.Sha1, f.Sha1, before.GetValueOrDefault(f.Name), Path.Combine(cloudBefore, Array.FindIndex(preserved, p => p.Name == f.Name) + ".dat"), start.GetValueOrDefault(f.Name)?.File.Timestamp ?? f.Timestamp, f.Timestamp, NewTemporary(plan, f.Name))).ToArray();
                items = items.Concat(localDeletes.Select(name => new AutoReplace(name, start[name].File.Sha1, null, before.GetValueOrDefault(name), null, start[name].File.Timestamp, 0, NewTemporary(plan, name)))).ToArray();
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
                        throw new IOException(L.Text(TextId.TheLocalVersionChangedBeforeWritingOverwriteStopped));
                    if (item.AfterHash is null) AutoCloudDisk.DeleteRollbackFile(destination);
                    else
                    {
                        var bytes = await ReadOwned(item.AfterFile!, item.AfterHash, token);
                        await AutoCloudDisk.Write(destination, bytes, item.AfterTime, token, item.TemporaryFile);
                    }
                }

                var end = await Scan(plan, token);
                if (!Matches(end, confirmed.Hashes) || !SameRemote(verified, await api.ListAsync(token)))
                    throw new IOException(L.Text(TextId.BothVersionsChangedAtTheEndOfSyncCompletion));
            } // Shutdown must succeed before the application baseline advances.

            if (confirmed is null || !Matches(await Scan(plan, token), confirmed.Hashes))
                throw new IOException(L.Text(TextId.TheLocalVersionChangedAfterTheNativeInterfaceExited));
            await CloudSyncService.AtomicJson(BaselineFile, confirmed, token);
            File.Delete(TransactionFile);
            await CloudSyncService.AtomicJson(Path.Combine(backup, "confirmed.json"), new AutoConfirmed(confirmed, false, true), token);
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
                throw new IOException(L.Text(TextId.SyncIsIncompleteSomeLocalTransactionsStillRequireSafe) + original.Message, new AggregateException(original, recovery));
            }

            throw;
        }
    }

    private static Dictionary<string, CloudFile> Remote(AutoCloudPathPlan plan, CloudSnapshot snapshot)
    {
        if (snapshot.Files.Length > 4096 || snapshot.Files.Sum(f => f.Size) > 4096L * 1024 * 1024)
            throw new NotSupportedException(L.Text(TextId.CloudDirectoryExceedsSupportedLimits));
        plan.MapAll(snapshot.Files.Select(f => AutoCloudWire.Key(f.Name)));
        var result = new Dictionary<string, CloudFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in snapshot.Files)
        {
            if ((f.Platforms & 1) == 0 || f.Size < 0 || f.Size > CloudTransfer.MaxFileBytes || f.Timestamp < 0 || f.Timestamp > 253402300799 || f.Sha1.Length != 40 || !f.Sha1.All(Uri.IsHexDigit) || !result.TryAdd(f.Name, f))
                throw new InvalidDataException(L.Text(TextId.InvalidAutoCloudDirectoryStatePlatformOrFilename));
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
                throw new IOException(L.Text(TextId.AutoCloudSaveDirectoryIsInvalidOrIsALink));
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 4096)
                    throw new NotSupportedException(L.Text(TextId.TooManyEntriesInTheSaveDirectory));
                attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(L.Text(TextId.TheSaveDirectoryContainsLinksAutomaticSyncWasNot));
                if (AutoCloudDisk.IsTemporary(path))
                {
                    if (scratch?.Contains(path) == true)
                        continue;
                    throw new IOException(L.Text(TextId.TheSaveDirectoryContainsTemporaryFilesWithNoMatching));
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
                if (result.Count >= 4096 || total > 4096L * 1024 * 1024 || !result.TryAdd(name, new(new(name, CloudTransfer.Hash(bytes), bytes.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds(), plan.PlatformsFor(key)), path)))
                    throw new InvalidDataException(L.Text(TextId.SaveMappingsOverlapAreDuplicatedOrExceedSupportedLimits));
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
                throw new IOException(L.Text(TextId.SavesChangedDuringBackup));
            var path = Path.Combine(folder, "local-" + i++ + ".dat");
            await File.WriteAllBytesAsync(path, bytes, token);
            if (CloudTransfer.Hash(await File.ReadAllBytesAsync(path, token)) != p.Value.File.Sha1)
                throw new IOException(L.Text(TextId.LocalBackupVerificationFailed));
            result.Add(p.Key, path);
        }

        await CloudSyncService.AtomicJson(Path.Combine(folder, "local-manifest.json"), new AutoLocalManifest(local, result), token);
        return result;
    }

    private static async Task StoreDownload(ICloudFiles api, CloudFile file, string target, CancellationToken token)
    {
        var bytes = await api.DownloadAsync(file, token);
        if (bytes.LongLength != file.Size || CloudTransfer.Hash(bytes) != file.Sha1)
            throw new IOException(L.Text(TextId.CloudCopyVerificationFailed));
        await File.WriteAllBytesAsync(target, bytes, token);
        if (CloudTransfer.Hash(await File.ReadAllBytesAsync(target, token)) != file.Sha1)
            throw new IOException(L.Text(TextId.CloudBackupVerificationOnDiskFailed));
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
            throw new IOException(L.Text(TextId.RecoveryFileIsOutsideTheSyncDirectory));
        var bytes = await AutoCloudDisk.Read(canonical, token);
        if (CloudTransfer.Hash(bytes) != hash)
            throw new IOException(L.Text(TextId.RecoveryCopyHashMismatch));
        return bytes;
    }

    private async Task Recover(AutoCloudPathPlan plan, CancellationToken token)
    {
        if (!File.Exists(TransactionFile))
            return;
        var transaction = KettleJson.Deserialize<AutoTransaction>(await File.ReadAllTextAsync(TransactionFile, token)) ?? throw new IOException(L.Text(TextId.InvalidSaveTransactionRecord));
        if (transaction.Files.Length > 4096)
            throw new IOException(L.Text(TextId.SaveTransactionRecordExceedsSupportedLimits));
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
                throw new IOException(L.Text(TextId.CouldNotPreserveTransactionTemporaryFiles));
            AutoCloudDisk.DeleteRollbackFile(item.TemporaryFile!);
        }

        if (File.Exists(BaselineFile) && Digest(KettleJson.Deserialize<AutoBaseline>(await File.ReadAllTextAsync(BaselineFile, token))) == Digest(transaction.ExpectedBaseline))
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
                throw new IOException(L.Text(TextId.AnotherProgramModifiedSavesAfterInterruptionTransactionsAndBoth));
        }

        foreach (var item in transaction.Files.Reverse())
        {
            var target = plan.ToLocal(AutoCloudWire.Key(item.WireName));
            var current = await HashAt(target, token);
            if (current == item.BeforeHash)
                continue;
            if (current != item.AfterHash)
                throw new IOException(L.Text(TextId.SavesChangedDuringRollbackRollbackStopped));
            if (item.BeforeHash is null)
                AutoCloudDisk.DeleteRollbackFile(target);
            else
                await AutoCloudDisk.Write(target, await ReadOwned(item.BeforeFile ?? throw new IOException(L.Text(TextId.TheOriginalCopyIsMissing)), item.BeforeHash, token), item.BeforeTime, token, item.TemporaryFile);
        }

        File.Delete(TransactionFile);
    }

    private static string NewTemporary(AutoCloudPathPlan plan, string wire) => Path.Combine(Path.GetDirectoryName(plan.ToLocal(AutoCloudWire.Key(wire)))!, ".kettle-" + Guid.NewGuid().ToString("N") + ".tmp");
    private async Task<HashSet<string>> JournalTemps(AutoCloudPathPlan plan, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(TransactionFile))
            return result;
        var transaction = KettleJson.Deserialize<AutoTransaction>(await File.ReadAllTextAsync(TransactionFile, token)) ?? throw new IOException(L.Text(TextId.InvalidTransactionRecord));
        if (transaction.Files.Length > 4096)
            throw new IOException(L.Text(TextId.TransactionRecordIsTooLarge));
        foreach (var item in transaction.Files)
        {
            if (item.TemporaryFile is null)
                continue;
            if (!AutoCloudDisk.IsTemporary(item.TemporaryFile) || Path.GetDirectoryName(AutoCloudPathSafety.Absolute(item.TemporaryFile)) != Path.GetDirectoryName(plan.ToLocal(AutoCloudWire.Key(item.WireName))) || !result.Add(item.TemporaryFile))
                throw new IOException(L.Text(TextId.TransactionTemporaryPathMismatch));
        }

        return result;
    }
}
