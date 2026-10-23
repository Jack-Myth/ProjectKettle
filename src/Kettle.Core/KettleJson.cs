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
[JsonSerializable(typeof(BackupRetentionReport[]))]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(OwnedLibrarySnapshot))]
[JsonSerializable(typeof(CommandFailureReport))]
[JsonSerializable(typeof(CommandChannelStatus))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
internal partial class KettleJsonContext : JsonSerializerContext;
