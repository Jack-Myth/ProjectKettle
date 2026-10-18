using System.Text.Json;
using System.Security.Cryptography;

namespace Kettle.Core;

public enum ConflictChoice
{
    None,
    Local,
    Cloud
}

public sealed record CloudContext(uint AppId, uint AccountId, string SteamCmd, AppLaunchMetadata Metadata)
{
    public string? InstallPath
    {
        get; init;
    }
}

public sealed record CloudBaseline(Dictionary<string, string> Hashes, ulong ChangeNumber);
public sealed record CloudPreparation(CloudContext Context, string? RemoteRoot, bool Applicable);
public interface IGameCloudSync
{
    Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token);
    Task FinishAsync(CloudPreparation preparation, ConflictChoice choice, string? ticket, CancellationToken token);
}

public sealed class CloudSyncService(string appDirectory, Func<CloudContext, CancellationToken, Task<ICloudRpc>>? connect = null, Func<uint, ICloudRpc, ICloudFiles>? files = null, IAutoCloudRoots? autoCloudRoots = null, IBackupRecycle? recycle = null) : IGameCloudSync
{
    private readonly Func<CloudContext, CancellationToken, Task<ICloudRpc>> connection = connect ?? (async (context, token) => await CloudHostClient.ConnectAsync(context.SteamCmd, context.AccountId, context.AppId, token));
    private string Home(CloudContext context) => Path.Combine(appDirectory, "data", "cloud", context.AccountId.ToString(), context.AppId.ToString(), Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(context.SteamCmd).ToUpperInvariant())))[..16]);
    public async Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
    {
        if (context.Metadata.Cloud == CloudSupport.None)
            return new(context, null, false);
        if (context.Metadata.Cloud == CloudSupport.AutoCloud)
        {
            await new AutoCloudSync(Home(context), connection, files, autoCloudRoots ?? new WindowsAutoCloudRoots(), recycle).SyncAsync(context, choice, ticket, token);
            return new(context, null, true);
        }

        if (context.Metadata.Cloud == CloudSupport.UnsupportedAutoCloud)
            throw new NotSupportedException("此 AutoCloud 配置缺少受支持的规则，请重新读取游戏信息：" + context.Metadata.CloudDescription);
        if (context.Metadata.Cloud != CloudSupport.RemoteStorage)
            throw new NotSupportedException("未知云存档模式，未执行同步。");
        return new(context, await Sync(context, choice, ticket, token), true);
    }

    public async Task FinishAsync(CloudPreparation preparation, ConflictChoice choice, string? ticket, CancellationToken token)
    {
        if (preparation.Applicable)
        {
            if (preparation.Context.Metadata.Cloud == CloudSupport.AutoCloud)
                await new AutoCloudSync(Home(preparation.Context), connection, files, autoCloudRoots ?? new WindowsAutoCloudRoots(), recycle).SyncAsync(preparation.Context, choice, ticket, token);
            else if (preparation.Context.Metadata.Cloud == CloudSupport.RemoteStorage)
                await Sync(preparation.Context, choice, ticket, token);
            else
                throw new NotSupportedException("未完成记录缺少受支持的云存档模式，未执行同步。");
        }
    }

    private async Task<string> Sync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
    {
        var home = Home(context);
        Directory.CreateDirectory(home);
        using var retention = new CloudBackupRetention(home, recycle);
        retention.BeforeCreate();
        var expectedApp = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(context.SteamCmd)!, "userdata", context.AccountId.ToString(), context.AppId.ToString()));
        // Fresh complete local backup precedes even SteamAPI.Init, which can rewrite its local index.
        var backup = await SaveBackup.CreateAsync(expectedApp, Path.Combine(home, "backups"), token, pendingRetention: true);
        retention.Commit(backup);
        var remoteRoot = Path.Combine(expectedApp, "remote");
        CloudSnapshot verified;
        await using (var rpc = await connection(context, token))
        {
            if (!Path.GetFullPath(rpc.RemoteRoot).Equals(remoteRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("云存档实际路径与当前 SteamCMD 账号不匹配。");
            if (!rpc.CloudEnabled)
                throw new IOException("Steam 云存档已关闭，请确认后重试；未改动关闭选项。");
            var api = files?.Invoke(context.AppId, rpc) ?? new CloudTransfer(context.AppId, rpc);
            var batchFile = Path.Combine(home, "unfinished-batch.json");
            if (File.Exists(batchFile))
            {
                var batch = KettleJson.Deserialize<ulong>(await File.ReadAllTextAsync(batchFile, token));
                if (batch != 0)
                    await api.CloseBatchAsync(batch, false, CancellationToken.None);
                File.Delete(batchFile);
            }

            var remote = await api.ListAsync(token);
            var local = await LocalFiles(remoteRoot, token);
            var baselineFile = Path.Combine(home, "baseline.json");
            var baseline = File.Exists(baselineFile) ? KettleJson.Deserialize<CloudBaseline>(await File.ReadAllTextAsync(baselineFile, token)) : null;
            var remoteMap = remote.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var currentTicket = Ticket(local, remote);
            var staleChoice = choice != ConflictChoice.None && ticket != currentTicket;
            if (staleChoice)
                choice = ConflictChoice.None;
            var downloads = new List<CloudFile>();
            var uploads = new List<CloudUpload>();
            var conflicts = new List<string>();
            foreach (var name in local.Keys.Union(remoteMap.Keys, StringComparer.OrdinalIgnoreCase))
            {
                local.TryGetValue(name, out var localFile);
                remoteMap.TryGetValue(name, out var remoteFile);
                var l = localFile?.Sha1;
                var r = remoteFile?.Sha1;
                if (l == r)
                    continue;
                var previous = baseline?.Hashes.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                bool takeCloud = false, takeLocal = false;
                if (previous is not null)
                {
                    if (l == previous && r is not null)
                        takeCloud = true;
                    else if (r == previous && l is not null)
                        takeLocal = true;
                }
                else if (l is null && r is not null)
                    takeCloud = true;
                if (!takeCloud && !takeLocal)
                {
                    if (choice == ConflictChoice.Cloud && r is not null)
                        takeCloud = true;
                    else if (choice == ConflictChoice.Local && l is not null)
                        takeLocal = true;
                    else
                    {
                        conflicts.Add(name);
                        continue;
                    }
                }

                if (takeCloud)
                    downloads.Add(remoteFile!);
                if (takeLocal)
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(remoteRoot, name), token);
                    if (CloudTransfer.Hash(bytes) != l)
                        throw new IOException("本地存档仍在变化，请关闭其他游戏实例后重试。");
                    uploads.Add(new(name, bytes, localFile!.Timestamp, r));
                }
            }

            if (staleChoice)
            {
                conflicts = ConflictFiles(local.Keys.Union(remoteMap.Keys, StringComparer.OrdinalIgnoreCase), local, remoteMap).Select(f => f.Name).ToList();
                downloads.Clear();
                uploads.Clear();
                if (conflicts.Count == 0)
                    throw new IOException("双方存档版本已变化且当前一致，请重新运行；未应用旧选择。");
            }

            if (conflicts.Count > 0)
            {
                var conflictFolder = Path.Combine(home, "conflicts", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(conflictFolder);
                await Parallel.ForEachAsync(conflicts, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (name, ct) =>
                {
                    if (local.ContainsKey(name))
                        File.Copy(Path.Combine(remoteRoot, name), Path.Combine(conflictFolder, name + ".local"));
                    if (remoteMap.TryGetValue(name, out var cloud))
                        await File.WriteAllBytesAsync(Path.Combine(conflictFolder, name + ".cloud"), await api.DownloadAsync(cloud, ct), ct);
                });
                await File.WriteAllTextAsync(Path.Combine(conflictFolder, "versions.json"), KettleJson.Serialize(new CloudVersions(currentTicket, backup, remote.Files, conflicts.ToArray())), token);
                throw new CloudConflictException((staleChoice ? "存档版本已变化，旧选择未应用。" : "") + "本地与云端存档存在冲突。两个版本已保留，请选择本次要使用的版本；缺失文件不会自动传播删除。", currentTicket, ConflictFiles(conflicts, local, remoteMap), conflictFolder);
            }

            // Stage and verify every network payload before changing any game-visible local file.
            var stage = Path.Combine(home, "downloads", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            await Parallel.ForEachAsync(downloads, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (file, ct) => await File.WriteAllBytesAsync(Path.Combine(stage, file.Name), await api.DownloadAsync(file, ct), ct));
            await api.UploadAsync(uploads, async batch =>
            {
                if (batch == 0)
                {
                    if (File.Exists(batchFile))
                        File.Delete(batchFile);
                }
                else
                    await AtomicJson(batchFile, batch, CancellationToken.None);
            }, token);
            verified = await api.ListAsync(token);
            foreach (var file in downloads)
                if (verified.Files.FirstOrDefault(f => f.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase))?.Sha1 != file.Sha1)
                    throw new IOException("下载后云端版本又发生变化，请重试。");
            var nowLocal = await LocalFiles(remoteRoot, token);
            if (!SameLocal(local, nowLocal))
                throw new IOException("同步期间本地存档发生变化，未覆盖本地文件。");
            Directory.CreateDirectory(remoteRoot);
            foreach (var file in downloads)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(remoteRoot, file.Name);
                File.Move(Path.Combine(stage, file.Name), destination, true);
                File.SetLastWriteTimeUtc(destination, DateTimeOffset.FromUnixTimeSeconds(file.Timestamp).UtcDateTime);
            }
        }

        // The helper has shut down before rebuilding the game-visible index, so its old cache
        // cannot overwrite our verified download index during SteamAPI.Shutdown.
        var finalLocal = await LocalFiles(remoteRoot, token);
        if (verified.Files.Any(f => !finalLocal.TryGetValue(f.Name, out var local) || local.Sha1 != f.Sha1) || finalLocal.Keys.Any(name => !verified.Files.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("同步后的本地/云端版本未完全一致，未标记完成。");
        await WriteIndex(context.AppId, remoteRoot, verified, token);
        await AtomicJson(Path.Combine(home, "baseline.json"), new CloudBaseline(finalLocal.ToDictionary(p => p.Key, p => p.Value.Sha1, StringComparer.OrdinalIgnoreCase), verified.ChangeNumber), token);
        return remoteRoot;
    }

    private static bool SameLocal(Dictionary<string, CloudFile> first, Dictionary<string, CloudFile> second) => first.Count == second.Count && first.All(p => second.TryGetValue(p.Key, out var value) && value.Sha1 == p.Value.Sha1);
    private static CloudConflictFile[] ConflictFiles(IEnumerable<string> names, Dictionary<string, CloudFile> local, Dictionary<string, CloudFile> remote) => names.Where(name => local.GetValueOrDefault(name)?.Sha1 != remote.GetValueOrDefault(name)?.Sha1).Select(name => new CloudConflictFile(name, local.GetValueOrDefault(name), remote.GetValueOrDefault(name))).ToArray();
    private static string Ticket(Dictionary<string, CloudFile> local, CloudSnapshot remote) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(KettleJson.Serialize(new CloudTicket(local.OrderBy(p => p.Key).Select(p => new LocalHash(p.Key, p.Value.Sha1)).ToArray(), remote.Files.OrderBy(f => f.Name).Select(f => new RemoteHash(f.Name, f.Sha1)).ToArray(), remote.ChangeNumber)))));
    private static async Task<Dictionary<string, CloudFile>> LocalFiles(string root, CancellationToken token)
    {
        var result = new Dictionary<string, CloudFile>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
            return result;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(root).Any())
            throw new NotSupportedException("当前只支持无链接、平坦目录的 RemoteStorage 存档。");
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root))
        {
            var info = new FileInfo(file);
            CloudTransfer.ValidateName(info.Name);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > CloudTransfer.MaxFileBytes || (total += info.Length) > 256 * 1024 * 1024)
                throw new NotSupportedException("存档目录超出当前支持范围。");
            var bytes = await File.ReadAllBytesAsync(file, token);
            result.Add(info.Name, new(info.Name, CloudTransfer.Hash(bytes), bytes.LongLength, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()));
        }

        return result;
    }

    public static async Task AtomicJson<T>(string file, T value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, KettleJson.Serialize(value), token);
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
