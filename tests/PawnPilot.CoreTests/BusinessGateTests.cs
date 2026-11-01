using PawnPilot;
using System.Text.Json;

public static class BusinessGateTests
{
    public static void Run(Action<string, bool> check)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "v3-business-observation.json")));
        var options = new JsonSerializerOptions { IncludeFields = true };
        bool canonical = true;
        foreach (var sample in fixture.RootElement.GetProperty("fingerprint_cases").EnumerateArray())
        {
            var request = JsonSerializer.Deserialize<Command>(sample.GetProperty("request").GetRawText(), options);
            canonical &= ActionGate.CanonicalRequestJson(request) == sample.GetProperty("canonical_json").GetString() &&
                ActionGate.Fingerprint(request) == sample.GetProperty("sha256").GetString();
        }
        check("v3_all_argument_fingerprints_match_python_fixture", canonical);
        var before = State(1, true);
        var command = Command(before);
        check("v3_snapshot_keeps_explicit_partial_business", ActionGate.IsSnapshotValid(before));
        var gate = new ActionGate();
        check("v3_close_menu_accepted_by_same_gate", gate.Accept(command, before, "broker", 1) == null);
        int calls = 0;
        gate.Advance("broker", before, 2, 2, _ => calls++, ActionGate.VerifyCloseMenu);
        before.business.details.eligibility.missing.Clear();
        var after = State(2, false);
        gate.Advance("broker", after, 3, 3, _ => calls++, ActionGate.VerifyCloseMenu);
        var result = gate.PendingResult;
        check("v3_result_has_own_wire_and_immutable_business", result?.status == "succeeded" && result.schema_version == 3 &&
            result.before.business.details.eligibility.missing.Count == 4 && calls == 1);
        check("v3_ack_same_id_never_invokes_twice", gate.Acknowledge("v3-close") &&
            gate.Accept(command, State(1, true), "broker", 4).reason == "command_already_acknowledged" && calls == 1);

        var mixed = Command(State(1, true)); mixed.schema_version = 2;
        var mixedResult = new ActionGate().Accept(mixed, State(1, true), "broker", 1);
        check("v3_cannot_accept_v2_envelope", mixedResult.reason == "schema_version_mismatch" && mixedResult.before == null);
        var old = State(1, true); old.schema_version = 2;
        check("v2_rejects_new_business_fields", !ActionGate.IsSnapshotValid(old));

        var unknown = new ActionGate(); var original = State(1, true);
        unknown.Accept(Command(original), original, "broker", 1);
        unknown.Advance("broker", original, 2, 2, _ => throw new InvalidOperationException(), ActionGate.VerifyCloseMenu);
        check("v3_native_exception_stays_unknown_after_ack", unknown.PendingResult?.status == "outcome_unknown" &&
            unknown.Acknowledge("v3-close") && unknown.IsWriteBlocked);
        var economic = Command(original); economic.action = "purchase_visible_offer";
        check("v3_purchase_not_in_dispatch_allowlist", new ActionGate().Accept(economic, original, "broker", 1).reason == "unknown_action");
        var extra = Command(original); extra.arguments.max_spend = 40;
        check("v3_close_rejects_business_args", new ActionGate().Accept(extra, original, "broker", 1).reason == "invalid_arguments");
        var fingerprint = ActionGate.Fingerprint(extra); extra.arguments.max_spend = 41;
        check("v3_fingerprint_binds_new_numeric_args", fingerprint != ActionGate.Fingerprint(extra));
        var invalidGate = new ActionGate();
        invalidGate.Accept(Command(original), original, "broker", 1);
        invalidGate.Advance("broker", original, 2, 2, _ => { }, ActionGate.VerifyCloseMenu);
        var invalidAfter = State(2, false); invalidAfter.business.details.items = null;
        var invalidResult = invalidGate.Advance("broker", invalidAfter, 3, 3, _ => { },
            (_, _, _) => VerificationDecision.Succeeded()).result;
        check("invalid_v3_post_sample_cannot_be_waived_by_verifier", invalidResult.status == "outcome_unknown" &&
            invalidResult.after == null && invalidGate.IsWriteBlocked);
    }

    private static Command Command(Snapshot state) => new Command
    {
        schema_version = 3, id = "v3-close", broker_epoch = "broker", action = "close_menu", arguments = new ActionArguments(),
        expected_world_epoch = state.world_epoch, expected_revision = state.revision, expected_version = state.version,
        start_deadline_unix_ms = 100, execution_timeout_ms = 5000
    };
    private static Snapshot State(int revision, bool open) => new Snapshot
    {
        schema_version = 3, world_epoch = "world", revision = revision, version = "world:" + revision,
        loaded = true, ready = true, phase = "MORNING", scene = "InventoryScene", cash = 550,
        menu_open = open, can_close_menu = open,
        interaction = new InteractionObservation { phase = "MORNING", consistency = "unverified", operable = false },
        business = new BusinessObservation { support = "unsupported", item_refs = new List<string>(), details = new BusinessDetails() },
        legal_actions = open ? new List<LegalAction> { new LegalAction { action = "close_menu", arguments = new ActionArguments() } } : new List<LegalAction>()
    };
}
