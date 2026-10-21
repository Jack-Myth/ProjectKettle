using Kettle.Core;
using System.Net;
using System.Text;

internal static class RunCloudChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var parser = new GameEventParser();
        check(parser.Feed("\u001b[32mGame pro").Length == 0, "partial process event is not a confirmed start");
        var events = parser.Feed("cess added : AppID 635940 \"path\", ProcID 321\u001b[0m\r\nGame process removed: AppID 635940 x, ProcID 321 \r\n");
        check(events.Length == 2 && events[0] == new GameRuntimeEvent(635940, 321, GameEventKind.Added) && events[1].Kind == GameEventKind.Removed, "streaming ANSI process events retain app and PID");
        check(parser.Feed("AppId 12 launch failed").Single().Kind == GameEventKind.LaunchFailed, "launch failure is not a running event");
        await RunEvents(check);
        foreach (var name in new[]
        {
            "../save",
            "a\\b",
            "NUL",
            "COM1.sav",
            "bad?",
            "bad*",
            "trailing."
        }

        )
        {
            bool rejected = false;
            try
            {
                CloudTransfer.ValidateName(name);
            }
            catch (NotSupportedException)
            {
                rejected = true;
            }

            check(rejected, "reject unsafe cloud filename " + name);
        }

        var raw = CloudWire.Message((1, 9876543210ul), (2, new byte[] { 1, 255 }), (3, "中文"));
        var fields = CloudWire.Parse(raw);
        check(fields.Number(1) == 9876543210ul && fields.Bytes(2).SequenceEqual(new byte[] { 1, 255 }) && fields.Text(3) == "中文", "protobuf bytes and unsigned values roundtrip");
        foreach (var malformed in new byte[][]
        {
            [0],
            [10, 5, 1],
            [8, 128]
        }

        )
        {
            bool rejected = false;
            try
            {
                CloudWire.Parse(malformed);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            check(rejected, "truncated protobuf rejected");
        }

        var rpc = new TransferFixture();
        using var http = new HttpClient(rpc);
        var transfer = new CloudTransfer(12, rpc, http);
        rpc.Server["save.dat"] = "old"u8.ToArray();
        var snapshot = await transfer.ListAsync(default);
        var bytes = await transfer.DownloadAsync(snapshot.Files.Single(), default);
        check(Encoding.UTF8.GetString(bytes) == "old", "network payload is verified against server SHA1 and size");
        rpc.CorruptDownload = true;
        bool corrupt = false;
        try
        {
            await transfer.DownloadAsync(snapshot.Files.Single(), default);
        }
        catch (IOException)
        {
            corrupt = true;
        }

        check(corrupt, "corrupt download is rejected before use");
        rpc.CorruptDownload = false;
        var journals = new List<ulong>();
        await transfer.UploadAsync([new("save.dat", "new"u8.ToArray(), 123, snapshot.Files.Single().Sha1)], b =>
        {
            journals.Add(b);
            return Task.CompletedTask;
        }, default);
        check(journals.SequenceEqual(new ulong[] { 77, 0 }) && rpc.Closed == 1 && rpc.LastBatchSuccess, "successful upload journals and closes its batch");
        check(rpc.RawSha && rpc.ContentHeader && rpc.MethodWasPost && Encoding.UTF8.GetString(rpc.Server["save.dat"]) == "new", "raw SHA1, raw size, server method and content headers honored");
        check(rpc.Commits.SequenceEqual(new[] { true }), "successful HTTP requires file commit and fresh remote verification");
        rpc.FailHttp = true;
        rpc.Commits.Clear();
        var before = rpc.Server["save.dat"].ToArray();
        bool failed = false;
        try
        {
            await transfer.UploadAsync([new("save.dat", "bad"u8.ToArray(), 124, CloudTransfer.Hash(before))], _ => Task.CompletedTask, default);
        }
        catch (HttpRequestException)
        {
            failed = true;
        }

        check(failed && rpc.Closed == 2 && !rpc.LastBatchSuccess && rpc.Commits.SequenceEqual(new[] { false }) && rpc.Server["save.dat"].SequenceEqual(before), "HTTP failure aborts file and always closes batch without false success");
        rpc.FailHttp = false;
        var cancel = new CancellationTokenSource();
        failed = false;
        try
        {
            await transfer.UploadAsync([new("save.dat", "cancel"u8.ToArray(), 125, CloudTransfer.Hash(before))], b =>
            {
                if (b != 0)
                    cancel.Cancel();
                return Task.CompletedTask;
            }, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            failed = true;
        }

        check(failed && rpc.Closed == 3 && !rpc.LastBatchSuccess, "cancellation after batch creation still closes it");
        failed = false;
        var closed = rpc.Closed;
        try
        {
            await transfer.UploadAsync([new("save.dat", "race"u8.ToArray(), 126, "wrong-hash")], _ => Task.CompletedTask, default);
        }
        catch (CloudConflictException)
        {
            failed = true;
        }

        check(failed && rpc.Closed == closed, "changed server hash blocks opening an upload batch");
        rpc.Multipart = true;
        await transfer.UploadAsync([new("save.dat", "chunks"u8.ToArray(), 127, CloudTransfer.Hash(rpc.Server["save.dat"]))], _ => Task.CompletedTask, default);
        check(rpc.LastMethod == "PUT" && Encoding.UTF8.GetString(rpc.Server["save.dat"]) == "chunks", "server PUT block offsets and lengths reconstruct raw payload");
        rpc.Multipart = false;
        rpc.ExplicitBody = "body"u8.ToArray();
        await transfer.UploadAsync([new("save.dat", rpc.ExplicitBody, 128, CloudTransfer.Hash(rpc.Server["save.dat"]))], _ => Task.CompletedTask, default);
        check(Encoding.UTF8.GetString(rpc.Server["save.dat"]) == "body", "explicit server block body takes precedence over unused offset");
        rpc.ExplicitBody = null;
        rpc.FailName = "second.dat";
        failed = false;
        try
        {
            await transfer.UploadAsync([new("first.dat", "one"u8.ToArray(), 129, null), new("second.dat", "two"u8.ToArray(), 129, null)], _ => Task.CompletedTask, default);
        }
        catch (HttpRequestException)
        {
            failed = true;
        }

        check(failed && rpc.Server.ContainsKey("first.dat") && !rpc.Server.ContainsKey("second.dat") && !rpc.LastBatchSuccess, "partial upload preserves committed file but fails and closes whole batch");
        rpc.FailName = null;
        var home = Path.Combine(root, "sync-engine");
        var steam = Path.Combine(home, "host", "steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(steam)!);
        File.WriteAllText(steam, "never executed");
        var remote = Path.Combine(Path.GetDirectoryName(steam)!, "userdata", "123", "12", "remote");
        var memory = new MemoryCloud();
        memory.Server["save.dat"] = "remote-1"u8.ToArray();
        var sync = new CloudSyncService(home, (_, _) => Task.FromResult<ICloudRpc>(new MemoryRpc(remote)), (_, _) => memory, deletion: new TestBackupDelete(root));
        var context = new CloudContext(12, 123, steam, new(CloudSupport.RemoteStorage, "game.exe", null));
        var prepared = await sync.PrepareAsync(context, ConflictChoice.None, null, default);
        check(File.ReadAllText(Path.Combine(remote, "save.dat")) == "remote-1" && Directory.GetFiles(Path.Combine(home, "data"), "baseline.json", SearchOption.AllDirectories).Length == 1, "initial cloud-only save downloads and establishes verified baseline");
        var downloadsBefore = memory.DownloadCount;
        await sync.PrepareAsync(context, ConflictChoice.None, null, default);
        check(memory.DownloadCount == downloadsBefore, "unchanged sync does not download existing identical files");
        File.WriteAllText(Path.Combine(remote, "save.dat"), "local-2");
        await sync.FinishAsync(prepared, ConflictChoice.None, null, default);
        check(Encoding.UTF8.GetString(memory.Server["save.dat"]) == "local-2" && memory.UploadCount == 1, "changed local save uploads after baseline");
        memory.Server["save.dat"] = "remote-3"u8.ToArray();
        string? preflightTicket = null;
        try { await sync.PrepareAsync(context, ConflictChoice.None, null, default); }
        catch (CloudConflictException e) { preflightTicket = e.Ticket; }
        check(preflightTicket is not null && File.ReadAllText(Path.Combine(remote, "save.dat")) == "local-2", "remote-only change requires a conflict decision at launch");
        prepared = await sync.PrepareAsync(context, ConflictChoice.Cloud, preflightTicket, default);
        check(File.ReadAllText(Path.Combine(remote, "save.dat")) == "remote-3", "explicit cloud choice downloads");
        File.WriteAllText(Path.Combine(remote, "save.dat"), "local-conflict");
        memory.Server["save.dat"] = "cloud-conflict"u8.ToArray();
        string? ticket = null;
        try
        {
            await sync.FinishAsync(prepared, ConflictChoice.None, null, default);
        }
        catch (CloudConflictException c)
        {
            ticket = c.Ticket;
            check(c.Files.Length == 1 && c.Files[0].Local?.Size == 14 && c.Files[0].Cloud?.Size == 14 && c.PreservedCopies is not null, "conflict exposes both bound version timestamps, sizes and preserved copy location");
        }

        check(ticket is not null && File.ReadAllText(Path.Combine(remote, "save.dat")) == "local-conflict" && Directory.GetFiles(Path.Combine(home, "data"), "*.cloud", SearchOption.AllDirectories).Length >= 1, "conflict preserves both versions without overwrite");
        bool stale = false;
        try
        {
            await sync.FinishAsync(prepared, ConflictChoice.Cloud, "stale", default);
        }
        catch (CloudConflictException)
        {
            stale = true;
        }

        check(stale, "conflict choice is bound to exact version ticket");
        await sync.FinishAsync(prepared, ConflictChoice.Cloud, ticket, default);
        check(File.ReadAllText(Path.Combine(remote, "save.dat")) == "cloud-conflict", "explicit current cloud conflict choice applies verified payload");
        prepared = await sync.PrepareAsync(context, ConflictChoice.None, null, default);
        memory.FailUpload = true;
        File.WriteAllText(Path.Combine(remote, "save.dat"), "retry-value");
        var baseline = Directory.GetFiles(Path.Combine(home, "data"), "baseline.json", SearchOption.AllDirectories).Single();
        var baselineBefore = File.ReadAllText(baseline);
        failed = false;
        try
        {
            await sync.FinishAsync(prepared, ConflictChoice.None, null, default);
        }
        catch (IOException)
        {
            failed = true;
        }

        check(failed && File.ReadAllText(baseline) == baselineBefore && File.ReadAllText(Path.Combine(remote, "save.dat")) == "retry-value", "failed upload keeps local save and does not advance baseline");
        memory.FailUpload = false;
        await sync.FinishAsync(prepared, ConflictChoice.None, null, default);
        check(Encoding.UTF8.GetString(memory.Server["save.dat"]) == "retry-value", "failed sync retries from unchanged baseline");
        File.Delete(Path.Combine(remote, "save.dat"));
        prepared = await sync.PrepareAsync(context, ConflictChoice.None, null, default);
        check(Encoding.UTF8.GetString(memory.Server["save.dat"]) == "retry-value" && File.ReadAllText(Path.Combine(remote, "save.dat")) == "retry-value", "missing local save is restored without deleting its surviving cloud copy");
        memory.Server["one/save.dat"] = "one"u8.ToArray();
        memory.Server["two/save.dat"] = "two"u8.ToArray();
        await sync.FinishAsync(prepared, ConflictChoice.None, null, default);
        check(File.ReadAllText(Path.Combine(remote, "one", "save.dat")) == "one" && File.ReadAllText(Path.Combine(remote, "two", "save.dat")) == "two", "RemoteStorage downloads nested files with identical basenames");
        prepared = await sync.PrepareAsync(context, ConflictChoice.None, null, default);
        File.WriteAllText(Path.Combine(remote, "one", "save.dat"), "changed");
        await sync.FinishAsync(prepared, ConflictChoice.None, null, default);
        check(Encoding.UTF8.GetString(memory.Server["one/save.dat"]) == "changed" && Encoding.UTF8.GetString(memory.Server["two/save.dat"]) == "two", "nested RemoteStorage upload preserves separate paths and backups");
        var auto = context with
        {
            Metadata = new(CloudSupport.UnsupportedAutoCloud, null, "WinAppDataLocal/Game")
        };
        failed = false;
        try
        {
            await sync.PrepareAsync(auto, ConflictChoice.None, null, default);
        }
        catch (NotSupportedException)
        {
            failed = true;
        }

        check(failed, "AutoCloud is not assumed to use userdata remote");
        var trace = new List<string>();
        var backend = new LifecycleBackend(trace);
        var cloud = new LifecycleCloud(trace);
        var life = new GameLifecycle(backend, cloud, Path.Combine(root, "lifecycle"), steam, "fixture", () => Task.FromResult(123u));
        var states = new List<GameLifecycleStatus>();
        life.Changed += states.Add;
        var game = new Game("Fixture", "12", home, "已安装", "", "");
        var run = life.StartAsync(game);
        await SessionChecks.Until(() => life.Current.Phase == GamePhase.Running);
        check(life.BlocksSessionEnd && !trace.Contains("finish"), "running game blocks logout and cannot trigger upload before confirmed end");
        backend.End.TrySetResult();
        await run;
        check(life.Current.Phase == GamePhase.Completed && trace.IndexOf("prepare") < trace.IndexOf("run") && trace.IndexOf("end") < trace.IndexOf("finish"), "preflight precedes app_run and upload follows confirmed game end");
        check(states.Any(s => s.Phase == GamePhase.Preparing && s.SyncActive) && states.Where(s => s.Phase is GamePhase.Running or GamePhase.Completed).All(s => !s.SyncActive), "cloud activity is limited to actual preflight and postgame sync phases");
        cloud.FailFinish = true;
        backend.End = new();
        run = life.StartAsync(game);
        await SessionChecks.Until(() => life.Current.Phase == GamePhase.Running);
        backend.End.TrySetResult();
        await run;
        check(life.Current.NeedsSyncRetry && life.Current.Phase == GamePhase.Failed, "postgame sync failure exposes retry rather than success");
        cloud.FailFinish = false;
        await life.StartAsync(game with
        {
            AppId = "99"
        });
        check(life.Current.AppId == 12 && life.Current.Phase == GamePhase.Completed && backend.Runs == 2, "retry uses original game and does not launch another selected game");
        backend.FailRunning = true;
        await life.StartAsync(game);
        check(life.Current.Phase == GamePhase.Unknown && !life.Current.NeedsSyncRetry && life.BlocksSessionEnd, "lost running session remains unknown without fabricated end or upload");
        var recovered = new GameLifecycle(backend, cloud, Path.Combine(root, "lifecycle"), steam, "fixture", () => Task.FromResult(123u));
        recovered.LoadRecovery();
        check(recovered.Current.Phase == GamePhase.Unknown && recovered.Current.NeedsSyncRetry, "unfinished durable run record requires explicit checked recovery");
    }

    private static async Task RunEvents(Action<bool, string> check)
    {
        var terminal = new RuntimeTerminal();
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        using var cancel = new CancellationTokenSource();
        bool started = false;
        var run = session.RunGameAsync(12, value => started = value, cancel.Token);
        await SessionChecks.Until(() => terminal.Commands.Contains("app_run 12"));
        terminal.Emit("Game process added : AppID 12 x, ProcID 100 \r\nSteam>");
        await SessionChecks.Until(() => started);
        check(!run.IsCompleted, "prompt during running game is not an end event");
        terminal.Emit("Game process added : AppID 12 child, ProcID 101 \r\nGame process removed: AppID 12 x, ProcID 100 \r\nSteam>");
        await Task.Delay(30);
        check(!run.IsCompleted, "removing one PID does not finish a game with a remaining child");
        var queued = session.QueryMetadataAsync(["apps_running"], TimeSpan.FromSeconds(3));
        await Task.Delay(30);
        check(!terminal.Commands.Contains("apps_running"), "metadata cannot interleave with app_run lifetime");
        cancel.Cancel();
        await Task.Delay(30);
        check(!run.IsCompleted && session.IsAuthenticated && !terminal.Stopped, "cancellation after app_run does not terminate active game or session");
        terminal.Emit("Game process removed: AppID 12 child, ProcID 101 \r\n");
        await Task.Delay(30);
        check(!run.IsCompleted, "last removed PID still waits for command-ready confirmation");
        terminal.Emit("Steam>");
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        await queued;
        check(terminal.Commands.Contains("apps_running"), "confirmed removal and readiness release queued metadata");
        bool failed = false;
        var failure = session.RunGameAsync(13, _ => throw new Exception("failed launch cannot report started"));
        await SessionChecks.Until(() => terminal.Commands.Contains("app_run 13"));
        terminal.Emit("AppId 13 launch failed\r\nSteam>");
        try
        {
            await failure;
        }
        catch (IOException)
        {
            failed = true;
        }

        check(failed && !session.GameCommandActive, "typed launch failure releases run state without success");
        var reads = terminal.Commands.Count(c => c == "apps_running");
        bool backendStarted = false;
        var backendRun = new SteamGameBackend(session, HybridRunChecks.Fast).RunAsync(14, _ => backendStarted = true, default);
        await SessionChecks.Until(() => terminal.Commands.Contains("app_launch 14"));
        await SessionChecks.Until(() => backendStarted);
        terminal.BackendRunning = false;
        terminal.Emit("Game process removed: AppID 14 x, ProcID 140 \r\nSteam>");
        await SessionChecks.Until(() => terminal.Commands.Count(c => c == "apps_running") > reads);
        check(!backendRun.IsCompleted, "removed PID and prompt cannot finish backend before fresh empty snapshots");
        await backendRun.WaitAsync(TimeSpan.FromSeconds(4));
        check(terminal.Commands.Count(c => c == "apps_running") - reads >= 4, "backend confirms running then three complete fresh empty snapshots before returning for upload");
    }

    private sealed class RuntimeTerminal : ISteamTerminal
    {
        private readonly System.Threading.Channels.Channel<string> output = System.Threading.Channels.Channel.CreateUnbounded<string>();
        public System.Collections.Concurrent.ConcurrentBag<string> Commands = new();
        public bool Stopped;
        public bool BackendRunning;
        public void Emit(string text) => output.Writer.TryWrite((text.StartsWith("Game process", StringComparison.Ordinal) ? "\r\n" : "") + text);
        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            var command = line.ToString().Trim();
            Commands.Add(command);
            if (command.StartsWith("login "))
                Emit("Logged in OK\r\nSteam>");
            else if (command == "app_launch 14")
            {
                BackendRunning = true;
                Emit(command + "\r\nGame process added: AppID 14 x, ProcID 140 \r\nSteam>");
            }
            else if (command == "apps_running" && BackendRunning)
                Emit(command + "\r\n" + HybridRunChecks.Row(14, 140) + "\r\nSteam>");
            else if (!command.StartsWith("app_run "))
                Emit(command + "\r\nSteam>");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            output.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            StopAsync();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryRpc(string root) : ICloudRpc
    {
        public string RemoteRoot => root;
        public bool CloudEnabled => true;

        public Task<byte[]> CallAsync(string m, byte[] b, CancellationToken t) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryCloud : ICloudFiles
    {
        public async Task DeleteAsync(IReadOnlyList<CloudDelete> files, Func<ulong, Task> saveBatch, CancellationToken token)
        {
            if (files.Count == 0) return;
            await saveBatch(77);
            foreach (var f in files)
            {
                if (CloudTransfer.Hash(Server[f.Name]) != f.ExpectedRemoteSha1) throw new IOException("Changed cloud save");
                Server.Remove(f.Name);
            }
            await saveBatch(0);
        }
        public Dictionary<string, byte[]> Server = new();
        public int UploadCount, DownloadCount;
        public bool FailUpload;
        public Task<CloudSnapshot> ListAsync(CancellationToken token) => Task.FromResult(new CloudSnapshot(1, Server.Select(p => new CloudFile(p.Key, CloudTransfer.Hash(p.Value), p.Value.Length, 123)).ToArray()));
        public Task<byte[]> DownloadAsync(CloudFile file, CancellationToken token)
        {
            Interlocked.Increment(ref DownloadCount);
            return Task.FromResult(Server[file.Name].ToArray());
        }

        public async Task UploadAsync(IReadOnlyList<CloudUpload> files, Func<ulong, Task> saveBatch, CancellationToken token)
        {
            if (files.Count == 0)
                return;
            await saveBatch(77);
            if (FailUpload)
                throw new IOException("fixture upload failed");
            foreach (var f in files)
            {
                Server[f.Name] = f.Data.ToArray();
                UploadCount++;
            }

            await saveBatch(0);
        }

        public Task CloseBatchAsync(ulong batch, bool success, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class LifecycleBackend(List<string> trace) : IGameBackend
    {
        public TaskCompletionSource End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs, Uninstalls;
        public bool FailRunning;
        public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => Task.FromResult(new AppLaunchMetadata(CloudSupport.RemoteStorage, "game.exe", null));
        public Task WaitIdleAsync(uint id, CancellationToken token) => Task.CompletedTask;
        public async Task RunAsync(uint id, Action<bool> started, CancellationToken token)
        {
            Runs++;
            trace.Add("run");
            started(true);
            if (FailRunning)
                throw new IOException("fixture connection lost");
            await End.Task;
            trace.Add("end");
        }

        public Task UninstallAsync(uint id, CancellationToken token)
        {
            Uninstalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleCloud(List<string> trace) : IGameCloudSync
    {
        public bool FailFinish;
        public Task<CloudPreparation> PrepareAsync(CloudContext c, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            trace.Add("prepare");
            return Task.FromResult(new CloudPreparation(c, null, true));
        }

        public Task FinishAsync(CloudPreparation p, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            trace.Add("finish");
            return FailFinish ? Task.FromException(new IOException("fixture offline")) : Task.CompletedTask;
        }
    }

    private sealed class TransferFixture : HttpMessageHandler, ICloudRpc
    {
        public Dictionary<string, byte[]> Server = new();
        public List<bool> Commits = new();
        public bool FailHttp, CorruptDownload, RawSha, ContentHeader, MethodWasPost, LastBatchSuccess, Multipart;
        public int Closed;
        public byte[]? ExplicitBody;
        public string? FailName, LastMethod;
        private string pendingName = "";
        private byte[] pending = [];
        private byte[]? uploaded;
        public string RemoteRoot => "unused";
        public bool CloudEnabled => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token)
        {
            var f = CloudWire.Parse(request);
            byte[] result;
            switch (method)
            {
                case "Cloud.GetAppFileChangelist#1":
                    result = CloudWire.Message(new[] { (1, (object)1ul) }.Concat(Server.Select(p => (2, (object)CloudWire.Message((1, p.Key), (2, System.Security.Cryptography.SHA1.HashData(p.Value)), (3, 123ul), (4, (ulong)p.Value.Length))))).ToArray());
                    break;
                case "Cloud.ClientFileDownload#1":
                    var b = Server[f.Text(2)];
                    result = CloudWire.Message((1, 12u), (2, (ulong)b.Length), (3, (ulong)b.Length), (4, System.Security.Cryptography.SHA1.HashData(b)), (7, "fixture.example"), (8, "/download/" + f.Text(2)), (9, true));
                    break;
                case "Cloud.BeginAppUploadBatch#1":
                    result = CloudWire.Message((1, 77ul));
                    break;
                case "Cloud.ClientBeginFileUpload#1":
                    pendingName = f.Text(6);
                    pending = f.Bytes(4);
                    uploaded = null;
                    RawSha = pending.Length == 20 && f.Number(2) == f.Number(3) && f.Number(10) == 0 && f.Number(13) == 77;
                    byte[] Block(ulong offset, ulong length) => CloudWire.Message((1, "fixture.example"), (2, "/upload"), (3, true), (4, Multipart ? 4u : 3u), (5, CloudWire.Message((1, "Content-Type"), (2, "application/octet-stream"))), (6, offset), (7, length));
                    result = Multipart ? CloudWire.Message((2, Block(0, 1)), (2, Block(1, f.Number(3) - 1))) : ExplicitBody is not null ? CloudWire.Message((2, CloudWire.Message((1, "fixture.example"), (2, "/upload"), (3, true), (4, 3u), (6, 999ul), (8, ExplicitBody)))) : CloudWire.Message((2, Block(0, f.Number(3))));
                    break;
                case "Cloud.ClientCommitFileUpload#1":
                    var ok = f.Number(1) == 1;
                    Commits.Add(ok);
                    if (ok)
                    {
                        if (uploaded is null || !System.Security.Cryptography.SHA1.HashData(uploaded).SequenceEqual(pending))
                            throw new IOException("bad payload");
                        Server[pendingName] = uploaded;
                    }

                    result = CloudWire.Message((1, true));
                    break;
                case "Cloud.GetSingleFileInfo#1":
                    var data = Server[f.Text(2)];
                    result = CloudWire.Message((4, System.Security.Cryptography.SHA1.HashData(data)), (6, (ulong)data.Length));
                    break;
                case "Cloud.CompleteAppUploadBatchBlocking#1":
                    Closed++;
                    LastBatchSuccess = f.Number(3) == 1;
                    result = [];
                    break;
                default:
                    throw new InvalidOperationException(method);
            }

            return Task.FromResult(result);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
            {
                var b = Server[request.RequestUri!.Segments.Last()];
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(CorruptDownload ? new byte[b.Length] : b)
                };
            }

            MethodWasPost = request.Method == HttpMethod.Post;
            LastMethod = request.Method.Method;
            ContentHeader = request.Content?.Headers.ContentType?.MediaType == "application/octet-stream";
            uploaded = (uploaded ?? []).Concat(await request.Content!.ReadAsByteArrayAsync(token)).ToArray();
            return new(FailHttp || pendingName == FailName ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }
}
