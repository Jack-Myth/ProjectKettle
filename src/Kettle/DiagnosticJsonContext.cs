using System.Text.Json.Serialization;
using Kettle.Core;

namespace Kettle;

[JsonSerializable(typeof(GameRuntimeEvent))]
[JsonSerializable(typeof(SteamCommandBoundaryTrace))]
internal partial class DiagnosticJsonContext : JsonSerializerContext;
