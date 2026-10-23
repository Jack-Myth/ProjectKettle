using System.Runtime;
using System.Text;
using System.Text.Json;
using Kettle.Shared;
using Kettle.Cloud;

internal static class SyncWorker
{
    // This is an allocation budget, not a maximum save size. Never silently fall back to GC.
    private static bool BeginNoGc()
    {
        var small = int.TryParse(Environment.GetEnvironmentVariable("KETTLE_CLOUD_NOGC_SOH_MB"), out var configured) ? configured : 8;
        var total = int.TryParse(Environment.GetEnvironmentVariable("KETTLE_CLOUD_NOGC_MB"), out var configuredTotal) ? configuredTotal : 1024;
        if (small is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(small));
        if (total < small || total is < 128 or > 1536) throw new ArgumentOutOfRangeException(nameof(total));
        return GC.TryStartNoGCRegion((long)total * 1024 * 1024, (long)(total - small) * 1024 * 1024, disallowFullBlockingGC: true);
    }
    public static int NoGcSelfTest()
    {
        try
        {
            if (!BeginNoGc()) return 3;
            Console.WriteLine("NoGCRegion=" + (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion));
            return GCSettings.LatencyMode == GCLatencyMode.NoGCRegion ? 0 : 3;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 3; }
    }

    public static int Run(string[] args)
    {
        // --sync job-directory prepare|finish none|local|cloud ticket-or-dash request-id
        if (args.Length != 6 || args[2] is not ("prepare" or "finish") ||
            !Guid.TryParseExact(args[5], "N", out _) ||
            !Enum.TryParse<ConflictChoice>(args[3], true, out var choice) || !Enum.IsDefined(choice)) return 2;
        var directory = Path.GetFullPath(args[1]);
        var requestId = args[5];
        CoreSessionSyncResult result;
        int exit;
        bool steamRegistered = false;
        try
        {
            if (!BeginNoGc()) throw new IOException("Cannot reserve the configured no-GC budget. Synchronization was not started.");
            var counts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            void Verify()
            {
                if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion ||
                    Enumerable.Range(0, 3).Any(i => GC.CollectionCount(i) != counts[i]))
                    throw new IOException("The no-GC budget expired. The operation is unconfirmed; preserve saves and retry.");
            }
            using var lease = new FileStream(Path.Combine(directory, "sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var job = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "job.json")), CoreSessionJson.Default.CoreSessionJob)
                ?? throw new InvalidDataException("Missing job.");
            if (job.Version != 1 || !Guid.TryParseExact(job.RunId, "N", out _) ||
                Path.GetFileName(directory) != job.RunId || job.Context.AppId == 0)
                throw new InvalidDataException("Invalid job identity.");
            var expected = Path.GetFullPath(Path.Combine(job.AppDirectory, "data", "assistant", job.RunId));
            if (!directory.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected job directory.");
            var ticket = args[4] == "-" ? null : args[4];
            if ((choice == ConflictChoice.None) != (ticket is null)) throw new InvalidDataException("A conflict decision requires its version ticket.");
            var statePath = Path.Combine(directory, "cloud-state.json");
            // File lock also excludes a second run for this Steam account/client/app.
            var lockDirectory = Path.Combine(job.AppDirectory, "data", "assistant");
            var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(job.Context.SteamCmd).ToUpperInvariant() + "|" + job.Context.AccountId + "|" + job.Context.AppId)));
            using var accountLease = new FileStream(Path.Combine(lockDirectory, identity + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var service = new CloudSyncService(job.AppDirectory, (context, token) =>
            {
                Verify();
                var rpc = new NativeCloudRpc(context, Verify);
                steamRegistered = true;
                return Task.FromResult<ICloudRpc>(rpc);
            });
            if (args[2] == "prepare")
            {
                if (File.Exists(Path.Combine(directory, "launch-issued")))
                    throw new IOException("A launch was already attempted. Its snapshot must not be replaced.");
                var preparation = service.PrepareAsync(job.Context, choice, ticket, CancellationToken.None).GetAwaiter().GetResult();
                Verify();
                Atomic(statePath, JsonSerializer.Serialize(new CoreSessionCloudState(job.RunId, preparation, false), CoreSessionJson.Default.CoreSessionCloudState));
            }
            else
            {
                // Written by the native monitor after confirmed start and game completion.
                if (File.ReadAllText(Path.Combine(directory, "game-ended")) != job.RunId)
                    throw new IOException("Game exit has not been confirmed. Upload is blocked.");
                var state = JsonSerializer.Deserialize(File.ReadAllText(statePath), CoreSessionJson.Default.CoreSessionCloudState)
                    ?? throw new InvalidDataException("Missing launch snapshot.");
                if (state.RunId != job.RunId ||
                    JsonSerializer.Serialize(state.Preparation.Context, CoreSessionJson.Default.CloudContext) !=
                    JsonSerializer.Serialize(job.Context, CoreSessionJson.Default.CloudContext))
                    throw new InvalidDataException("Launch snapshot identity mismatch.");
                if (!state.Finished)
                    service.FinishAsync(state.Preparation, choice, ticket, CancellationToken.None).GetAwaiter().GetResult();
                Verify();
                Atomic(statePath, JsonSerializer.Serialize(state with { Finished = true }, CoreSessionJson.Default.CoreSessionCloudState));
            }
            Verify();
            result = new(requestId, "ok", "Synchronization complete.", NoGcVerified: true);
            exit = 0;
        }
        catch (CloudConflictException e)
        {
            result = new(requestId, "conflict", e.Message, new("", 0, e.Message, e.Ticket, e.Files, e.PreservedCopies, SaveErrors.Describe(e)), Error: SaveErrors.Describe(e));
            exit = 10;
        }
        catch (Exception e)
        {
            result = new(requestId, "error", e.Message, Error: SaveErrors.Describe(e));
            exit = 20;
        }
        try
        {
            Atomic(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, CoreSessionJson.Default.CoreSessionSyncResult));
            string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
            var details = result.Conflict is null ? "" : string.Join("; ", result.Conflict.Files.Take(20).Select(f =>
                $"{f.Name}: local={f.Local?.Size.ToString() ?? "missing"} bytes, cloud={f.Cloud?.Size.ToString() ?? "missing"} bytes"));
            Atomic(Path.Combine(directory, "result.ini"), "[result]\r\nRequestId=" + requestId + "\r\nStatus=" + result.Status +
                "\r\nSteamRegistered=" + (steamRegistered ? "1" : "0") + "\r\nMessage=" + OneLine(result.Message + " " + details) + "\r\nTicket=" + result.Conflict?.Ticket + "\r\n", unicode: true);
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 21; }
        // Do not EndNoGCRegion: the OS reclaims the short-lived process allocation.
        return exit;
    }

    private static void Atomic(string path, string content, bool unicode = false)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var encoding = unicode ? Encoding.Unicode : new UTF8Encoding(false);
            var preamble = encoding.GetPreamble();
            stream.Write(preamble);
            stream.Write(encoding.GetBytes(content));
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
