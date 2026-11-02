using System.Text.Json;
using PawnPilot;

public static class VersionedSerializationTests
{
    public static void Run(Action<string, bool> check)
    {
        var json = ManagedVersionedJson.Create();
        var legacy = Snapshot(3);
        check("jsonnet_v3_snapshot_should_serialize_surface_is_false", !legacy.ShouldSerializesurface());
        var legacyWithSurface = JsonSerializer.Serialize(legacy, json);
        using (var document = JsonDocument.Parse(legacyWithSurface))
            check("wire3_serializer_omits_surface_even_if_present", !document.RootElement.TryGetProperty("surface", out _));

        var v2 = Snapshot(2);
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(v2, json)))
            check("wire2_serializer_omits_surface", !document.RootElement.TryGetProperty("surface", out _));

        var current = Snapshot(4);
        check("jsonnet_v4_snapshot_should_serialize_surface_is_true", current.ShouldSerializesurface());
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(current, json)))
            check("wire4_serializer_keeps_surface", document.RootElement.TryGetProperty("surface", out _));

        var legacyResult = new ActionResult { schema_version = 3, before = Snapshot(3), after = Snapshot(3), interaction_result = Result() };
        check("jsonnet_v3_result_should_serialize_interaction_is_false", !legacyResult.ShouldSerializeinteraction_result());
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(legacyResult, json)))
        {
            check("wire3_result_serializer_omits_interaction_result", !document.RootElement.TryGetProperty("interaction_result", out _));
            check("wire3_result_serializer_omits_nested_snapshot_surfaces",
                !document.RootElement.GetProperty("before").TryGetProperty("surface", out _) &&
                !document.RootElement.GetProperty("after").TryGetProperty("surface", out _));
        }

        var currentResult = new ActionResult { schema_version = 4, before = Snapshot(4), interaction_result = Result() };
        check("jsonnet_v4_result_should_serialize_interaction_is_true", currentResult.ShouldSerializeinteraction_result());
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(currentResult, json)))
        {
            check("wire4_result_serializer_keeps_interaction_result", document.RootElement.TryGetProperty("interaction_result", out _));
            check("wire4_result_serializer_keeps_nested_snapshot_surface",
                document.RootElement.GetProperty("before").TryGetProperty("surface", out _));
        }

        currentResult.interaction_result = null;
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(currentResult, json)))
            check("wire4_result_serializer_omits_absent_interaction_result", !document.RootElement.TryGetProperty("interaction_result", out _));
    }

    private static Snapshot Snapshot(int version) => new Snapshot
    {
        schema_version = version,
        surface = InteractionState.Unavailable()
    };

    private static InteractionResult Result() => new InteractionResult
    {
        input_receipt = "unknown", verification = "unknown", effect_contract = "unknown"
    };
}
