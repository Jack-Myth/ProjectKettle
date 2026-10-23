using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kettle.Shared;
using Kettle.Cloud;

// Short-lived worker: all save operations live here. No legacy per-RPC server.
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--sync") return SyncWorker.Run(args);
            if (args is ["--no-gc-self-test"]) return SyncWorker.NoGcSelfTest();
            if (args is ["--self-test"]) return SelfTest();
            if (args is ["--maintain-backups", var appDirectory])
            {
                Console.WriteLine(JsonSerializer.Serialize(CloudBackupRetention.MaintainAll(appDirectory), CoreSessionJson.Default.BackupRetentionReportArray));
                return 0;
            }
            if (args is ["--backup", var source, var destination])
            {
                Console.WriteLine(SaveBackup.CreateAsync(source, destination, CancellationToken.None).GetAwaiter().GetResult());
                return 0;
            }
            return 2;
        }
        catch (Exception error) { Console.Error.WriteLine(args is ["--self-test"] ? error.ToString() : JsonSerializer.Serialize(SaveErrors.Describe(error), CoreSessionJson.Default.SaveError)); return 1; }
    }

    private static int SelfTest()
    {
        if (RuntimeFeature.IsDynamicCodeSupported || RuntimeInformation.ProcessArchitecture != Architecture.X86)
            throw new InvalidOperationException("Expected x86 Native AOT");
        var result = new CoreSessionSyncResult("fixture", "error", "test", Error: new SaveError(nameof(SaveErrorCode.HistoricalSavesMissing), "detail"));
        if (JsonSerializer.Deserialize(JsonSerializer.Serialize(result, CoreSessionJson.Default.CoreSessionSyncResult), CoreSessionJson.Default.CoreSessionSyncResult) != result)
            throw new InvalidOperationException("Result JSON round trip failed");
        var state = new CloudBaseline(new() { ["save.dat"] = "abc" }, 7);
        if (CloudJson.Deserialize<CloudBaseline>(CloudJson.Serialize(state))?.Hashes["save.dat"] != "abc")
            throw new InvalidOperationException("Cloud JSON round trip failed");
        foreach (var library in new[] { "steam_api.dll", "CSteamworks.dll" })
        {
            var handle = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, library));
            NativeLibrary.Free(handle);
        }
        Console.WriteLine("PASS: x86 Native AOT, worker contracts, cloud persistence, native DLL loading. Steam was not initialized.");
        return 0;
    }
}
