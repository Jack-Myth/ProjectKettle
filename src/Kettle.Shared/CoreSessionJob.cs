using System.Text.Json.Serialization;

namespace Kettle.Shared;

// Immutable input. Credentials are never serialized; SteamCMD owns authentication.
public sealed record CoreSessionJob(int Version, string RunId, string AppDirectory,
    string AccountName, string GameName, CloudContext Context, int? LaunchOptionIndex);
public sealed record CoreSessionCloudState(string RunId, CloudPreparation Preparation, bool Finished);
public sealed record CoreSessionConnection(string Token, string AccountName, string SteamCmd);
public sealed record CoreSessionSyncResult(string RequestId, string Status, string Message,
    CloudConflictPrompt? Conflict = null, bool NoGcVerified = false, SaveError? Error = null);

// These small persisted contracts favor size over generated serialization fast paths.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SaveError))]
[JsonSerializable(typeof(BackupRetentionReport[]))]
[JsonSerializable(typeof(CoreSessionJob))]
[JsonSerializable(typeof(CoreSessionConnection))]
[JsonSerializable(typeof(CoreSessionCloudState))]
[JsonSerializable(typeof(CoreSessionSyncResult))]
public partial class CoreSessionJson : JsonSerializerContext;
