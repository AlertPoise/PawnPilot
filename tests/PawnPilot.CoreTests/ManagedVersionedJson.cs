using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PawnPilot;

// System.Text.Json is used only by the managed test harness. Production uses Json.NET's
// ShouldSerialize predicates on Snapshot and ActionResult; these converters keep test
// publications on the same versioned wire shape without adding a serializer dependency to DTOs.
internal static class ManagedVersionedJson
{
    internal static JsonSerializerOptions Create(Action<JsonSerializerOptions> configure = null)
    {
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = false };
        options.Converters.Add(new SnapshotVersionConverter());
        options.Converters.Add(new ActionResultVersionConverter());
        configure?.Invoke(options);
        return options;
    }

    private static JsonSerializerOptions Without<TConverter>(JsonSerializerOptions options)
    {
        var copy = new JsonSerializerOptions(options);
        for (int i = copy.Converters.Count - 1; i >= 0; i--)
            if (copy.Converters[i] is TConverter)
                copy.Converters.RemoveAt(i);
        return copy;
    }

    private sealed class SnapshotVersionConverter : JsonConverter<Snapshot>
    {
        public override Snapshot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return JsonSerializer.Deserialize<Snapshot>(document.RootElement.GetRawText(), Without<SnapshotVersionConverter>(options));
        }

        public override void Write(Utf8JsonWriter writer, Snapshot value, JsonSerializerOptions options)
        {
            var node = JsonSerializer.SerializeToNode(value, Without<SnapshotVersionConverter>(options)) as JsonObject;
            if (node == null) { writer.WriteNullValue(); return; }
            if (value.schema_version != Protocol.InteractionSchemaVersion || value.surface == null)
                node.Remove("surface");
            node.WriteTo(writer, options);
        }
    }

    private sealed class ActionResultVersionConverter : JsonConverter<ActionResult>
    {
        public override ActionResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return JsonSerializer.Deserialize<ActionResult>(document.RootElement.GetRawText(), Without<ActionResultVersionConverter>(options));
        }

        public override void Write(Utf8JsonWriter writer, ActionResult value, JsonSerializerOptions options)
        {
            var node = JsonSerializer.SerializeToNode(value, Without<ActionResultVersionConverter>(options)) as JsonObject;
            if (node == null) { writer.WriteNullValue(); return; }
            if (value.schema_version != Protocol.InteractionSchemaVersion || value.interaction_result == null)
                node.Remove("interaction_result");
            node.WriteTo(writer, options);
        }
    }
}
