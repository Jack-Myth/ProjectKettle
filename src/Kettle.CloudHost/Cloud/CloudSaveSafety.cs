namespace Kettle.Cloud;

internal static class CloudSaveSafety
{
    internal enum Action { None, Download, Upload, Delete, Conflict }
    internal static CloudRunSnapshot? Normalize(CloudRunSnapshot? run) => run is null ? null : new(new(run.Local, StringComparer.OrdinalIgnoreCase), new(run.Cloud, StringComparer.OrdinalIgnoreCase));

    internal static Action Decide(bool postgame, string name, string? local, string? remote, string? previous,
        bool reupload, ConflictChoice choice, bool stale, CloudRunSnapshot? run)
    {
        if (local == remote) return local is not null && reupload && postgame ? Action.Upload : Action.None;
        if (stale) return Action.Conflict;
        if (local is null)
        {
            if (choice == ConflictChoice.Cloud) return Action.Download;
            if (postgame && run?.Local.ContainsKey(name) == true)
                return run.Cloud.GetValueOrDefault(name) == remote ? Action.Delete : Action.Conflict;
            return Action.Download;
        }
        if (choice == ConflictChoice.Local) return Action.Upload;
        if (choice == ConflictChoice.Cloud) return remote is null ? Action.Conflict : Action.Download;
        if (!postgame) return remote is null && previous is null ? Action.Upload : Action.Conflict;
        if (remote == previous) return Action.Upload;
        if (local == previous && remote is not null) return Action.Download;
        return Action.Conflict;
    }

    internal static IEnumerable<string>? RequiredHistory(IEnumerable<string>? history, CloudRunSnapshot? run, bool postgame)
        => history?.Where(name => !postgame || run?.Local.ContainsKey(name) != true);
    // Absence is not evidence of an intentional deletion. Keep historical names until
    // at least one verified copy exists; otherwise a retry could bless an empty save set.
    internal static void RequireHistoricalCopies(IEnumerable<string>? historical, IEnumerable<string> local, IEnumerable<string> remote)
    {
        var available = local.Concat(remote).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (historical?.Any(name => !available.Contains(name)) == true)
            throw SaveErrors.Tag(new IOException(nameof(SaveErrorCode.HistoricalSavesMissing)), SaveErrorCode.HistoricalSavesMissing);
    }

    internal static string RecoveryFile(string home) => Path.Combine(home, "cloud-sync-recovery.json");

    internal static async Task PreserveFailedAttempt(string home, string backup, CloudBackupRetention retention, CancellationToken token)
    {
        // Keep the first failed attempt's originals across repeated retries and cleanup.
        // Retention reads this reference before it is allowed to remove old backups.
        var path = RecoveryFile(home);
        if (!File.Exists(path))
        {
            // This backup may already be empty after a user deletion. Also keep the
            // preceding complete backup, which may still contain the missing saves.
            var previous = retention.Enforce(preview: true).Kept.Where(p => !p.Equals(backup, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault();
            await CloudSyncService.AtomicCloudJson(path, new CloudSyncRecovery(backup, previous), token);
        }
    }
}
