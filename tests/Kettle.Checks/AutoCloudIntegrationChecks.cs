using System.Net;
using System.Text;
using System.Text.Json;
using Kettle.Core;

internal static class AutoCloudIntegrationChecks
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    public static async Task Run(Action<bool, string> check, string root)
    {
        await SteamMatchingRoundTrip(check, root);
        await DeletionRoundTrip(check, root);
        await GameInstallRoundTrip(check, root);
        var f = new Fixture(Path.Combine(root, "auto-e2e"));
        var a = f.Name("sub-a/save.dat");
        var b = f.Name("sub-b/save.dat");
        f.Cloud.Set(a, "cloud-a", 1);
        f.Cloud.Set(b, "cloud-b");
        f.Cloud.Set("%MacHome%Other/save.dat", "foreign", 2);
        var prepared = await f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default);
        check(File.ReadAllText(f.Local(a)) == "cloud-a", "preparation restores nested save before game changes");
        f.Write(a, "game-change");
        f.Write(f.Name("sub-new/save.dat"), "new-file");
        await f.Service.FinishAsync(prepared, ConflictChoice.None, null, default);
        check(f.Cloud.Uploads == 2 && f.Cloud.Text(a) == "game-change" && f.Cloud.Text(f.Name("sub-new/save.dat")) == "new-file", "postgame uploads modified and new nested saves");
        check(File.ReadAllText(f.Local(b)) == "cloud-b" && File.ReadAllText(f.Local(a)) == "game-change" && f.Cloud.Platform(a) == 1 && f.Cloud.Text("%MacHome%Other/save.dat") == "foreign", "same basenames in separate directories and foreign platforms remain distinct; existing platform mask preserved");
        var count = f.Cloud.Uploads;
        prepared = await f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default);
        await f.Service.FinishAsync(prepared, ConflictChoice.None, null, default);
        check(f.Cloud.Uploads == count, "unchanged saves perform no upload on next round");
        check(f.PreInitVerified && f.Cloud.Disposals == 4 && !File.Exists(Path.Combine(Path.GetDirectoryName(f.Remote)!, "remotecache.vdf")), "matched KnownFolder payload backups precede every native connection; no fake native index generated");
        var snapshots = Directory.GetFiles(f.Home, "local-cloud-difference.json", SearchOption.AllDirectories);
        check(snapshots.Length == 3 && f.SeenBackups.Count == 4 && Directory.GetFiles(f.Home, "manifest.json", SearchOption.AllDirectories).Length >= 4, "each phase creates a fresh verified copy while retention keeps the latest three");
        f.Cloud.Reupload = true;
        var reuploadPreparation = await f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default);
        check(f.Cloud.Uploads == count, "launch preparation defers server reupload requests");
        await f.Service.FinishAsync(reuploadPreparation, ConflictChoice.None, null, default);
        check(f.Cloud.Uploads > count, "postgame sends server-requested unchanged local contents");
        f.Cloud.Reupload = false;
        File.Delete(f.Local(a));
        await f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default);
        check(f.Cloud.Text(a) == "game-change" && File.ReadAllText(f.Local(a)) == "game-change" && f.Cloud.Deletes == 0, "baseline-confirmed local absence restores the cloud save instead of deleting it");
        f.Cloud.DeletedName = b;
        f.Cloud.Remove(b);
        var missingCloud = await Conflict(() => f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default));
        check(File.ReadAllText(f.Local(b)) == "cloud-b", "remote tombstone cannot automatically delete the remaining local save");
        await Conflict(() => f.Service.PrepareAsync(f.Context, ConflictChoice.Cloud, missingCloud.Ticket, default));
        check(File.ReadAllText(f.Local(b)) == "cloud-b", "even a bound cloud choice cannot select a missing copy and erase local data");
        var first = new Fixture(Path.Combine(root, "auto-first-conflict"));
        var name = first.Name("save.dat");
        first.Write(name, "local-first");
        first.Cloud.Set(name, "cloud-first");
        var conflict = await Conflict(() => first.Service.PrepareAsync(first.Context, ConflictChoice.None, null, default));
        check(conflict.Files.Length == 1 && conflict.PreservedCopies is not null && Directory.GetFiles(conflict.PreservedCopies, "*.local").Length == 1 && Directory.GetFiles(conflict.PreservedCopies, "*.cloud").Length == 1, "first-run divergent copies reach existing conflict model with separately preserved local and cloud payloads");
        first.Cloud.Set(name, "cloud-later");
        await Conflict(() => first.Service.PrepareAsync(first.Context, ConflictChoice.Local, conflict.Ticket, default));
        check(first.Cloud.Text(name) == "cloud-later" && File.ReadAllText(first.Local(name)) == "local-first", "stale conflict choice does not overwrite either version");
        conflict = await Conflict(() => first.Service.PrepareAsync(first.Context, ConflictChoice.None, null, default));
        await first.Service.PrepareAsync(first.Context, ConflictChoice.Cloud, conflict.Ticket, default);
        check(File.ReadAllText(first.Local(name)) == "cloud-later", "fresh bound cloud choice applies via verified atomic replacement");
        var baseline = first.BaselineBytes();
        first.Cloud.Set(name, "cloud-changed-after-baseline");
        File.Delete(first.Local(name));
        await first.Service.PrepareAsync(first.Context, ConflictChoice.None, null, default);
        check(File.ReadAllText(first.Local(name)) == "cloud-changed-after-baseline" && first.Cloud.Text(name) == "cloud-changed-after-baseline", "missing local save restores the surviving updated cloud contents");
        first.Cloud.Remove(name);
        baseline = first.BaselineBytes();
        await Conflict(() => first.Service.PrepareAsync(first.Context, ConflictChoice.None, null, default));
        check(File.Exists(first.Local(name)) && first.BaselineBytes().SequenceEqual(baseline), "remote absence does not silently delete or resurrect a historical save");
        File.Delete(first.Local(name));
        await Reject(() => first.Service.PrepareAsync(first.Context, ConflictChoice.None, null, default));
        check(first.BaselineBytes().SequenceEqual(baseline), "both sides absent preserves historical baseline and blocks an empty successful sync");
        var failure = new Fixture(Path.Combine(root, "auto-partial-upload"));
        a = failure.Name("a.dat");
        b = failure.Name("b.dat");
        failure.Cloud.Set(a, "a0");
        failure.Cloud.Set(b, "b0");
        var prep = await failure.Service.PrepareAsync(failure.Context, ConflictChoice.None, null, default);
        baseline = failure.BaselineBytes();
        failure.Write(a, "a1");
        failure.Write(b, "b1");
        failure.Cloud.FailUploadName = b;
        await Reject(() => failure.Service.FinishAsync(prep, ConflictChoice.None, null, default));
        check(failure.BaselineBytes().SequenceEqual(baseline) && failure.Cloud.Text(a) == "a1" && failure.Cloud.Text(b) == "b0", "partial cloud commit keeps prior baseline and all local payloads for retry");
        failure.Cloud.FailUploadName = null;
        await failure.Service.FinishAsync(prep, ConflictChoice.None, null, default);
        check(failure.Cloud.Text(b) == "b1" && !failure.BaselineBytes().SequenceEqual(baseline), "retry rereads partially committed cloud state and finishes remaining upload without losing edits");
        prep = await failure.Service.PrepareAsync(failure.Context, ConflictChoice.None, null, default);
        failure.Write(a, "a2");
        baseline = failure.BaselineBytes();
        failure.Cloud.FailBatchClose = true;
        await Reject(() => failure.Service.FinishAsync(prep, ConflictChoice.None, null, default));
        check(failure.BaselineBytes().SequenceEqual(baseline) && Directory.GetFiles(failure.Home, "autocloud-unfinished-batch.json", SearchOption.AllDirectories).Length == 1, "unacknowledged batch closure retains batch journal and old baseline");
        failure.Cloud.FailBatchClose = false;
        await failure.Service.FinishAsync(prep, ConflictChoice.None, null, default);
        check(Directory.GetFiles(failure.Home, "autocloud-unfinished-batch.json", SearchOption.AllDirectories).Length == 0, "subsequent session closes unfinished batch and verifies state before advancing baseline");
        prep = await failure.Service.PrepareAsync(failure.Context, ConflictChoice.None, null, default);
        failure.Write(a, "local-race");
        baseline = failure.BaselineBytes();
        var lists = failure.Cloud.Lists;
        failure.Cloud.OnList = n =>
        {
            if (n == lists + 3)
                failure.Cloud.Set(a, "other-device");
        };
        await Reject(() => failure.Service.FinishAsync(prep, ConflictChoice.None, null, default));
        failure.Cloud.OnList = null;
        check(failure.Cloud.Text(a) == "other-device" && File.ReadAllText(failure.Local(a)) == "local-race" && failure.BaselineBytes().SequenceEqual(baseline), "cloud race immediately before upload blocks overwrite and baseline advance");
        var rollback = new Fixture(Path.Combine(root, "auto-local-rollback"));
        a = rollback.Name("a.dat");
        b = rollback.Name("b.dat");
        rollback.Cloud.Set(a, "a0");
        rollback.Cloud.Set(b, "b0");
        await rollback.Service.PrepareAsync(rollback.Context, ConflictChoice.None, null, default);
        baseline = rollback.BaselineBytes();
        rollback.Cloud.Set(a, "a-new");
        rollback.Cloud.Set(b, "b-new");
        var rollbackConflict = await Conflict(() => rollback.Service.PrepareAsync(rollback.Context, ConflictChoice.None, null, default));
        FileStream? locked = null;
        lists = rollback.Cloud.Lists;
        rollback.Cloud.OnList = n =>
        {
            if (n == lists + 3)
                locked = new FileStream(rollback.Local(b), FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        try
        {
            await Reject(() => rollback.Service.PrepareAsync(rollback.Context, ConflictChoice.Cloud, rollbackConflict.Ticket, default));
        }
        finally
        {
            locked?.Dispose();
            rollback.Cloud.OnList = null;
        }

        check(File.ReadAllText(rollback.Local(a)) == "a0" && File.ReadAllText(rollback.Local(b)) == "b0" && rollback.BaselineBytes().SequenceEqual(baseline), "second atomic replacement failure rolls first file back; multi-file update never reports success");
        await rollback.Service.PrepareAsync(rollback.Context, ConflictChoice.Cloud, rollbackConflict.Ticket, default);
        check(File.ReadAllText(rollback.Local(a)) == "a-new" && File.ReadAllText(rollback.Local(b)) == "b-new", "checked retry applies both files after local failure clears");
        await InterruptedJournal(check, rollback, a, b);
        var multi = new Fixture(Path.Combine(root, "auto-multiple-roots"), multipleRoots: true);
        var lowName = multi.Name("same.dat");
        var docsName = "%WinMyDocuments%Other/76561197960265851/same.dat";
        multi.Cloud.Set(lowName, "low");
        multi.Cloud.Set(docsName, "docs");
        var nativeIndex = Path.Combine(Path.GetDirectoryName(multi.Remote)!, "remotecache.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(nativeIndex)!);
        File.WriteAllText(nativeIndex, "native-existing-index");
        await multi.Service.PrepareAsync(multi.Context, ConflictChoice.None, null, default);
        check(File.ReadAllText(multi.Local(lowName)) == "low" && File.ReadAllText(multi.Local(docsName)) == "docs" && multi.Local(lowName) != multi.Local(docsName), "multiple KnownFolder roots retain separate equal basenames in one verified transaction");
        check(File.ReadAllText(nativeIndex) == "native-existing-index" && Directory.GetFiles(multi.Home, "remotecache.vdf", SearchOption.AllDirectories).Length == 2, "existing native index is backed up before init and not overwritten with flat root-zero metadata");
        var init = new Fixture(Path.Combine(root, "auto-native-init-change"));
        var initName = init.Name("save.dat");
        init.Cloud.Set(initName, "original");
        await init.Service.PrepareAsync(init.Context, ConflictChoice.None, null, default);
        var initBaseline = init.BaselineBytes();
        init.BeforeConnect = () => init.Write(initName, "native-side-effect");
        await Reject(() => init.Service.PrepareAsync(init.Context, ConflictChoice.None, null, default));
        check(init.BaselineBytes().SequenceEqual(initBaseline) && init.Cloud.Uploads == 0 && File.ReadAllText(init.Local(initName)) == "native-side-effect", "native-init payload change aborts without overwriting the unexpected version; original payload backup remains");
        var invalid = new Fixture(Path.Combine(root, "auto-unsafe-rules"));
        await Reject(() => invalid.Service.PrepareAsync(invalid.Context with { Metadata = invalid.Context.Metadata with { AutoCloudAppInfo = invalid.Info.Replace("WinAppDataLocalLow", "UnknownRoot") } }, ConflictChoice.None, null, default));
        check(invalid.Cloud.Connections == 0, "unknown metadata fails before native initialization or touching game paths");
        var unknownMode = invalid.Context with
        {
            Metadata = invalid.Context.Metadata with
            {
                Cloud = (CloudSupport)999
            }
        };
        await Reject(() => invalid.Service.PrepareAsync(unknownMode, ConflictChoice.None, null, default));
        await Reject(() => invalid.Service.FinishAsync(new(unknownMode, null, true), ConflictChoice.None, null, default));
        check(invalid.Cloud.Connections == 0, "unknown cloud mode in prepare or recovered finish never falls back to flat RemoteStorage");
        var localOnly = new Fixture(Path.Combine(root, "auto-first-local-only"));
        var onlyName = localOnly.Name("save.dat");
        localOnly.Write(onlyName, "first-local");
        var localPreparation = await localOnly.Service.PrepareAsync(localOnly.Context, ConflictChoice.None, null, default);
        check(localOnly.Cloud.Uploads == 0 && File.ReadAllText(localOnly.Local(onlyName)) == "first-local", "explicit initial local choice preserves local data without pregame upload");
        await localOnly.Service.FinishAsync(localPreparation, ConflictChoice.None, null, default);
        check(localOnly.Cloud.Text(onlyName) == "first-local", "initial local copy is uploaded during postgame sync");
        baseline = localOnly.BaselineBytes();
        localOnly.Cloud.OnList = _ => throw new IOException("synthetic enumeration failure");
        await Reject(() => localOnly.Service.PrepareAsync(localOnly.Context, ConflictChoice.None, null, default));
        localOnly.Cloud.OnList = null;
        check(localOnly.BaselineBytes().SequenceEqual(baseline) && localOnly.Cloud.Deletes == 0, "cloud enumeration error never masquerades as empty cloud or advances baseline");
        var linked = new Fixture(Path.Combine(root, "auto-hardlink"));
        var linkName = linked.Name("save.dat");
        var source = Path.Combine(linked.Home, "outside-rule.dat");
        File.WriteAllText(source, "outside-data");
        Directory.CreateDirectory(Path.GetDirectoryName(linked.Local(linkName))!);
        if (!CreateHardLinkW(linked.Local(linkName), source, 0))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        await Reject(() => linked.Service.PrepareAsync(linked.Context, ConflictChoice.None, null, default));
        check(linked.Cloud.Connections == 0 && File.ReadAllText(source) == "outside-data", "hard-linked matched file is rejected before init and never exposes or overwrites its outside target");
        await ProtocolAndOverride(check, root);
    }

    private static async Task InterruptedJournal(Action<bool, string> check, Fixture f, string a, string b)
    {
        var state = Path.GetDirectoryName(Directory.GetFiles(f.Home, "autocloud-baseline.json", SearchOption.AllDirectories).Single())!;
        var recovery = Path.Combine(state, "synthetic-crash");
        Directory.CreateDirectory(recovery);
        var oldA = File.ReadAllBytes(f.Local(a));
        var oldB = File.ReadAllBytes(f.Local(b));
        f.Cloud.Set(a, "after-crash-a");
        f.Cloud.Set(b, "after-crash-b");
        async Task WriteJournal()
        {
            var expected = System.Text.Json.Nodes.JsonNode.Parse(f.BaselineBytes())!;
            expected["ChangeNumber"] = f.Cloud.Change;
            expected["Hashes"]![a] = CloudTransfer.Hash(Bytes("after-crash-a"));
            expected["Hashes"]![b] = CloudTransfer.Hash(Bytes("after-crash-b"));
            var items = new List<object>();
            int i = 0;
            foreach (var (name, before, after) in new[]
            {
                (a, oldA, Bytes("after-crash-a")),
                (b, oldB, Bytes("after-crash-b"))
            }

            )
            {
                var beforePath = Path.Combine(recovery, i + ".before");
                var afterPath = Path.Combine(recovery, i++ + ".after");
                await File.WriteAllBytesAsync(beforePath, before);
                await File.WriteAllBytesAsync(afterPath, after);
                var temporary = Path.Combine(Path.GetDirectoryName(f.Local(name))!, ".kettle-" + Guid.NewGuid().ToString("N") + ".tmp");
                if (name == a)
                    await File.WriteAllTextAsync(temporary, "partial-scratch");
                items.Add(new
                {
                    WireName = name,
                    BeforeHash = CloudTransfer.Hash(before),
                    AfterHash = CloudTransfer.Hash(after),
                    BeforeFile = beforePath,
                    AfterFile = afterPath,
                    BeforeTime = 123,
                    AfterTime = 124,
                    TemporaryFile = temporary
                });
            }

            await File.WriteAllTextAsync(Path.Combine(state, "autocloud-local-transaction.json"), JsonSerializer.Serialize(new
            {
                ExpectedBaseline = expected,
                Files = items
            }));
        }

        await WriteJournal();
        f.Write(a, "user-edit-after-crash");
        var baseline = f.BaselineBytes();
        var connections = f.Cloud.Connections;
        await Reject(() => f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default));
        check(File.ReadAllText(f.Local(a)) == "user-edit-after-crash" && f.Cloud.Connections == connections && f.BaselineBytes().SequenceEqual(baseline) && File.Exists(Path.Combine(state, "autocloud-local-transaction.json")), "crash recovery refuses unrelated edits before connecting; transaction and baseline retained");
        f.Write(a, "after-crash-a");
        bool sawRolledBack = false;
        f.BeforeConnect = () => sawRolledBack = File.ReadAllBytes(f.Local(a)).SequenceEqual(oldA) && File.ReadAllBytes(f.Local(b)).SequenceEqual(oldB);
        var recoveredConflict = await Conflict(() => f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default));
        await f.Service.PrepareAsync(f.Context, ConflictChoice.Cloud, recoveredConflict.Ticket, default);
        f.BeforeConnect = null;
        check(sawRolledBack && File.ReadAllText(f.Local(a)) == "after-crash-a" && File.ReadAllText(f.Local(b)) == "after-crash-b" && !File.Exists(Path.Combine(state, "autocloud-local-transaction.json")), "restart rolls back known partial transaction before init then redownloads and confirms both new versions");
        check(Directory.GetFiles(Path.Combine(state, "recovered-scratch")).Any(p => File.ReadAllText(p) == "partial-scratch") && f.Cloud.Server.Keys.All(p => !p.Contains(".kettle-")), "journal-owned partial temporary file is preserved separately and never uploaded as a game save");
    }

    private static async Task ProtocolAndOverride(Action<bool, string> check, string root)
    {
        var f = new Fixture(Path.Combine(root, "auto-override"), true);
        var wire = f.Name("sub/save.dat");
        f.Cloud.Set(wire, "override");
        await f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default);
        check(f.Local(wire).Contains("WindowsOverride") && File.ReadAllText(f.Local(wire)) == "override" && !Directory.Exists(Path.Combine(f.Low, "Game")), "Windows root override writes transformed local path but keeps original logical wire root");
        f.Write(wire, "changed");
        var changedConflict = await Conflict(() => f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default));
        var prep = await f.Service.PrepareAsync(f.Context, ConflictChoice.Local, changedConflict.Ticket, default);
        check(f.Cloud.LastUploadName is null, "launch preflight does not send an upload");
        var outside = Path.Combine(Path.GetDirectoryName(f.Local(wire))!, "excluded.bak");
        File.WriteAllText(outside, "untouched");
        await f.Service.FinishAsync(prep, ConflictChoice.None, null, default);
        check(f.Cloud.LastUploadName == wire && f.Cloud.LastUploadPlatforms == uint.MaxValue, "postgame upload sends complete root/path filename and explicit rule-derived platform metadata");
        check(File.ReadAllText(outside) == "untouched" && f.Cloud.Server.Keys.All(k => !k.EndsWith(".bak")), "files outside literal rule mask are never uploaded or replaced");
        f.Cloud.BadPrefix = true;
        await Reject(() => f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default));
        f.Cloud.BadPrefix = false;
        f.Cloud.Delta = true;
        await Reject(() => f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default));
        f.Cloud.Delta = false;
        f.Cloud.Tombstone = true;
        await Reject(() => f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default));
        f.Cloud.Tombstone = false;
        check(f.Cloud.Deletes == 0, "invalid prefix/delta/tombstone fail explicitly without deletion requests");
        var casing = new Fixture(Path.Combine(root, "auto-wire-casing"));
        var canonical = casing.Name("save.dat");
        var exact = canonical.Replace("WinAppDataLocalLow", "winappdatalocallow");
        casing.Cloud.Set(exact, "old");
        await casing.Service.PrepareAsync(casing.Context, ConflictChoice.None, null, default);
        casing.Write(canonical, "new");
        var casingConflict = await Conflict(() => casing.Service.PrepareAsync(casing.Context, ConflictChoice.None, null, default));
        var casingPreparation = await casing.Service.PrepareAsync(casing.Context, ConflictChoice.Local, casingConflict.Ticket, default);
        await casing.Service.FinishAsync(casingPreparation, ConflictChoice.None, null, default);
        check(casing.Cloud.LastUploadName == exact && casing.Cloud.Server.Count == 1 && casing.Cloud.Text(exact) == "new", "Windows logical comparison never rewrites the exact existing server wire-name spelling");
        using var historical = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "autocloud-history.json")));
        var h = historical.RootElement;
        var info = AutoCloudPathChecks.AppInfo(1030300, h.GetProperty("Ufs"));
        var plan = AutoCloudPathPlan.Compile(AutoCloudDefinition.Parse(1030300, info), new(220538249, 76561198180803977), AutoCloudPlatform.Windows, new Roots(Path.Combine(root, "historical-low"), Path.Combine(root, "historical-docs")));
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "autocloud-server-catalog.json")));
        var rpc = new CatalogRpc(catalog.RootElement.Clone());
        var listing = await new CloudTransfer(1030300, rpc, autoCloud: plan).ListAsync(default);
        check(listing.Files.Length == 27 && listing.Files.Count(x => x.Name.Contains("/Restore_Points1/")) == 23 && listing.Files[0].Name.StartsWith("%WinAppDataLocalLow%Team Cherry/"), "actual saved server prefix/index fixture yields 27 canonical wire names and 23 nested mappings; no real payload or cloud access");
    }

    private static async Task<CloudConflictException> Conflict(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (CloudConflictException conflict)
        {
            return conflict;
        }

        throw new Exception("Expected bound conflict");
    }

    private static async Task Reject(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or HttpRequestException)
        {
            return;
        }

        throw new Exception("Expected safe failure");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, nint security);
    private sealed class Roots(string low, string docs) : IAutoCloudRoots
    {
        public string Resolve(string root) => root switch
        {
            "WinAppDataLocalLow" => low,
            "WinMyDocuments" => docs,
            _ => throw new NotSupportedException(root)
        };
    }

    private static async Task GameInstallRoundTrip(Action<bool, string> check, string root)
    {
        var f = new Fixture(Path.Combine(root, "gameinstall-full-flow"), gameInstall: true);
        var wire = f.Name("nested/save.dat");
        f.Cloud.Set(wire, "cloud-first");
        var prepared = await f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default);
        check(File.ReadAllText(f.Local(wire)) == "cloud-first" && f.PreInitVerified, "GameInstall prepares exact installation directory");
        f.Write(wire, "game-change");
        await f.Service.FinishAsync(prepared, ConflictChoice.None, null, default);
        check(f.Cloud.Text(wire) == "game-change" && f.Plan.Rules.All(r => r.LocalRoot == f.Install), "GameInstall uploads from exact installation context");
        f.Cloud.Set(wire, "next-cloud");
        var conflict = await Conflict(() => f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default));
        prepared = await f.Service.PrepareAsync(f.Context, ConflictChoice.Cloud, conflict.Ticket, default);
        f.Write(wire, "next-local");
        f.Cloud.FailUploadName = wire;
        await Reject(() => f.Service.FinishAsync(prepared, ConflictChoice.None, null, default));
        f.Cloud.FailUploadName = null;
        prepared = JsonSerializer.Deserialize(JsonSerializer.Serialize(prepared, CoreSessionJson.Default.CloudPreparation), CoreSessionJson.Default.CloudPreparation)!;
        await f.Service.FinishAsync(prepared, ConflictChoice.None, null, default);
        check(f.Cloud.Text(wire) == "next-local" && prepared.Context.InstallPath == f.Install, "postgame retry retains installation context in persisted preparation");
        var missing = new Fixture(Path.Combine(root, "gameinstall-missing-context"), gameInstall: true);
        await Reject(() => missing.Service.PrepareAsync(missing.Context with { InstallPath = null }, ConflictChoice.None, null, default));
        check(missing.Cloud.Connections == 0 && !Directory.GetFiles(missing.Home, "before-init.json", SearchOption.AllDirectories).Any(), "missing installation context aborts before scanning/backup or opening native cloud");
    }

    private sealed class Fixture
    {
        public readonly string Home, Steam, Remote, Low, Docs, Info, Install;
        public readonly Transport Cloud;
        public readonly CloudContext Context;
        public readonly CloudSyncService Service;
        public readonly AutoCloudPathPlan Plan;
        public bool PreInitVerified = true;
        public Action? BeforeConnect;
        public HashSet<string> SeenBackups = [];
        public Fixture(string home, bool @override = false, bool multipleRoots = false, bool gameInstall = false, string pattern = "*.dat", bool recursive = true)
        {
            Home = home;
            Low = Path.Combine(home, "low");
            Docs = Path.Combine(home, "docs");
            Steam = Path.Combine(home, "steam", "steamcmd.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(Steam)!);
            File.WriteAllText(Steam, "never executed");
            Remote = Path.Combine(Path.GetDirectoryName(Steam)!, "userdata", "123", "42", "remote");
            Info = "\"42\" { \"ufs\" { \"savefiles\" { \"0\" { \"root\" \"WinAppDataLocalLow\" \"path\" \"Game/{Steam3AccountID}\" \"pattern\" \"*.dat\" \"recursive\" \"1\" } } " + (@override ? "\"rootoverrides\" { \"0\" { \"root\" \"WinAppDataLocalLow\" \"os\" \"Windows\" \"useinstead\" \"WinMyDocuments\" \"addpath\" \"WindowsOverride\" } }" : "") + " } }";
            if (multipleRoots)
                Info = Info.Replace(" } } ", " } \"1\" { \"root\" \"WinMyDocuments\" \"path\" \"Other/{64BitSteamID}\" \"pattern\" \"*.dat\" \"recursive\" \"1\" } } ");
            Install = Path.Combine(home, "Library B with spaces", "common", "Fixture Game");
            if (gameInstall)
            {
                Directory.CreateDirectory(Install);
                Info = Info.Replace("WinAppDataLocalLow", "gameinstall");
            }
            Info = Info.Replace("*.dat", pattern);
            if (!recursive)
                Info = Info.Replace("\"recursive\" \"1\"", "\"recursive\" \"0\"");

            Context = new(42, 123, Steam, AppLaunchMetadataReader.Read(42, Info))
            {
                InstallPath = gameInstall ? Install : null
            };
            var roots = new Roots(Low, Docs);
            Plan = AutoCloudPathPlan.Compile(AutoCloudDefinition.Parse(42, Info), new(123, 76561197960265851), AutoCloudPlatform.Windows, roots, Context.InstallPath);
            Cloud = new Transport(Remote);
            var http = new HttpClient(Cloud);
            Service = new(home, (_, _) =>
            {
                Cloud.Connections++;
                var copies = Directory.GetFiles(home, "before-init.json", SearchOption.AllDirectories);
                var fresh = copies.Where(p => !SeenBackups.Contains(p)).ToArray();
                PreInitVerified &= fresh.Length == 1 && copies.Length <= 3;
                if (fresh.Length == 1)
                    SeenBackups.Add(fresh[0]);
                BeforeConnect?.Invoke();
                return Task.FromResult<ICloudRpc>(new RpcLease(Cloud));
            }, (_, rpc) => new CloudTransfer(42, rpc, http, Plan), roots, new TestBackupDelete(Path.GetDirectoryName(home)!));
        }

        public string Name(string suffix) => "%" + Plan.Rules[0].CloudRoot + "%Game/123/" + suffix;
        public string Local(string wire) => Plan.ToLocal(AutoCloudWire.Key(wire));
        public void Write(string wire, string text)
        {
            var path = Local(wire);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public byte[] BaselineBytes() => File.ReadAllBytes(Directory.GetFiles(Home, "autocloud-baseline.json", SearchOption.AllDirectories).Single());
    }

    private static async Task SteamMatchingRoundTrip(Action<bool, string> check, string root)
    {
        var local = new Fixture(Path.Combine(root, "auto-windows-short-names"), pattern: "*.sav", recursive: false);
        var save = local.Name("GameData-Record-0-0.v2.sav");
        var backup = local.Name("GameData-Record-0-0.v2.sav-bak");
        var ignored = local.Name("excluded.txt");
        var nested = local.Name("sub/save.sav");
        local.Write(save, "save");
        local.Write(backup, "backup");
        local.Write(ignored, "leave alone");
        local.Write(nested, "nonrecursive");
        var shortName = new StringBuilder(1024);
        if (GetShortPathNameW(local.Local(backup), shortName, (uint)shortName.Capacity) == 0)
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        bool shortMatches = Path.GetExtension(shortName.ToString()).Equals(".sav", StringComparison.OrdinalIgnoreCase);
        var preparation = await local.Service.PrepareAsync(local.Context, ConflictChoice.None, null, default);
        check(preparation.Snapshot!.Local.ContainsKey(save) && preparation.Snapshot.Local.ContainsKey(backup) == shortMatches,
            "native discovery includes .sav-bak exactly when its existing Windows 8.3 alias matches *.sav");
        check(!preparation.Snapshot.Local.ContainsKey(ignored) && !preparation.Snapshot.Local.ContainsKey(nested), "native discovery keeps filename and nonrecursive boundaries");
        await local.Service.FinishAsync(preparation, ConflictChoice.None, null, default);
        check(local.Cloud.Server.ContainsKey(backup) == shortMatches && local.Cloud.Server.ContainsKey(save) && !local.Cloud.Server.ContainsKey(ignored) && !local.Cloud.Server.ContainsKey(nested), "postgame upload uses the same native-discovered long filenames");

        var cloud = new Fixture(Path.Combine(root, "auto-cloud-existing-names"), @override: true, pattern: "*.sav", recursive: false);
        var wire = "%WinAppDataLocalLow%PreviousDirectory/nested/slot.legacy";
        var foreign = "%MacAppSupport%PreviousDirectory/foreign.legacy";
        cloud.Cloud.Set(wire, "cloud copy", 1);
        cloud.Cloud.Set(foreign, "Mac only", 2);
        var downloaded = await cloud.Service.PrepareAsync(cloud.Context, ConflictChoice.None, null, default);
        check(File.ReadAllText(cloud.Local(wire)) == "cloud copy" && cloud.Local(wire).Contains("WindowsOverride"), "existing cloud names outside mask, recursion and save directories download through the root override");
        check(downloaded.Snapshot!.Local.Count == 1 && downloaded.Snapshot.Local.ContainsKey(wire) && !downloaded.Snapshot.Cloud.ContainsKey(foreign), "downloaded names remain tracked and foreign-platform roots are skipped before resolution");
        await cloud.Service.FinishAsync(downloaded, ConflictChoice.None, null, default);
        var again = await cloud.Service.PrepareAsync(cloud.Context, ConflictChoice.None, null, default);
        check(again.Snapshot!.Local.ContainsKey(wire) && cloud.Cloud.Uploads == 0 && cloud.Cloud.Deletes == 0, "out-of-mask downloads survive final verification, postgame and a fresh preflight without false deletion");
        cloud.Write(wire, "local edit");
        var conflict = await Conflict(() => cloud.Service.PrepareAsync(cloud.Context, ConflictChoice.None, null, default));
        check(conflict.Files.Single().Local is not null && Directory.GetFiles(cloud.Home, "local-*.dat", SearchOption.AllDirectories).Length > 0, "tracked out-of-mask local edits are backed up and enter normal conflict handling");
        var keep = await cloud.Service.PrepareAsync(cloud.Context, ConflictChoice.Local, conflict.Ticket, default);
        await cloud.Service.FinishAsync(keep, ConflictChoice.None, null, default);
        check(cloud.Cloud.Text(wire) == "local edit" && cloud.Cloud.Text(foreign) == "Mac only", "tracked existing cloud names upload after game exit while foreign-platform data stays intact");

        var existing = new Fixture(Path.Combine(root, "auto-cloud-existing-target"), pattern: "*.sav", recursive: false);
        var old = existing.Name("slot.legacy");
        existing.Write(old, "local original");
        existing.Cloud.Set(old, "remote original");
        var firstConflict = await Conflict(() => existing.Service.PrepareAsync(existing.Context, ConflictChoice.None, null, default));
        check(File.ReadAllText(Path.Combine(firstConflict.PreservedCopies!, "0.local")) == "local original" && Directory.GetDirectories(existing.Home, "cloud-targets-before", SearchOption.AllDirectories).Length == 1,
            "first-seen cloud targets outside the discovery mask preserve existing local contents before replacement");
        await existing.Service.PrepareAsync(existing.Context, ConflictChoice.Cloud, firstConflict.Ticket, default);
        check(File.ReadAllText(existing.Local(old)) == "remote original", "bound cloud choice replaces an out-of-mask target through the verified transaction");

        var platform = new Fixture(Path.Combine(root, "auto-rule-upload-platforms"), pattern: "*.sav");
        var windowsFile = platform.Name("windows.txt");
        platform.Write(windowsFile, "Windows payload");
        var mixedInfo = platform.Info.Replace("\"savefiles\" {", "\"savefiles\" { \"1\" { \"root\" \"WinAppDataLocalLow\" \"path\" \"Game/{Steam3AccountID}\" \"pattern\" \"*.txt\" \"platforms\" { \"0\" \"Windows\" } }");
        var mixedContext = platform.Context with { Metadata = AppLaunchMetadataReader.Read(42, mixedInfo) };
        var mixedPreparation = await platform.Service.PrepareAsync(mixedContext, ConflictChoice.None, null, default);
        await platform.Service.FinishAsync(mixedPreparation, ConflictChoice.None, null, default);
        check(platform.Cloud.LastUploadPlatforms == 1, "new uploads retain the matching discovery rule's platform flags when another rule shares its root");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder shortPath, uint length);

    private static async Task DeletionRoundTrip(Action<bool, string> check, string root)
    {
        var f = new Fixture(Path.Combine(root, "auto-delete-protocol"));
        var name = f.Name("save.dat");
        f.Cloud.Set(name, "original");
        var prepared = await f.Service.PrepareAsync(f.Context, ConflictChoice.None, null, default);
        File.Delete(f.Local(name));
        f.Cloud.FailBatchClose = true;
        await Reject(() => f.Service.FinishAsync(prepared, ConflictChoice.None, null, default));
        check(f.Cloud.Deletes == 1 && !f.Cloud.Server.ContainsKey(name), "delete is committed before failed batch acknowledgement");
        f.Cloud.FailBatchClose = false;
        prepared = JsonSerializer.Deserialize(JsonSerializer.Serialize(prepared, CoreSessionJson.Default.CloudPreparation), CoreSessionJson.Default.CloudPreparation)!;
        await f.Service.FinishAsync(prepared, ConflictChoice.None, null, default);
        check(f.Cloud.Deletes == 1 && !File.Exists(f.Local(name)), "persisted snapshot retries delete batch without deleting twice or restoring file");
    }

    private sealed class RpcLease(Transport owner) : ICloudRpc
    {
        public string RemoteRoot => owner.RemoteRoot;
        public bool CloudEnabled => true;

        public Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token) => owner.CallAsync(method, request, token);
        public ValueTask DisposeAsync()
        {
            owner.Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CatalogRpc(JsonElement catalog) : ICloudRpc
    {
        public string RemoteRoot => "unused";
        public bool CloudEnabled => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token)
        {
            var fields = new List<(int, object)>
            {
                (1, catalog.GetProperty("ChangeNumber").GetUInt64())
            };
            fields.AddRange(catalog.GetProperty("Prefixes").EnumerateArray().Select(p => (4, (object)p.GetString()!)));
            foreach (var f in catalog.GetProperty("Files").EnumerateArray())
                fields.Add((2, CloudWire.Message((1, f.GetProperty("Name").GetString()!), (2, Convert.FromHexString(f.GetProperty("Sha1").GetString()!)), (3, f.GetProperty("Timestamp").GetUInt64()), (4, f.GetProperty("Size").GetUInt64()), (6, f.GetProperty("Platforms").GetUInt32()), (7, f.GetProperty("PrefixIndex").ValueKind == JsonValueKind.Null ? 0u : f.GetProperty("PrefixIndex").GetUInt32()))));
            return Task.FromResult(CloudWire.Message(fields.ToArray()));
        }
    }

    private sealed class Transport(string remote) : HttpMessageHandler
    {
        public Dictionary<string, (byte[] Bytes, uint Platforms)> Server = new();
        public int Uploads, Lists, Connections, Disposals, Deletes;
        public ulong Change = 1;
        public string RemoteRoot => remote;

        public string? FailUploadName, LastUploadName;
        public uint LastUploadPlatforms;
        public bool FailBatchClose, BadPrefix, Delta, Tombstone, Reupload;
        public string? DeletedName;
        public Action<int>? OnList;
        private string pending = "", downloading = "";
        private byte[] uploaded = [];
        private uint platform;
        public string Text(string name) => Encoding.UTF8.GetString(Server[name].Bytes);
        public uint Platform(string name) => Server[name].Platforms;
        public void Set(string name, string value, uint platforms = uint.MaxValue)
        {
            Server[name] = (Bytes(value), platforms);
            Change++;
        }

        public void Remove(string name)
        {
            Server.Remove(name);
            Change++;
        }

        public Task<byte[]> CallAsync(string method, byte[] request, CancellationToken token)
        {
            var f = CloudWire.Parse(request);
            byte[] result;
            switch (method)
            {
                case "Cloud.GetAppFileChangelist#1":
                    OnList?.Invoke(++Lists);
                    var prefixes = Server.Keys.Select(n => n[..(n.LastIndexOf('/') + 1)]).Distinct().ToArray();
                    var fields = new List<(int, object)>
                    {
                        (1, Change),
                        (3, Delta)
                    };
                    fields.AddRange(prefixes.Select(p => (4, (object)p)));
                    if (DeletedName is not null)
                    {
                        fields.Add((4, ""));
                        fields.Add((2, CloudWire.Message((1, DeletedName), (5, 2u), (7, (uint)prefixes.Length))));
                    }
                    foreach (var p in Server)
                        fields.Add((2, CloudWire.Message((1, p.Key[(p.Key.LastIndexOf('/') + 1)..]), (2, System.Security.Cryptography.SHA1.HashData(p.Value.Bytes)), (3, 123ul), (4, (uint)p.Value.Bytes.Length), (5, Tombstone ? 1u : 0u), (6, p.Value.Platforms), (7, BadPrefix ? 999u : (uint)Array.IndexOf(prefixes, p.Key[..(p.Key.LastIndexOf('/') + 1)])), (9, Reupload))));
                    result = CloudWire.Message(fields.ToArray());
                    break;
                case "Cloud.ClientFileDownload#1":
                    downloading = f.Text(2);
                    var d = Server[downloading].Bytes;
                    result = CloudWire.Message((1, 42u), (2, (uint)d.Length), (3, (uint)d.Length), (4, System.Security.Cryptography.SHA1.HashData(d)), (7, "fixture.example"), (8, "/download"), (9, true));
                    break;
                case "Cloud.BeginAppUploadBatch#1":
                    if (f.Any(p => p.Number == 4))
                        Deletes++;
                    result = CloudWire.Message((1, 77ul));
                    break;
                case "Cloud.ClientBeginFileUpload#1":
                    pending = f.Text(6);
                    platform = (uint)f.Number(7);
                    LastUploadName = pending;
                    LastUploadPlatforms = platform;
                    result = CloudWire.Message((2, CloudWire.Message((1, "fixture.example"), (2, "/upload"), (3, true), (4, 4u), (6, 0ul), (7, f.Number(3)))));
                    break;
                case "Cloud.ClientDeleteFile#1":
                    Remove(f.Text(2));
                    result = [];
                    break;
                case "Cloud.ClientCommitFileUpload#1":
                    if (f.Number(1) != 0)
                    {
                        Server[pending] = (uploaded.ToArray(), platform);
                        Change++;
                        Uploads++;
                    }

                    result = CloudWire.Message((1, true));
                    break;
                case "Cloud.GetSingleFileInfo#1":
                    var s = Server[f.Text(2)].Bytes;
                    result = CloudWire.Message((2, 42u), (3, f.Text(2)), (4, System.Security.Cryptography.SHA1.HashData(s)), (6, (uint)s.Length));
                    break;
                case "Cloud.CompleteAppUploadBatchBlocking#1":
                    if (FailBatchClose)
                        throw new IOException("synthetic missing batch acknowledgement");
                    result = [];
                    break;
                default:
                    throw new Exception("Unexpected RPC " + method);
            }

            return Task.FromResult(result);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Server[downloading].Bytes)
                };
            uploaded = await request.Content!.ReadAsByteArrayAsync(token);
            return new(pending == FailUploadName ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }
}
