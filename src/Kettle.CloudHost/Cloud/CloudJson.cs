using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Kettle.Cloud;
// Cloud persistence has an independent resolver so AOT can trim frontend-only contracts.
internal static class CloudJson
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        TypeInfoResolver = CloudJsonContext.Default
    };
    private static readonly JsonSerializerOptions Indented = new()
    {
        TypeInfoResolver = CloudJsonContext.Default,
        WriteIndented = true
    };
    public static string Serialize<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.Serialize(value, (JsonTypeInfo<T>)(options?.WriteIndented == true ? Indented : Compact).GetTypeInfo(typeof(T)));
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)Compact.GetTypeInfo(typeof(T)));
}

// These small persisted contracts favor size over generated serialization fast paths.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(AutoBaseline))]
[JsonSerializable(typeof(AutoTransaction))]
[JsonSerializable(typeof(CloudBaseline))]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(CloudTicket))]
[JsonSerializable(typeof(AutoTicket))]
[JsonSerializable(typeof(CloudVersions))]
[JsonSerializable(typeof(AutoVersions))]
[JsonSerializable(typeof(AutoBeforeInit))]
[JsonSerializable(typeof(AutoDifference))]
[JsonSerializable(typeof(CloudBackupEntry[]))]
[JsonSerializable(typeof(CloudSyncRecovery))]
[JsonSerializable(typeof(AutoConfirmed))]
[JsonSerializable(typeof(AutoLocalManifest))]
[JsonSerializable(typeof(IReadOnlyList<AutoCloudCompiledRule>))]
[JsonSerializable(typeof(CloudFile[]))]
internal partial class CloudJsonContext : JsonSerializerContext;
