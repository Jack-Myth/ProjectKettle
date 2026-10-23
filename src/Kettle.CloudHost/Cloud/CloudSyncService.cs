using System.Text.Json;
using System.Security.Cryptography;

namespace Kettle.Cloud;

public sealed record CloudBaseline(Dictionary<string, string> Hashes, ulong ChangeNumber)
{
    // Cloud-confirmed hashes and known local filenames differ while uploads are deferred.
    public string[]? KnownFiles { get; init; }
}
public interface IGameCloudSync
{
    Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token);
    Task FinishAsync(CloudPreparation preparation, ConflictChoice choice, string? ticket, CancellationToken token);
}

public sealed class CloudSyncService(string appDirectory, Func<CloudContext, CancellationToken, Task<ICloudRpc>> connect, Func<uint, ICloudRpc, ICloudFiles>? files = null, IAutoCloudRoots? autoCloudRoots = null, IBackupDelete? deletion = null) : IGameCloudSync
{
    private readonly Func<CloudContext, CancellationToken, Task<ICloudRpc>> connection = connect ?? throw new ArgumentNullException(nameof(connect));
    private string Home(CloudContext context) => Path.Combine(appDirectory, "data", "cloud", context.AccountId.ToString(), context.AppId.ToString(), Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(context.SteamCmd).ToUpperInvariant())))[..16]);
    public async Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
    {
        if (context.Metadata.Cloud == CloudSupport.None)
            return new(context, null, false);
        if (context.Metadata.Cloud == CloudSupport.AutoCloud)
        {
            var snapshot = await new AutoCloudSync(Home(context), connection, files, autoCloudRoots ?? new WindowsAutoCloudRoots(), deletion).SyncAsync(context, choice, ticket, token, uploadChanges: false);
            return new(context, null, true) { Snapshot = snapshot };
        }

        if (context.Metadata.Cloud == CloudSupport.UnsupportedAutoCloud)
            throw SaveErrors.Tag(new NotSupportedException(nameof(SaveErrorCode.ThisAutoCloudConfigurationHasNoSupportedRulesReadThe)), SaveErrorCode.ThisAutoCloudConfigurationHasNoSupportedRulesReadThe, detail: context.Metadata.CloudDescription);
        if (context.Metadata.Cloud != CloudSupport.RemoteStorage)
            throw SaveErrors.Tag(new NotSupportedException(nameof(SaveErrorCode.UnknownCloudSaveModeSyncWasNotPerformed)), SaveErrorCode.UnknownCloudSaveModeSyncWasNotPerformed);
        return await Sync(context, choice, ticket, token, uploadChanges: false);
    }

    public async Task FinishAsync(CloudPreparation preparation, ConflictChoice choice, string? ticket, CancellationToken token)
    {
        if (preparation.Applicable)
        {
            if (preparation.Context.Metadata.Cloud == CloudSupport.AutoCloud)
                await new AutoCloudSync(Home(preparation.Context), connection, files, autoCloudRoots ?? new WindowsAutoCloudRoots(), deletion).SyncAsync(preparation.Context, choice, ticket, token, uploadChanges: true, preparation.Snapshot);
            else if (preparation.Context.Metadata.Cloud == CloudSupport.RemoteStorage)
                await Sync(preparation.Context, choice, ticket, token, uploadChanges: true, preparation.Snapshot);
            else
                throw SaveErrors.Tag(new NotSupportedException(nameof(SaveErrorCode.ThePendingRecordHasNoSupportedCloudSaveMode)), SaveErrorCode.ThePendingRecordHasNoSupportedCloudSaveMode);
        }
    }

    private async Task<CloudPreparation> Sync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token, bool uploadChanges, CloudRunSnapshot? run = null)
    {
        run = CloudSaveSafety.Normalize(run);
        var home = Home(context);
        Directory.CreateDirectory(home);
        using var retention = new CloudBackupRetention(home, deletion);
        retention.BeforeCreate();
        var expectedApp = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(context.SteamCmd)!, "userdata", context.AccountId.ToString(), context.AppId.ToString()));
        // Fresh complete local backup precedes even SteamAPI.Init, which can rewrite its local index.
        var backup = await SaveBackup.CreateAsync(expectedApp, Path.Combine(home, "backups"), token, pendingRetention: true);
        retention.Commit(backup);
        var remoteRoot = Path.Combine(expectedApp, "remote");
        var beforeInit = await LocalFiles(remoteRoot, token);
        await CloudSaveSafety.PreserveFailedAttempt(home, backup, retention, token);
        CloudSnapshot verified;
        Dictionary<string, string> expectedLocal;
        await using (var rpc = await connection(context, token))
        {
            if (!Path.GetFullPath(rpc.RemoteRoot).Equals(remoteRoot, StringComparison.OrdinalIgnoreCase))
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.TheActualCloudSavePathDoesNotMatchThe)), SaveErrorCode.TheActualCloudSavePathDoesNotMatchThe);
            if (!rpc.CloudEnabled)
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.SteamCloudIsDisabledCheckAndRetryTheSetting)), SaveErrorCode.SteamCloudIsDisabledCheckAndRetryTheSetting);
            if (!SameLocal(beforeInit, await LocalFiles(remoteRoot, token)))
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.RealSavesChangedAfterNativeInterfaceInitializationThePre)), SaveErrorCode.RealSavesChangedAfterNativeInterfaceInitializationThePre);
            var api = files?.Invoke(context.AppId, rpc) ?? new CloudTransfer(context.AppId, rpc);
            var batchFile = Path.Combine(home, "unfinished-batch.json");
            if (uploadChanges && File.Exists(batchFile))
            {
                var batch = CloudJson.Deserialize<ulong>(await File.ReadAllTextAsync(batchFile, token));
                if (batch != 0)
                    await api.CloseBatchAsync(batch, false, CancellationToken.None);
                File.Delete(batchFile);
            }

            var remote = await api.ListAsync(token);
            var local = await LocalFiles(remoteRoot, token);
            var baselineFile = Path.Combine(home, "baseline.json");
            var baseline = File.Exists(baselineFile) ? CloudJson.Deserialize<CloudBaseline>(await File.ReadAllTextAsync(baselineFile, token)) : null;
            var remoteMap = remote.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            CloudSaveSafety.RequireHistoricalCopies(CloudSaveSafety.RequiredHistory(baseline?.KnownFiles?.AsEnumerable() ?? baseline?.Hashes.Keys, run, uploadChanges), local.Keys, remoteMap.Keys);
            var currentTicket = Ticket(local, remote);
            var staleChoice = choice != ConflictChoice.None && ticket != currentTicket;
            if (staleChoice)
                choice = ConflictChoice.None;
            var downloads = new List<CloudFile>();
            var uploads = new List<CloudUpload>();
            var deletes = new List<CloudDelete>();
            var conflicts = new List<string>();
            foreach (var name in local.Keys.Union(remoteMap.Keys, StringComparer.OrdinalIgnoreCase))
            {
                local.TryGetValue(name, out var localFile);
                remoteMap.TryGetValue(name, out var remoteFile);
                var l = localFile?.Sha1;
                var r = remoteFile?.Sha1;
                var previous = uploadChanges && run is not null ? run.Cloud.GetValueOrDefault(name)
                    : baseline?.Hashes.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                var action = CloudSaveSafety.Decide(uploadChanges, name, l, r, previous, remoteFile?.ReuploadRequested == true, choice, staleChoice, run);
                if (action == CloudSaveSafety.Action.None) continue;
                if (action == CloudSaveSafety.Action.Delete) { deletes.Add(new(remoteFile!.Name, r!)); continue; }
                if (action == CloudSaveSafety.Action.Conflict) { conflicts.Add(name); continue; }
                if (action == CloudSaveSafety.Action.Download) downloads.Add(remoteFile!);
                bool takeLocal = action == CloudSaveSafety.Action.Upload;
                if (takeLocal)
                {
                    var bytes = await AutoCloudDisk.Read(RemotePath(remoteRoot, name), token);
                    if (CloudTransfer.Hash(bytes) != l)
                        throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.LocalSavesAreStillChangingCloseOtherGameInstances)), SaveErrorCode.LocalSavesAreStillChangingCloseOtherGameInstances);
                    var upload = new CloudUpload(name, bytes, localFile!.Timestamp, r, remoteFile?.Platforms ?? localFile.Platforms);
                    uploads.Add(api is CloudTransfer ? await CloudUpload.Stage(upload, Path.Combine(backup, "uploads"), token) : upload);
                }
            }

            if (staleChoice)
            {
                conflicts = ConflictFiles(local.Keys.Union(remoteMap.Keys, StringComparer.OrdinalIgnoreCase), local, remoteMap).Select(f => f.Name).ToList();
                downloads.Clear();
                uploads.Clear();
                if (conflicts.Count == 0)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.BothSaveVersionsChangedAndNowMatchRunThe)), SaveErrorCode.BothSaveVersionsChangedAndNowMatchRunThe);
            }

            if (conflicts.Count > 0)
            {
                var conflictFolder = Path.Combine(home, "conflicts", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(conflictFolder);
                await Parallel.ForEachAsync(conflicts, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (name, ct) =>
                {
                    if (local.ContainsKey(name))
                        File.Copy(RemotePath(remoteRoot, name), RemotePath(conflictFolder, name + ".local", create: true));
                    if (remoteMap.TryGetValue(name, out var cloud))
                        await File.WriteAllBytesAsync(RemotePath(conflictFolder, name + ".cloud", create: true), await api.DownloadAsync(cloud, ct), ct);
                });
                await File.WriteAllTextAsync(Path.Combine(conflictFolder, "versions.json"), CloudJson.Serialize(new CloudVersions(currentTicket, backup, remote.Files, conflicts.ToArray())), token);
                throw SaveErrors.Tag(new CloudConflictException(nameof(SaveErrorCode.LocalAndCloudSavesConflictBothCopiesArePreserved), currentTicket, ConflictFiles(conflicts, local, remoteMap), conflictFolder), SaveErrorCode.LocalAndCloudSavesConflictBothCopiesArePreserved, prefixCode: staleChoice ? nameof(SaveErrorCode.SaveVersionsChangedThePreviousChoiceWasNotApplied) : null);
            }

            // Stage and verify every network payload before changing any game-visible local file.
            var stage = Path.Combine(home, "downloads", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            // Preserve remote originals before overwriting them, including failed uploads.
            var cloudBefore = Path.Combine(backup, "cloud-before");
            Directory.CreateDirectory(cloudBefore);
            var overwritten = uploads.Where(u => uploadChanges && remoteMap.ContainsKey(u.Name)).Select(u => remoteMap[u.Name]).Concat(deletes.Select(d => remoteMap[d.Name])).DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            for (int i = 0; i < overwritten.Length; i++)
            {
                var original = overwritten[i];
                var bytes = await api.DownloadAsync(original, token);
                if (bytes.LongLength != original.Size || CloudTransfer.Hash(bytes) != original.Sha1)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CloudCopyVerificationFailed)), SaveErrorCode.CloudCopyVerificationFailed);
                var copy = Path.Combine(cloudBefore, i + ".dat");
                await File.WriteAllBytesAsync(copy, bytes, token);
                if (CloudTransfer.Hash(await File.ReadAllBytesAsync(copy, token)) != original.Sha1)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CloudBackupVerificationOnDiskFailed)), SaveErrorCode.CloudBackupVerificationOnDiskFailed);
            }
            await AtomicCloudJson(Path.Combine(cloudBefore, "manifest.json"), overwritten.Select((f, i) => new CloudBackupEntry(i + ".dat", f)).ToArray(), token);
            await Parallel.ForEachAsync(downloads, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (file, ct) =>
            {
                var bytes = await api.DownloadAsync(file, ct);
                if (bytes.LongLength != file.Size || CloudTransfer.Hash(bytes) != file.Sha1)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CloudCopyVerificationFailed)), SaveErrorCode.CloudCopyVerificationFailed);
                await File.WriteAllBytesAsync(RemotePath(stage, file.Name, create: true), bytes, ct);
            });
            async Task SaveBatch(ulong batch)
            {
                if (batch == 0)
                {
                    if (File.Exists(batchFile))
                        File.Delete(batchFile);
                }
                else
                    await AtomicCloudJson(batchFile, batch, CancellationToken.None);
            }
            // Launch preparation never mutates the cloud, including reupload requests
            // and explicit local conflict choices. Local edits wait for confirmed game exit.
            if (uploadChanges)
            {
                await api.UploadAsync(uploads, SaveBatch, token);
                if (deletes.Count > 0)
                {
                    if (!SameLocal(local, await LocalFiles(remoteRoot, token)))
                        throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.LocalSavesChangedDuringSyncLocalFilesWereNot)), SaveErrorCode.LocalSavesChangedDuringSyncLocalFilesWereNot);
                    await api.DeleteAsync(deletes, SaveBatch, token);
                }
            }
            verified = await api.ListAsync(token);
            var expected = remoteMap.ToDictionary(p => p.Key, p => p.Value.Sha1, StringComparer.OrdinalIgnoreCase);
            if (uploadChanges)
                foreach (var upload in uploads) expected[upload.Name] = upload.Hash;
            foreach (var deleted in deletes) expected.Remove(deleted.Name);
            if (verified.ChangeNumber < remote.ChangeNumber || verified.Files.Length != expected.Count || verified.Files.Any(f => !expected.TryGetValue(f.Name, out var hash) || hash != f.Sha1))
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.TheFullCloudListingWasNotConfirmedAfterCommit)), SaveErrorCode.TheFullCloudListingWasNotConfirmedAfterCommit);
            foreach (var file in downloads)
                if (verified.Files.FirstOrDefault(f => f.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase))?.Sha1 != file.Sha1)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CloudVersionsChangedAgainAfterDownloadRetry)), SaveErrorCode.CloudVersionsChangedAgainAfterDownloadRetry);
            var nowLocal = await LocalFiles(remoteRoot, token);
            if (!SameLocal(local, nowLocal))
                throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.LocalSavesChangedDuringSyncLocalFilesWereNot)), SaveErrorCode.LocalSavesChangedDuringSyncLocalFilesWereNot);
            expectedLocal = local.ToDictionary(p => p.Key, p => p.Value.Sha1, StringComparer.OrdinalIgnoreCase);
            foreach (var file in downloads) expectedLocal[file.Name] = file.Sha1;
            Directory.CreateDirectory(remoteRoot);
            foreach (var file in downloads)
            {
                token.ThrowIfCancellationRequested();
                var destination = RemotePath(remoteRoot, file.Name, create: true);
                var bytes = await AutoCloudDisk.Read(RemotePath(stage, file.Name), token);
                if (bytes.LongLength != file.Size || CloudTransfer.Hash(bytes) != file.Sha1)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.CloudBackupVerificationOnDiskFailed)), SaveErrorCode.CloudBackupVerificationOnDiskFailed);
                await AutoCloudDisk.Write(destination, bytes, file.Timestamp, token);
            }
        }

        // The helper has shut down before rebuilding the game-visible index, so its old cache
        // cannot overwrite our verified download index during SteamAPI.Shutdown.
        var finalLocal = await LocalFiles(remoteRoot, token);
        if (finalLocal.Count != expectedLocal.Count || expectedLocal.Any(p => !finalLocal.TryGetValue(p.Key, out var local) || local.Sha1 != p.Value))
            throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.LocalAndCloudVersionsDoNotFullyMatchAfter)), SaveErrorCode.LocalAndCloudVersionsDoNotFullyMatchAfter);
        // Only mark files as cloud-confirmed in the native cache when they really match.
        if (verified.Files.Length == finalLocal.Count && verified.Files.All(f => finalLocal.TryGetValue(f.Name, out var local) && local.Sha1 == f.Sha1))
            await WriteIndex(context.AppId, remoteRoot, verified, token);
        await AtomicCloudJson(Path.Combine(home, "baseline.json"), new CloudBaseline(verified.Files.ToDictionary(f => f.Name, f => f.Sha1, StringComparer.OrdinalIgnoreCase), verified.ChangeNumber) { KnownFiles = finalLocal.Keys.ToArray() }, token);
        if (!File.Exists(Path.Combine(home, "unfinished-batch.json")))
            File.Delete(CloudSaveSafety.RecoveryFile(home));
        return new(context, remoteRoot, true) { Snapshot = new(expectedLocal, verified.Files.ToDictionary(f => f.Name, f => f.Sha1, StringComparer.OrdinalIgnoreCase)) };
    }

    private static bool SameLocal(Dictionary<string, CloudFile> first, Dictionary<string, CloudFile> second) => first.Count == second.Count && first.All(p => second.TryGetValue(p.Key, out var value) && value.Sha1 == p.Value.Sha1);
    private static CloudConflictFile[] ConflictFiles(IEnumerable<string> names, Dictionary<string, CloudFile> local, Dictionary<string, CloudFile> remote) => names.Where(name => local.GetValueOrDefault(name)?.Sha1 != remote.GetValueOrDefault(name)?.Sha1).Select(name => new CloudConflictFile(name, local.GetValueOrDefault(name), remote.GetValueOrDefault(name))).ToArray();
    private static string Ticket(Dictionary<string, CloudFile> local, CloudSnapshot remote) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(CloudJson.Serialize(new CloudTicket(local.OrderBy(p => p.Key).Select(p => new LocalHash(p.Key, p.Value.Sha1)).ToArray(), remote.Files.OrderBy(f => f.Name).Select(f => new RemoteHash(f.Name, f.Sha1)).ToArray(), remote.ChangeNumber)))));
    private static async Task<Dictionary<string, CloudFile>> LocalFiles(string root, CancellationToken token)
    {
        var result = new Dictionary<string, CloudFile>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
            return result;
        AutoCloudPathSafety.NoLinks(root, directoryOnly: true);
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            using var lease = AutoCloudDisk.HoldDirectory(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.SavesContainLinksAReliableBackupCannotBeCreated)), SaveErrorCode.SavesContainLinksAReliableBackupCannotBeCreated);
                if ((info.Attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                var name = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                CloudTransfer.ValidateName(name);
                if (info.Length > CloudTransfer.MaxFileBytes || (total += info.Length) > 4L * 1024 * 1024 * 1024 || result.Count >= 4096)
                    throw SaveErrors.Tag(new NotSupportedException(nameof(SaveErrorCode.TheSaveDirectoryExceedsSupportedLimits)), SaveErrorCode.TheSaveDirectoryExceedsSupportedLimits);
                var bytes = await AutoCloudDisk.Read(path, token);
                result.Add(name, new(name, CloudTransfer.Hash(bytes), bytes.LongLength, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()));
            }
        }        return result;
    }

    private static string RemotePath(string root, string name, bool create = false)
    {
        CloudTransfer.ValidateName(name);
        var path = AutoCloudPathSafety.Combine(root, name);
        AutoCloudPathSafety.NoLinks(path);
        if (create) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
    internal static Task AtomicCloudJson<T>(string file, T value, CancellationToken token) =>
        AtomicText(file, CloudJson.Serialize(value), token);

    private static async Task AtomicText(string file, string json, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, json, token);
        File.Move(temp, file, true);
    }

    private static async Task WriteIndex(uint id, string remoteRoot, CloudSnapshot snapshot, CancellationToken token)
    {
        var lines = new List<string>
        {
            $"\"{id}\"",
            "{",
            $"\t\"ChangeNumber\"\t\"{snapshot.ChangeNumber}\"",
            "\t\"ostype\"\t\"0\""
        };
        foreach (var file in snapshot.Files)
        {
            CloudTransfer.ValidateName(file.Name);
            var localTime = new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(remoteRoot, file.Name))).ToUnixTimeSeconds();
            lines.Add($"\t\"{file.Name}\"\n\t{{\n\t\t\"root\" \"0\"\n\t\t\"size\" \"{file.Size}\"\n\t\t\"localtime\" \"{localTime}\"\n\t\t\"time\" \"{file.Timestamp}\"\n\t\t\"remotetime\" \"{file.Timestamp}\"\n\t\t\"sha\" \"{file.Sha1.ToLowerInvariant()}\"\n\t\t\"syncstate\" \"1\"\n\t\t\"persiststate\" \"0\"\n\t\t\"platformstosync2\" \"{unchecked((int)file.Platforms)}\"\n\t}}");
        }

        lines.Add("}");
        var index = Path.Combine(Path.GetDirectoryName(remoteRoot)!, "remotecache.vdf");
        var temporary = index + ".kettle.tmp";
        await File.WriteAllTextAsync(temporary, string.Join("\r\n", lines), token);
        File.Move(temporary, index, true);
    }
}
