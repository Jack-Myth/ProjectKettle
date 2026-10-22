using System.Security.Cryptography;
using System.Text.Json;

namespace Kettle.Core;

internal sealed record AutoLocal(CloudFile File, string Path);
internal sealed record AutoBaseline(string Plan, Dictionary<string, string> Hashes, ulong ChangeNumber)
{
    public string[]? KnownFiles { get; init; }
}
internal sealed record AutoReplace(string WireName, string? BeforeHash, string? AfterHash, string? BeforeFile, string? AfterFile, long BeforeTime, long AfterTime, string? TemporaryFile = null);
internal sealed record AutoTransaction(AutoBaseline ExpectedBaseline, AutoReplace[] Files);
/// <summary>AutoCloud uses KnownFolders and full wire names. No synthetic native index.</summary>
internal sealed class AutoCloudSync(string home, Func<CloudContext, CancellationToken, Task<ICloudRpc>> connect, Func<uint, ICloudRpc, ICloudFiles>? files, IAutoCloudRoots roots, IBackupDelete? deletion = null)
{
    private string BaselineFile => Path.Combine(home, "autocloud-baseline.json");
    private string TransactionFile => Path.Combine(home, "autocloud-local-transaction.json");

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(KettleJson.Serialize(value))));
    public async Task<CloudRunSnapshot> SyncAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token, bool uploadChanges, CloudRunSnapshot? run = null)
    {
        DebugTrace.Write($"autocloud begin app={context.AppId} postGame={uploadChanges}");
        run = CloudSaveSafety.Normalize(run);
        if (context.Metadata.AutoCloudAppInfo is null)
            throw new NotSupportedException(L.Text(TextId.AutoCloudRulesAreMissingReadTheGameInformationAgain));
        DebugTrace.Write("autocloud definition parse begin");
        var definition = AutoCloudDefinition.Parse(context.AppId, context.Metadata.AutoCloudAppInfo);
        DebugTrace.Write("autocloud path compile begin");
        var plan = AutoCloudPathPlan.Compile(definition, new(context.AccountId, 76561197960265728ul + context.AccountId), AutoCloudPlatform.Windows, roots, context.InstallPath);
        if (plan.Rules.Count == 0)
            throw new NotSupportedException(L.Text(TextId.NoAutoCloudRulesApplyToThisPlatform));
        DebugTrace.Write($"autocloud path compile complete rules={plan.Rules.Count}");
        var signature = Digest(plan.Rules);
        Directory.CreateDirectory(home);
        using var retention = new CloudBackupRetention(home, deletion);
        DebugTrace.Write("autocloud retention begin");
        retention.BeforeCreate();
        DebugTrace.Write("autocloud retention complete; sync lock begin");
        await using var syncLock = new FileStream(Path.Combine(home, "autocloud-sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        DebugTrace.Write("autocloud sync lock acquired; journal read begin");
        AutoBaseline? baseline = File.Exists(BaselineFile) ? KettleJson.Deserialize<AutoBaseline>(await File.ReadAllTextAsync(BaselineFile, token)) : null;
        if (baseline is not null)
            baseline = baseline with { Hashes = new(baseline.Hashes, StringComparer.OrdinalIgnoreCase) };
        if (baseline is not null && baseline.Plan != signature)
            throw new IOException(L.Text(TextId.AutoCloudRulesInstallDirectoryOrKnownFolderPathsChangedPreserve));
        var trackedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (baseline is not null)
        {
            trackedNames.UnionWith(baseline.Hashes.Keys);
            trackedNames.UnionWith(baseline.KnownFiles ?? []);
        }
        if (run is not null)
        {
            trackedNames.UnionWith(run.Local.Keys);
            trackedNames.UnionWith(run.Cloud.Keys);
        }
        var scratch = await JournalTemps(plan, trackedNames, token);
        DebugTrace.Write("autocloud initial scan begin");
        var start = await Scan(plan, token, scratch, trackedNames);
        DebugTrace.Write($"autocloud initial scan complete files={start.Count}");
        var backup = Path.Combine(home, "autocloud-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        DebugTrace.Write("autocloud backup create " + backup);
        Directory.CreateDirectory(backup);
        CloudBackupRetention.MarkPending(backup);
        var before = await Backup(start, backup, token);
        // Save payloads and the potentially rewritten native index before SteamAPI.Init.
        DebugTrace.Write("autocloud local backup complete; native backup begin");
        var nativeRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(context.SteamCmd)!, "userdata", context.AccountId.ToString(), context.AppId.ToString()));
        var nativeBackup = await SaveBackup.CreateAsync(nativeRoot, Path.Combine(backup, "native-index-before"), token);
        await CloudSyncService.AtomicJson(Path.Combine(backup, "before-init.json"), new AutoBeforeInit(context.AppId, context.AccountId, plan.Rules, start, before, nativeBackup), token);
        if (!Same(start, await Scan(plan, token, scratch, trackedNames)))
            throw new IOException(L.Text(TextId.SavesChangedDuringBackupTheCloudInterfaceWasNot));
        DebugTrace.Write("autocloud backup verified; retention commit begin");
        retention.Commit(backup);
        DebugTrace.Write("autocloud retention commit complete; recovery marker begin");
        await CloudSaveSafety.PreserveFailedAttempt(home, backup, retention, token);
        DebugTrace.Write("autocloud recovery begin");
        await Recover(plan, token); // May roll back this app's interrupted local replacements, never game edits.
        DebugTrace.Write("autocloud recovery complete; rescan begin");
        start = await Scan(plan, token, tracked: trackedNames);
        AutoBaseline? confirmed = null;
        Dictionary<string, string>? expectedLocal = null;
        try
        {
            DebugTrace.Write("autocloud connect begin");
            await using (var rpc = await connect(context, token))
            {
                DebugTrace.Write("autocloud connect complete");
                if (!Path.GetFullPath(rpc.RemoteRoot).Equals(Path.Combine(nativeRoot, "remote"), StringComparison.OrdinalIgnoreCase) || !rpc.CloudEnabled)
                    throw new IOException(L.Text(TextId.CloudInterfaceAccountPathMismatchOrCloudSavesAre));
                if (!Same(start, await Scan(plan, token, tracked: trackedNames)))
                    throw new IOException(L.Text(TextId.RealSavesChangedAfterNativeInterfaceInitializationThePre));
                var api = files?.Invoke(context.AppId, rpc) ?? new CloudTransfer(context.AppId, rpc, autoCloud: plan);
                var batchFile = Path.Combine(home, "autocloud-unfinished-batch.json");
                if (uploadChanges && File.Exists(batchFile))
                {
                    var batch = KettleJson.Deserialize<ulong>(await File.ReadAllTextAsync(batchFile, token));
                    if (batch != 0)
                        await api.CloseBatchAsync(batch, false, token);
                    File.Delete(batchFile);
                }

                var remote = await api.ListAsync(token);
                var remoteMap = Remote(plan, remote);
                // Cloud names remain tracked even when today's discovery mask does
                // not select them (or this filesystem has no 8.3 aliases).
                trackedNames.UnionWith(remoteMap.Keys);
                var expanded = await Scan(plan, token, tracked: trackedNames);
                if (start.Any(p => expanded.GetValueOrDefault(p.Key)?.File.Sha1 != p.Value.File.Sha1))
                    throw new IOException(L.Text(TextId.RealSavesChangedAfterNativeInterfaceInitializationThePre));
                var additional = expanded.Where(p => !start.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                if (additional.Count > 0)
                    foreach (var copy in await Backup(additional, Path.Combine(backup, "cloud-targets-before"), token))
                        before[copy.Key] = copy.Value;
                start = expanded;
                CloudSaveSafety.RequireHistoricalCopies(CloudSaveSafety.RequiredHistory(baseline?.KnownFiles?.AsEnumerable() ?? baseline?.Hashes.Keys, run, uploadChanges), start.Keys, remoteMap.Keys);
                var version = Digest(new AutoTicket(start.OrderBy(p => p.Key).Select(p => new LocalHash(p.Key, p.Value.File.Sha1)).ToArray(), remote.Files.OrderBy(f => f.Name).ToArray(), remote.ChangeNumber));
                await CloudSyncService.AtomicJson(Path.Combine(backup, "local-cloud-difference.json"), new AutoDifference(version, start, remote, baseline), token);
                bool stale = choice != ConflictChoice.None && ticket != version;
                if (stale)
                    choice = ConflictChoice.None;
                var downloads = new List<CloudFile>();
                var uploads = new List<CloudUpload>();
                var deletes = new List<CloudDelete>();
                var conflicts = new List<CloudConflictFile>();
                var names = start.Keys.Union(remoteMap.Keys, StringComparer.OrdinalIgnoreCase).Union(baseline?.Hashes.Keys.AsEnumerable() ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var l = start.GetValueOrDefault(name)?.File;
                    var r = remoteMap.GetValueOrDefault(name);
                    var previous = uploadChanges && run is not null ? run.Cloud.GetValueOrDefault(name) : baseline?.Hashes.GetValueOrDefault(name);
                    var action = CloudSaveSafety.Decide(uploadChanges, name, l?.Sha1, r?.Sha1, previous, r?.ReuploadRequested == true, choice, stale, run);
                    if (action == CloudSaveSafety.Action.None) continue;
                    if (action == CloudSaveSafety.Action.Delete) { deletes.Add(new(r!.Name, r.Sha1)); continue; }
                    if (action == CloudSaveSafety.Action.Download)
                        downloads.Add(r!);
                    else if (action == CloudSaveSafety.Action.Upload)
                    {
                        var bytes = await AutoCloudDisk.Read(start[name].Path, token);
                        if (CloudTransfer.Hash(bytes) != l!.Sha1)
                            throw new IOException(L.Text(TextId.SavesChangedWhilePreparingTheUpload));
                        var upload = new CloudUpload(r?.Name ?? name, bytes, l.Timestamp, r?.Sha1, r?.Platforms ?? l.Platforms);
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
                var preserved = downloads.Concat(uploads.Where(u => uploadChanges && remoteMap.ContainsKey(u.Name)).Select(u => remoteMap[u.Name])).Concat(deletes.Select(d => remoteMap[d.Name])).DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                for (int i = 0; i < preserved.Length; i++)
                    await StoreDownload(api, preserved[i], Path.Combine(cloudBefore, i + ".dat"), token);
                await CloudSyncService.AtomicJson(Path.Combine(cloudBefore, "manifest.json"), preserved.Select((f, i) => new CloudBackupEntry(i + ".dat", f)).ToArray(), token);
                if (!SameRemote(remote, await api.ListAsync(token)) || !Same(start, await Scan(plan, token, tracked: trackedNames)))
                    throw new IOException(L.Text(TextId.BothVersionsChangedWhilePreparingTheTransferSavesWere));
                async Task SaveBatch(ulong batch)
                {
                    if (batch != 0)
                        await CloudSyncService.AtomicJson(batchFile, batch, CancellationToken.None);
                    else if (File.Exists(batchFile))
                        File.Delete(batchFile);
                }
                // A successful launch preflight is read-only with respect to the cloud.
                // Preserve local edits, but upload them only after a confirmed game exit.
                if (uploadChanges)
                {
                    await api.UploadAsync(uploads, SaveBatch, token);
                    if (deletes.Count > 0)
                    {
                        if (!Same(start, await Scan(plan, token, tracked: trackedNames)))
                            throw new IOException(L.Text(TextId.LocalSavesChangedDuringSyncTheLocalVersionWas));
                        await api.DeleteAsync(deletes, SaveBatch, token);
                    }
                }
                var verified = await api.ListAsync(token);
                var verifiedMap = Remote(plan, verified);
                var expected = remoteMap.ToDictionary(p => p.Key, p => p.Value.Sha1, StringComparer.OrdinalIgnoreCase);
                if (uploadChanges)
                    foreach (var upload in uploads)
                        expected[upload.Name] = upload.Hash;
                foreach (var deleted in deletes) expected.Remove(deleted.Name);
                if (verified.ChangeNumber < remote.ChangeNumber || verifiedMap.Count != expected.Count || expected.Any(p => verifiedMap.GetValueOrDefault(p.Key)?.Sha1 != p.Value) || uploadChanges && uploads.Any(u => verifiedMap.GetValueOrDefault(u.Name)?.Platforms != u.Platforms) || remoteMap.Any(p => expected.ContainsKey(p.Key) && (!uploadChanges || !uploads.Any(u => u.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase))) && verifiedMap.GetValueOrDefault(p.Key)?.Platforms != p.Value.Platforms))
                    throw new IOException(L.Text(TextId.TheFullCloudListingWasNotConfirmedAfterCommit));
                if (!Same(start, await Scan(plan, token, tracked: trackedNames)))
                    throw new IOException(L.Text(TextId.LocalSavesChangedDuringSyncTheLocalVersionWas));
                expectedLocal = start.ToDictionary(p => p.Key, p => p.Value.File.Sha1, StringComparer.OrdinalIgnoreCase);
                foreach (var file in downloads) expectedLocal[file.Name] = file.Sha1;
                confirmed = new(signature, expected, verified.ChangeNumber) { KnownFiles = expectedLocal.Keys.ToArray() };
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
                        throw new IOException(L.Text(TextId.TheLocalVersionChangedBeforeWritingOverwriteStopped));
                    if (item.AfterHash is null) AutoCloudDisk.DeleteRollbackFile(destination);
                    else
                    {
                        var bytes = await ReadOwned(item.AfterFile!, item.AfterHash, token);
                        await AutoCloudDisk.Write(destination, bytes, item.AfterTime, token, item.TemporaryFile);
                    }
                }

                var end = await Scan(plan, token, tracked: trackedNames);
                if (!Matches(end, expectedLocal) || !SameRemote(verified, await api.ListAsync(token)))
                    throw new IOException(L.Text(TextId.BothVersionsChangedAtTheEndOfSyncCompletion));
            } // Shutdown must succeed before the application baseline advances.

            DebugTrace.Write("autocloud host disposed; final scan begin");
            if (confirmed is null || expectedLocal is null || !Matches(await Scan(plan, token, tracked: trackedNames), expectedLocal))
                throw new IOException(L.Text(TextId.TheLocalVersionChangedAfterTheNativeInterfaceExited));
            DebugTrace.Write("autocloud final scan complete; baseline write begin");
            await CloudSyncService.AtomicJson(BaselineFile, confirmed, token);
            File.Delete(TransactionFile);
            await CloudSyncService.AtomicJson(Path.Combine(backup, "confirmed.json"), new AutoConfirmed(confirmed, false, false), token);
            if (!File.Exists(Path.Combine(home, "autocloud-unfinished-batch.json")))
                File.Delete(CloudSaveSafety.RecoveryFile(home));
            DebugTrace.Write("autocloud sync complete");
            return new(expectedLocal, confirmed.Hashes);
        }
        catch (Exception original)
        {
            DebugTrace.Failure("autocloud failed", original);
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
    private static async Task<Dictionary<string, AutoLocal>> Scan(AutoCloudPathPlan plan, CancellationToken token, HashSet<string>? scratch = null, IEnumerable<string>? tracked = null)
    {
        var result = new Dictionary<string, AutoLocal>(StringComparer.OrdinalIgnoreCase);
        int entries = 0;
        long total = 0;
        async Task Add(string path, AutoCloudKey key, uint platforms)
        {
            token.ThrowIfCancellationRequested();
            var name = AutoCloudWire.Name(key);
            if (result.TryGetValue(name, out var existing))
            {
                if (!existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.Text(TextId.SaveMappingsOverlapAreDuplicatedOrExceedSupportedLimits));
                result[name] = existing with { File = existing.File with { Platforms = existing.File.Platforms | platforms } };
                return;
            }
            DebugTrace.Write("scan file begin " + path);
            var bytes = await AutoCloudDisk.Read(path, token);
            DebugTrace.Write($"scan file read complete bytes={bytes.Length} path={path}");
            total += bytes.Length;
            if (result.Count >= 4096 || total > 4096L * 1024 * 1024)
                throw new InvalidDataException(L.Text(TextId.SaveMappingsOverlapAreDuplicatedOrExceedSupportedLimits));
            result.Add(name, new(new(name, CloudTransfer.Hash(bytes), bytes.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds(), platforms), path));
        }
        async Task Walk(AutoCloudCompiledRule rule, string directory)
        {
            token.ThrowIfCancellationRequested();
            DebugTrace.Write("scan directory begin " + directory);
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
            var matching = AutoCloudFileSearch.MatchingNames(directory, rule.Pattern);
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

                if (!matching.Contains(Path.GetFileName(path)) || Path.GetFileName(path).Equals("steam_autocloud.vdf", StringComparison.OrdinalIgnoreCase))
                    continue;
                await Add(path, plan.ToCloud(path), rule.Platforms);
            }
        }

        foreach (var rule in plan.Rules)
            await Walk(rule, rule.LocalDirectory);
        if (tracked is not null)
            foreach (var name in tracked)
            {
                var key = AutoCloudWire.Key(name);
                var path = plan.ToLocal(key);
                if (result.ContainsKey(AutoCloudWire.Name(key)))
                    continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || AutoCloudDisk.IsTemporary(path))
                    throw new IOException(L.Text(TextId.AutoCloudSaveDirectoryIsInvalidOrIsALink));
                await Add(path, key, plan.PlatformsFor(key));
            }
        return result;
    }

    private static async Task<Dictionary<string, string>> Backup(Dictionary<string, AutoLocal> local, string folder, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (var p in local)
        {
            DebugTrace.Write("backup file begin " + p.Value.Path);
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
        await JournalTemps(plan, [], token); // Validate exact temporary identities before touching them.
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
    private async Task<HashSet<string>> JournalTemps(AutoCloudPathPlan plan, HashSet<string> tracked, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(TransactionFile))
            return result;
        var transaction = KettleJson.Deserialize<AutoTransaction>(await File.ReadAllTextAsync(TransactionFile, token)) ?? throw new IOException(L.Text(TextId.InvalidTransactionRecord));
        if (transaction.Files.Length > 4096)
            throw new IOException(L.Text(TextId.TransactionRecordIsTooLarge));
        foreach (var item in transaction.Files)
        {
            tracked.Add(item.WireName);
            if (item.TemporaryFile is null)
                continue;
            if (!AutoCloudDisk.IsTemporary(item.TemporaryFile) || Path.GetDirectoryName(AutoCloudPathSafety.Absolute(item.TemporaryFile)) != Path.GetDirectoryName(plan.ToLocal(AutoCloudWire.Key(item.WireName))) || !result.Add(item.TemporaryFile))
                throw new IOException(L.Text(TextId.TransactionTemporaryPathMismatch));
        }

        return result;
    }
}
