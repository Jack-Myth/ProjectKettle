using System.Text;
using System.Text.Json;
using Kettle.Core;

internal static class MissingSaveChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        foreach (var mode in new[] { CloudSupport.RemoteStorage, CloudSupport.AutoCloud })
        {
            var home = Path.Combine(root, "missing-save-" + mode);
            var steam = Path.Combine(home, "steam", "steamcmd.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(steam)!);
            File.WriteAllText(steam, "fixture only");
            var remote = Path.Combine(Path.GetDirectoryName(steam)!, "userdata", "123", "42", "remote");
            var low = Path.Combine(home, "low");
            var info = "\"42\" { \"ufs\" { \"savefiles\" { \"0\" { \"root\" \"WinAppDataLocalLow\" \"path\" \"Game\" \"pattern\" \"*.dat\" \"recursive\" \"1\" } } } }";
            var metadata = new AppLaunchMetadata(mode, "game.exe", null) { AutoCloudAppInfo = mode == CloudSupport.AutoCloud ? info : null };
            var context = new CloudContext(42, 123, steam, metadata);
            var wire = mode == CloudSupport.AutoCloud ? "%WinAppDataLocalLow%Game/save.dat" : "save.dat";
            var local = Path.Combine(mode == CloudSupport.AutoCloud ? Path.Combine(low, "Game") : remote, "save.dat");
            var cloud = new Files();
            cloud.Server[wire] = "precious-save"u8.ToArray();
            bool failShutdown = false;
            var service = new CloudSyncService(home, (_, _) => Task.FromResult<ICloudRpc>(new Rpc(remote, () => failShutdown)), (_, _) => cloud, new Roots(low), new TestBackupDelete(root));
            var prep = await service.PrepareAsync(context, ConflictChoice.None, null, default);
            var baseline = Directory.GetFiles(home, mode == CloudSupport.AutoCloud ? "autocloud-baseline.json" : "baseline.json", SearchOption.AllDirectories).Single();
            var baselineBefore = File.ReadAllText(baseline);
            File.Delete(local);
            failShutdown = true;
            await Reject(() => service.PrepareAsync(context, ConflictChoice.None, null, default));
            check(cloud.Mutations == 0 && cloud.Text(wire) == "precious-save", mode + ": missing local plus shutdown failure fails without erasing cloud");
            check(File.ReadAllText(baseline) == baselineBefore, mode + ": failed preflight keeps baseline");
            var recovery = Directory.GetFiles(home, "cloud-sync-recovery.json", SearchOption.AllDirectories).Single();
            var recoveryBefore = File.ReadAllText(recovery);
            using (var pins = JsonDocument.Parse(recoveryBefore))
            {
                for (int i = 0; i < 4; i++)
                {
                    if (File.Exists(local)) File.Delete(local);
                    await Reject(() => service.PrepareAsync(context, ConflictChoice.None, null, default));
                    check(cloud.Mutations == 0, mode + ": repeated failed attempts never mutate cloud");
                }
                check(File.ReadAllText(recovery) == recoveryBefore && Directory.Exists(pins.RootElement.GetProperty("Backup").GetString()) && Directory.Exists(pins.RootElement.GetProperty("PreviousBackup").GetString()), mode + ": failed attempt and preceding backup survive repeated retries");
            }
            failShutdown = false;
            prep = await service.PrepareAsync(context, ConflictChoice.None, null, default);
            check(File.ReadAllText(local) == "precious-save", mode + ": original save restored by preparation");
            await service.FinishAsync(prep, ConflictChoice.None, null, default);
            check(cloud.Mutations == 0 && cloud.Text(wire) == "precious-save" && !File.Exists(recovery), mode + ": retry restores saves and successful unchanged postgame never overwrites cloud");

            // Preflight must not upload modified files, new files, or server-requested reuploads.
            File.WriteAllText(local, "local-edit");
            var newLocal = Path.Combine(Path.GetDirectoryName(local)!, "new.dat");
            File.WriteAllText(newLocal, "new-local");
            cloud.Reupload = true;
            var batchFile = Path.Combine(Path.GetDirectoryName(baseline)!, mode == CloudSupport.AutoCloud ? "autocloud-unfinished-batch.json" : "unfinished-batch.json");
            File.WriteAllText(batchFile, "77");
            var preflightConflict = await Conflict(() => service.PrepareAsync(context, ConflictChoice.None, null, default));
            check(cloud.Mutations == 0 && File.ReadAllText(baseline) == baselineBefore, mode + ": local-only change still conflicts at launch without advancing baseline");
            prep = await service.PrepareAsync(context, ConflictChoice.Local, preflightConflict.Ticket, default);
            check(cloud.Mutations == 0 && cloud.Text(wire) == "precious-save" && File.ReadAllText(local) == "local-edit", mode + ": preflight defers all uploads and retains local edits");
            check(cloud.ClosedBatches == 0 && File.Exists(batchFile) && File.Exists(recovery), mode + ": read-only preflight keeps unresolved batches and their recovery backups");
            using (var stored = JsonDocument.Parse(File.ReadAllText(baseline)))
            {
                check(stored.RootElement.GetProperty("Hashes").GetProperty(wire).GetString() == CloudTransfer.Hash("precious-save"u8.ToArray()) && stored.RootElement.GetProperty("Hashes").EnumerateObject().Count() == 1, mode + ": preflight does not mark deferred edits as cloud-confirmed");
                check(stored.RootElement.GetProperty("KnownFiles").GetArrayLength() == 2, mode + ": pending local files remain part of loss detection before upload");
            }
            File.Delete(newLocal);
            bool missingPendingBlocked = false;
            try { await service.PrepareAsync(context, ConflictChoice.None, null, default); }
            catch (IOException) { missingPendingBlocked = true; }
            check(missingPendingBlocked && cloud.Mutations == 0, mode + ": disappearing deferred local file cannot silently clear its history");
            File.WriteAllText(newLocal, "new-local");
            cloud.Reupload = false;
            await service.FinishAsync(prep, ConflictChoice.None, null, default);
            check(cloud.ClosedBatches == 1 && !File.Exists(batchFile) && !File.Exists(recovery), mode + ": postgame resolves old batch before releasing recovery backup pins");
            check(cloud.Text(wire) == "local-edit" && cloud.Server.Count == 2 && cloud.Mutations == 2, mode + ": postgame uploads deferred edits and new files");

            // A fresh, version-bound 'local' selection must also be deferred at launch.
            File.WriteAllText(local, "chosen-local");
            cloud.Server[wire] = "other-device"u8.ToArray();
            CloudConflictException? conflict = null;
            try { await service.PrepareAsync(context, ConflictChoice.None, null, default); }
            catch (CloudConflictException e) { conflict = e; }
            check(conflict is not null, mode + ": divergent versions require a bound choice");
            var mutations = cloud.Mutations;
            prep = await service.PrepareAsync(context, ConflictChoice.Local, conflict!.Ticket, default);
            check(cloud.Mutations == mutations && cloud.Text(wire) == "other-device" && File.ReadAllText(local) == "chosen-local", mode + ": choosing local before launch cannot upload");
            await service.FinishAsync(prep, ConflictChoice.None, null, default);
            check(cloud.Text(wire) == "chosen-local", mode + ": selected local copy uploads only postgame");

            // Failed downloads leave the server intact and prevent game launch.
            File.Delete(local);
            cloud.CorruptDownload = true;
            mutations = cloud.Mutations;
            await Reject(() => service.PrepareAsync(context, ConflictChoice.None, null, default));
            check(!File.Exists(local) && cloud.Mutations == mutations && cloud.Text(wire) == "chosen-local", mode + ": invalid download fails without mutating cloud");
            cloud.CorruptDownload = false;
            await service.PrepareAsync(context, ConflictChoice.None, null, default);
            check(File.ReadAllText(local) == "chosen-local", mode + ": successful retry restores intact cloud copy");

            // Legacy damage must not be blessed as a fresh game, even with replacement files.
            baselineBefore = File.ReadAllText(baseline);
            File.Delete(local);
            cloud.Server.Remove(wire);
            File.WriteAllText(newLocal, "empty-game-replacement");
            mutations = cloud.Mutations;
            for (int i = 0; i < 2; i++)
            {
                await Reject(() => service.PrepareAsync(context, ConflictChoice.None, null, default));
                check(cloud.Mutations == mutations && File.ReadAllText(baseline) == baselineBefore, mode + ": missing historical copy on both sides rejects preparation and replacement uploads across retries");
            }
            bool blocked = false;
            try { await service.FinishAsync(prep with { Snapshot = null }, ConflictChoice.None, null, default); }
            catch (IOException) { blocked = true; }
            check(blocked && cloud.Mutations == mutations && File.ReadAllText(baseline) == baselineBefore, mode + ": historical loss also blocks postgame replacement upload");

            // Recover the fixture, then exercise postgame deletion and snapshot recovery.
            cloud.Server[wire] = "chosen-local"u8.ToArray();
            File.WriteAllText(newLocal, "new-local");
            prep = await service.PrepareAsync(context, ConflictChoice.None, null, default);
            cloud.Server[wire] = "newer-cloud"u8.ToArray();
            var remoteConflict = await Conflict(() => service.PrepareAsync(context, ConflictChoice.None, null, default));
            check(File.ReadAllText(local) == "chosen-local", mode + ": cloud-only change also conflicts at launch");
            prep = await service.PrepareAsync(context, ConflictChoice.Cloud, remoteConflict.Ticket, default);
            check(File.ReadAllText(local) == "newer-cloud", mode + ": explicit cloud choice downloads before launch");
            File.Delete(local);
            cloud.Server[wire] = "other-device-race"u8.ToArray();
            var deletedBefore = cloud.Deletes;
            var deletionRace = await Conflict(() => service.FinishAsync(prep, ConflictChoice.None, null, default));
            check(cloud.Deletes == deletedBefore && cloud.Text(wire) == "other-device-race", mode + ": game deletion cannot delete cloud contents changed since launch");
            await service.FinishAsync(prep, ConflictChoice.Cloud, deletionRace.Ticket, default);
            check(File.ReadAllText(local) == "other-device-race" && cloud.Deletes == deletedBefore, mode + ": explicit cloud choice resolves deletion conflict by restoring the newer copy");
            prep = await service.PrepareAsync(context, ConflictChoice.None, null, default);
            File.Delete(local);
            cloud.FailDeleteAfterCommit = true;
            await Reject(() => service.FinishAsync(prep, ConflictChoice.None, null, default));
            check(!cloud.Server.ContainsKey(wire) && !File.Exists(local), mode + ": confirmed game deletion is sent, failed acknowledgement retains retry state");
            check(Directory.GetFiles(home, "*.dat", SearchOption.AllDirectories).Any(p => p.Contains("cloud-before") && File.ReadAllText(p) == "other-device-race"), mode + ": deleted cloud version is backed up before deletion");
            cloud.FailDeleteAfterCommit = false;
            prep = JsonSerializer.Deserialize(JsonSerializer.Serialize(prep, CoreSessionJson.Default.CloudPreparation), CoreSessionJson.Default.CloudPreparation)!;
            await service.FinishAsync(prep, ConflictChoice.None, null, default);
            check(!cloud.Server.ContainsKey(wire) && !File.Exists(local), mode + ": persisted launch snapshot permits idempotent deletion recovery without restoring deleted file");
            await service.PrepareAsync(context, ConflictChoice.None, null, default);
            check(!File.Exists(local), mode + ": next launch accepts confirmed deletion");
        }
    }

    private static async Task Reject(Func<Task> operation)
    {
        try { await operation(); }
        catch (IOException) { return; }
        throw new Exception("Expected synchronization failure");
    }

    private static async Task<CloudConflictException> Conflict(Func<Task> operation)
    {
        try { await operation(); }
        catch (CloudConflictException error) { return error; }
        throw new Exception("Expected a save conflict");
    }

    private sealed class Roots(string path) : IAutoCloudRoots
    {
        public string Resolve(string root) => path;
    }

    private sealed class Rpc(string root, Func<bool> fail) : ICloudRpc
    {
        public string RemoteRoot => root;
        public bool CloudEnabled => true;
        public Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token) => throw new Exception("Unexpected RPC");
        public ValueTask DisposeAsync() => fail() ? ValueTask.FromException(new IOException("injected shutdown failure")) : ValueTask.CompletedTask;
    }

    private sealed class Files : ICloudFiles
    {
        public Dictionary<string, byte[]> Server = new(StringComparer.OrdinalIgnoreCase);
        public int Mutations, ClosedBatches, Deletes;
        public bool CorruptDownload, Reupload, FailDeleteAfterCommit;
        public string Text(string name) => Encoding.UTF8.GetString(Server[name]);
        public Task<CloudSnapshot> ListAsync(CancellationToken token) => Task.FromResult(new CloudSnapshot(1, Server.Select(p => new CloudFile(p.Key, CloudTransfer.Hash(p.Value), p.Value.Length, 123) { ReuploadRequested = Reupload }).ToArray()));
        public Task DownloadAsync(CloudFile file, Stream destination, CancellationToken token) => destination.WriteAsync(CorruptDownload ? "broken"u8.ToArray() : Server[file.Name], token).AsTask();
        public Task UploadAsync(IReadOnlyList<CloudUpload> files, Func<ulong, Task> saveBatch, CancellationToken token)
        {
            foreach (var file in files)
            {
                Server[file.Name] = File.ReadAllBytes(file.SourcePath!);
                Mutations++;
            }
            return Task.CompletedTask;
        }
        public async Task DeleteAsync(IReadOnlyList<CloudDelete> files, Func<ulong, Task> saveBatch, CancellationToken token)
        {
            await saveBatch(99);
            foreach (var file in files)
            {
                if (CloudTransfer.Hash(Server[file.Name]) != file.ExpectedRemoteSha1) throw new IOException("Cloud changed before deletion");
                Server.Remove(file.Name);
                Mutations++;
                Deletes++;
            }
            if (FailDeleteAfterCommit) throw new IOException("Injected lost delete acknowledgement");
            await saveBatch(0);
        }
        public Task CloseBatchAsync(ulong batch, bool success, CancellationToken token)
        {
            if (success) throw new Exception("Old batch must be aborted");
            ClosedBatches++;
            return Task.CompletedTask;
        }
    }
}
