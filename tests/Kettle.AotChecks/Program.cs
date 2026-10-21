using System.Text.Json;
using Kettle.Core;

try
{
    await RunChecks(args);
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error);
    Environment.ExitCode = 1;
}

static async Task RunChecks(string[] args)
{
    if (args is ["--help"])
    {
        Console.WriteLine("Publish as Native AOT, then run without arguments for synthetic JSON/backup/ConPTY smoke checks.");
        Console.WriteLine("--verify-failure-reporting : deliberately fail with exit code 1.");
        return;
    }
    if (args.Length != 0 && args is not ["--verify-failure-reporting"] and not ["--terminal-fixture"])
    {
        Console.Error.WriteLine("Unknown/incomplete argument. Use --help; no checks were started.");
        Environment.ExitCode = 2;
        return;
    }
    if (args is ["--verify-failure-reporting"])
        throw new InvalidOperationException("Synthetic assertion for controlled failure reporting.");
    if (args is ["--terminal-fixture"])
    {
        Console.WriteLine("AOT fixture ready");
        Console.WriteLine("AOT reply: " + Console.ReadLine());
        // Keep the fixture alive until the parent observes the reply. Production
        // ConPTY teardown intentionally discards unread output after process exit.
        Console.ReadLine();
        return;
    }

    if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        throw new InvalidOperationException("Publish and run this check as Native AOT.");
    foreach (var language in new[] { "en", "zh-CN" })
    {
        L.Initialize(language);
        foreach (var key in Enum.GetValues<TextId>())
            if (string.IsNullOrEmpty(L.Text(key)))
                throw new InvalidOperationException("Missing AOT localization: " + language + "/" + key);
        Console.WriteLine("PASS all AOT localization resources: " + language);
        Require(L.Text(TextId.UpdateRequired) == (language == "en" ? "Update required" : "需要更新"), "AOT selects requested resource culture");
        Require(L.Format(TextId.InstalledApps, 2, 3) == (language == "en" ? "2 / 3 installed apps" : "2 / 3 个已安装应用"), "AOT resource format arguments");
    }
    L.Initialize("zh-CN");
    var root = Path.Combine(Path.GetTempPath(), "Kettle-AotChecks-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var settings = new PortableSettings(root);
        using var extra = JsonDocument.Parse("{\"retained\":true}");
        settings.Save(new UserSettings("合成测试", DefaultSort: 2) { Extra = new() { ["FutureOption"] = extra.RootElement.Clone() } });
        Require(settings.Load().Username == "合成测试" && settings.Load().Extra!["FutureOption"].GetProperty("retained").GetBoolean(), "settings and unknown fields round trip");
        var baseline = new AutoBaseline("fixture", new()
        {
            ["save.dat"] = "ABC"
        }, 7);
        var transaction = new AutoTransaction(baseline, [new("save.dat", null, "ABC", null, "after.dat", 0, 1)]);
        Require(KettleJson.Deserialize<AutoTransaction>(KettleJson.Serialize(transaction))!.Files[0].AfterHash == "ABC", "AutoCloud recovery round trip");
        Require(KettleJson.Deserialize<CloudBaseline>(KettleJson.Serialize(new CloudBaseline(new()
        {
            ["save.dat"] = "ABC"
        }, 7)))!.ChangeNumber == 7, "cloud baseline round trip");
        Require(KettleJson.Deserialize<ulong>(KettleJson.Serialize(7UL)) == 7, "batch marker round trip");
        var prepared = new CloudPreparation(new(480, 123, "fixture.exe", new(CloudSupport.RemoteStorage, "game.exe", null)), "remote", true)
        {
            Snapshot = new(new() { ["save.dat"] = "local-hash" }, new() { ["save.dat"] = "cloud-hash" })
        };
        var runSnapshot = new RunJournal(new(GamePhase.Uploading, 480, "fixture", "fixture", true, true), prepared, true, false, DateTime.UtcNow);
        var restoredRun = KettleJson.Deserialize<RunJournal>(KettleJson.Serialize(runSnapshot))!;
        Require(restoredRun.Preparation!.Snapshot!.Local["save.dat"] == "local-hash" && restoredRun.Preparation.Snapshot.Cloud["save.dat"] == "cloud-hash", "launch file snapshot survives AOT run-journal recovery");
        Require(KettleJson.Deserialize<OwnedLibrarySnapshot>(KettleJson.Serialize(new OwnedLibrarySnapshot([], true, true, DateTime.UtcNow, "fixture")))!.OwnershipComplete, "legacy library cache round trip");
        var journal = new RunJournal(new(GamePhase.Unknown, 480, "fixture", "fixture", false, true), null, false, false, DateTime.UtcNow);
        using var journalJson = JsonDocument.Parse(KettleJson.Serialize(journal));
        Require(journalJson.RootElement.GetProperty("Status").Deserialize(KettleJsonContext.Default.GameLifecycleStatus)!.NeedsSyncRetry, "recovery status uses generated metadata");
        Require(journalJson.RootElement.GetProperty("Preparation").Deserialize(KettleJsonContext.Default.CloudPreparation) is null, "nullable recovery preparation");
        // Each generic production serialization root must be registered explicitly.
        Check(new CommandFailureReport("fixture", "IOException", DateTime.UtcNow, []));
        Check(new CloudHostRequest("fixture", "AA=="));
        Check(new CloudTicket([new("save.dat", "ABC")], [new("save.dat", "ABC")], 7));
        Check(new AutoTicket([], [], 7));
        Check(new CloudVersions("ticket", "backup", [], []));
        Check(new AutoVersions("ticket", [], "backup"));
        Check(new AutoBeforeInit(480, 1, [], new(), new(), "backup"));
        Check(new AutoDifference("ticket", new(), new(7, []), baseline));
        Check(new CloudBackupEntry[] { new("0.dat", new("save.dat", "ABC", 1, 1)) });
        Check(new AutoConfirmed(baseline, false, false));
        Check(new AutoLocalManifest(new(), new()));
        Check<IReadOnlyList<AutoCloudCompiledRule>>([]);
        Check(Array.Empty<CloudFile>());
        Check(Array.Empty<BackupRetentionReport>());
        Check(CommandChannelStatus.Ready());
        Check(new System.Text.Json.Nodes.JsonObject { ["fixture"] = true });
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        await using (var terminal = new ConPtyTerminal())
        {
            await terminal.StartAsync(Environment.ProcessPath!, ["--terminal-fixture"], timeout.Token);
            var output = new System.Text.StringBuilder();
            var normalizer = new TerminalTextNormalizer();
            bool sent = false, received = false;
            await foreach (var chunk in terminal.ReadAsync(timeout.Token))
            {
                output.Append(normalizer.Feed(chunk));
                if (!sent && output.ToString().Contains("AOT fixture ready"))
                {
                    sent = true;
                    await terminal.WriteLineAsync("synthetic input".AsMemory(), timeout.Token);
                }

                if (!received && output.ToString().Contains("AOT reply: synthetic input"))
                {
                    received = true;
                    await terminal.WriteLineAsync("quit".AsMemory(), timeout.Token);
                }
            }

            Require(received && terminal.ExitCode == 0, "AOT terminal host ConPTY input/output");
        }

        var source = Path.Combine(root, "synthetic-save");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "synthetic fixture");
        var backup = await SaveBackup.CreateAsync(source, Path.Combine(root, "backups"), CancellationToken.None);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "manifest.json")));
        Require(manifest.RootElement.GetProperty("Files")[0].GetProperty("Path").GetString() == "save.dat", "real backup manifest contract");
        // Direct deletion operates only on this test-created temporary tree.
        var deletion = new BackupDelete();
        deletion.Delete(Path.Combine(source, "save.dat"));
        Require(!File.Exists(Path.Combine(source, "save.dat")), "direct file deletion");
        deletion.Delete(backup);
        Require(!Directory.Exists(backup), "direct directory deletion");
        Console.WriteLine("PASS Native AOT smoke checks");
    }
    finally
    {
        Directory.Delete(root, true);
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    static void Check<T>(T value)
    {
        using var document = JsonDocument.Parse(KettleJson.Serialize(value));
        Require(document.RootElement.ValueKind != JsonValueKind.Undefined, typeof(T).Name);
    }
}
