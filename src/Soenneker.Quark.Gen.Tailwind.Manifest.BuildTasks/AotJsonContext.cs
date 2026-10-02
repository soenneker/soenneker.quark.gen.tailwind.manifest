using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

[JsonSerializable(typeof(JsonDocument))]
internal partial class AotJsonContext : JsonSerializerContext;
