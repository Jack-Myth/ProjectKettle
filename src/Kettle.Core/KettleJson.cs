using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Kettle.Core;
// All persisted contracts are generated at build time; no reflection fallback in AOT.
internal static class KettleJson
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        TypeInfoResolver = KettleJsonContext.Default
    };
    private static readonly JsonSerializerOptions Indented = new()
    {
        TypeInfoResolver = KettleJsonContext.Default,
        WriteIndented = true
    };
    public static string Serialize<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.Serialize(value, (JsonTypeInfo<T>)(options?.WriteIndented == true ? Indented : Compact).GetTypeInfo(typeof(T)));
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)Compact.GetTypeInfo(typeof(T)));
}

internal sealed record CommandFailureReport(string Stage, string ErrorType, DateTime Utc, SteamCommandOutputDiagnostic[] Records);
internal sealed record CloudHostRequest(string Method, string Payload);
internal sealed record BackupEntry(string Path, long Size, string SHA256);
internal sealed record BackupManifest(string Source, List<BackupEntry> Files);
internal sealed record LocalHash(string Key, string Sha1);
internal sealed record RemoteHash(string Name, string Sha1);
internal sealed record CloudTicket(LocalHash[] Local, RemoteHash[] Remote, ulong ChangeNumber);
internal sealed record AutoTicket(LocalHash[] Local, CloudFile[] Remote, ulong ChangeNumber);
internal sealed record CloudVersions(string Ticket, string LocalBackup, CloudFile[] Remote, string[] Conflicts);
internal sealed record AutoVersions(string Ticket, CloudConflictFile[] Files, string LocalBackup);
internal sealed record AutoBeforeInit(uint AppId, uint AccountId, IReadOnlyList<AutoCloudCompiledRule> Plan, Dictionary<string, AutoLocal> Local, Dictionary<string, string> Payloads, string NativeBackup);
internal sealed record AutoDifference(string Ticket, Dictionary<string, AutoLocal> Local, CloudSnapshot Remote, AutoBaseline? Baseline);
internal sealed record CloudBackupEntry(string File, CloudFile Cloud);
internal sealed record AutoConfirmed(AutoBaseline Baseline, bool NativeIndexWritten, bool DeletionsPropagated);
internal sealed record AutoLocalManifest(Dictionary<string, AutoLocal> Files, Dictionary<string, string> Backups);
internal sealed record RunJournal(GameLifecycleStatus Status, CloudPreparation? Preparation, bool GameEndConfirmed, bool CloudSyncConfirmed, DateTime UpdatedUtc);
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(OwnedLibrarySnapshot))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(AutoBaseline))]
[JsonSerializable(typeof(AutoTransaction))]
[JsonSerializable(typeof(CloudBaseline))]
[JsonSerializable(typeof(CommandFailureReport))]
[JsonSerializable(typeof(CloudHostRequest))]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(CloudTicket))]
[JsonSerializable(typeof(AutoTicket))]
[JsonSerializable(typeof(CloudVersions))]
[JsonSerializable(typeof(AutoVersions))]
[JsonSerializable(typeof(AutoBeforeInit))]
[JsonSerializable(typeof(AutoDifference))]
[JsonSerializable(typeof(CloudBackupEntry[]))]
[JsonSerializable(typeof(AutoConfirmed))]
[JsonSerializable(typeof(AutoLocalManifest))]
[JsonSerializable(typeof(RunJournal))]
[JsonSerializable(typeof(IReadOnlyList<AutoCloudCompiledRule>))]
[JsonSerializable(typeof(CloudFile[]))]
[JsonSerializable(typeof(BackupRetentionReport[]))]
[JsonSerializable(typeof(CommandChannelStatus))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
internal partial class KettleJsonContext : JsonSerializerContext;
