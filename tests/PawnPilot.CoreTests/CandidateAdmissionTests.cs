using PawnPilot;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class CandidateAdmissionTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private static Snapshot Fixture(string name = "m3a-sort")
    {
        var state = JsonSerializer.Deserialize<Snapshot>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name + ".json")), Json);
        // The old wire sort fixture omits the two unused counters. Production projection
        // always emits all three; the managed admission policy requires that structure.
        if (name == "m3a-sort")
            foreach (string counter in new[] { "front", "back" })
                state.business.details.containers.Add(new ContainerState { container = counter, completeness = "complete" });
        if (name == "m3a-prepare_sale")
        {
            var a = state.legal_actions[0].arguments;
            var item = state.business.details.items.Find(x => x.item_ref == a.item_refs[0]);
            a.collection_fingerprint = BusinessCollection.Fingerprint(state.world_epoch, a.customer_ref, "buy",
                new[] { new BusinessCollectionMember { item_ref = item.item_ref, container = "player", units = item.units, owned = item.owned } },
                "unknown", "unverified", null);
        }
        return state;
    }
    private static LegalAction Sort() => new() { action = "sort_inventory_default", arguments = new() { container = "player" } };
    private static void Day(Snapshot state, string stage, bool transition = false, bool report = false)
    {
        state.business.details.day = new DayState { day_number = 3, stage = stage,
            transition_pending = transition, report_open = report, remaining_customers = 0 };
    }
    private static Snapshot Admit(Snapshot state, LegalAction action)
    {
        state.legal_actions.Clear();
        CandidateAdmission.TryAdd(state, state.legal_actions, action);
        state.interaction.operable = state.interaction.consistency != "blocked" && state.legal_actions.Count > 0;
        CandidateAdmission.EnsureValid(state);
        return state;
    }
    private static bool Throws(Action action)
    {
        try { action(); return false; } catch (InvalidOperationException) { return true; }
    }

    public static void Run(Action<string, bool> check)
    {
        foreach (var pair in Cases())
        {
            CandidateAdmission.EnsureValid(pair.Value);
            check("m5a_publication_" + pair.Key, ActionGate.IsSnapshotValid(pair.Value));
        }
        var state = Fixture(); Day(state, "open", transition: true);
        check("m5a_native_sortable_dto_transition_rejected", CandidateAdmission.Validate(state, Sort()) == "day_ui_blocked");
        Admit(state, Sort());
        check("m5a_sort_block_reason_preserved", state.legal_actions.Count == 0 &&
            state.business.details.candidates.Any(x => x.action == "sort_inventory_default" && x.status == "blocked" && x.reason == "day_ui_blocked"));
        state = Fixture(); state.business.details.customer.phase = "leaving";
        check("m5a_departing_actor_blocks_sort_and_receive", CandidateAdmission.Validate(state, Sort()) != null &&
            CandidateAdmission.Validate(state, new LegalAction { action = "receive_next_customer", arguments = new() }) != null);
        state = Fixture("m3a-prepare_sale");
        check("m5a_present_customer_blocks_sort", CandidateAdmission.Validate(state, Sort()) != null);
        foreach (var ui in new[] { "blocked", "updating" })
        {
            state = Fixture(); state.business.details.ui.status = ui;
            check("m5a_sort_rejects_" + ui + "_without_named_blocker", CandidateAdmission.Validate(state, Sort()) == "ui_scope_unverified");
        }
        state = Fixture(); state.business.details.ui.blockers.Add("dialogue_active");
        check("m5a_sort_dialogue_blocker", CandidateAdmission.Validate(state, Sort()) == "known_ui_blocker");
        state = Fixture(); Day(state, "open", transition: true);
        check("m5a_assembled_invalid_action_is_not_silently_filtered", Throws(() => CandidateAdmission.EnsureValid(state)) && state.legal_actions.Count == 1);
        state = Fixture(); state.business.details.items = null;
        check("m5a_bad_observation_is_not_masked_by_candidate_filter", Throws(() => CandidateAdmission.EnsureValid(state)));
        state = Fixture(); var malformed = Sort(); malformed.arguments.mode = "invented";
        check("m5a_malformed_native_candidate_is_programming_error", Throws(() => CandidateAdmission.TryAdd(state, state.legal_actions, malformed)));
        state = Fixture(); state.legal_actions.Add(Sort());
        check("m5a_duplicate_candidate_is_not_silently_deduped", Throws(() => CandidateAdmission.EnsureValid(state)));
        state = Fixture("v3-dialogue-snapshot"); var choice = state.legal_actions[0];
        choice.arguments.customer_ref = "stale-customer";
        check("m5a_customer_replacement_invalidates_choice", CandidateAdmission.Validate(state, choice) == "choice_not_visible");
        state = Purchase(); var purchase = state.legal_actions[0]; state.legal_actions.Clear();
        state.business.details.eligibility.purchase_enabled = false;
        check("m5a_purchase_checks_final_preflight_facts", CandidateAdmission.TryAddVerifiedPurchase(state, purchase) &&
            state.business.details.eligibility.purchase_enabled && state.legal_actions.Count == 1);
        state = Purchase(); purchase = state.legal_actions[0]; state.legal_actions.Clear(); Day(state, "open", true);
        check("m5a_purchase_transition_cannot_leave_eligibility_enabled", !CandidateAdmission.TryAddVerifiedPurchase(state, purchase) &&
            !state.business.details.eligibility.purchase_enabled && state.business.details.eligibility.missing.Contains("day_ui_blocked"));
        state = Fixture(); var gate = new ActionGate(); var driver = new ActionDriver(gate);
        var command = new Command { schema_version = 3, id = "sort-inflight", broker_epoch = "b", action = "sort_inventory_default",
            arguments = Sort().arguments, expected_world_epoch = state.world_epoch, expected_version = state.version,
            expected_revision = state.revision, start_deadline_unix_ms = 100, execution_timeout_ms = 100 };
        gate.Accept(command, state, "b", 1); int calls = 0;
        gate.Advance("b", state, 2, 2, _ => calls++, (_, _, _) => VerificationDecision.Pending());
        var step = driver.Tick(() => { }, () => { Day(state, "open", true); CandidateAdmission.EnsureValid(state); return state; },
            "b", 3, 3, _ => calls++, (_, _, _) => VerificationDecision.Succeeded());
        check("m5a_publication_fault_after_invocation_stays_unknown", step.failed && gate.PendingResult?.status == "outcome_unknown" && calls == 1);
        gate.Acknowledge(command.id);
        check("m5a_publication_fault_unknown_ack_does_not_unlock", gate.IsWriteBlocked);
    }

    public static Dictionary<string, Snapshot> Cases()
    {
        var result = new Dictionary<string, Snapshot>();
        result["sort_open"] = Admit(Fixture(), Sort());
        var s = Fixture(); s.phase = "MORNING"; Day(s, "morning"); result["sort_morning"] = Admit(s, Sort());
        s = Fixture(); s.business.details.ui.status = "unverified"; s.business.details.ui.blockers.Add("global_ui_stack_unobservable");
        result["sort_known_roots"] = Admit(s, Sort());
        foreach (string stage in new[] { "open", "transition", "settlement", "next_morning" })
        {
            s = Fixture(); Day(s, stage == "transition" ? "unavailable" : stage, transition: stage == "open" || stage == "transition", report: stage == "next_morning");
            result["sort_blocked_" + stage] = Admit(s, Sort());
        }
        foreach (string blocker in new[] { "dialogue_active", "day_transition", "nightly_report", "end_of_day", "generic_confirmation" })
        {
            s = Fixture(); s.business.details.ui.status = "blocked"; s.business.details.ui.blockers.Add(blocker);
            s.interaction.consistency = "blocked"; result[blocker] = Admit(s, Sort());
        }
        foreach (string name in new[] { "m3a-prepare_sale", "m3a-sell", "v3-dialogue-snapshot" })
        {
            s = Fixture(name); var candidate = s.legal_actions[0];
            result[name] = Admit(s, candidate);
            s = Fixture(name); candidate = s.legal_actions[0]; Day(s, "open", true);
            result[name + "_transition"] = Admit(s, candidate);
        }
        s = Purchase(); var p = s.legal_actions[0]; s.legal_actions.Clear();
        CandidateAdmission.TryAddVerifiedPurchase(s, p); result["purchase"] = s;
        s = Purchase(); p = s.legal_actions[0]; s.legal_actions.Clear(); Day(s, "open", true);
        CandidateAdmission.TryAddVerifiedPurchase(s, p); s.interaction.operable = false; result["purchase_transition"] = s;
        s = Fixture(); s.phase = "MORNING"; Day(s, "next_morning", report: true);
        s.business.details.ui.blockers.Add("nightly_report"); s.business.details.ui.status = "blocked";
        s.interaction.consistency = "blocked";
        result["report_close_remains_legal"] = Admit(s, new LegalAction { action = "close_day_report", arguments = new() });
        s = Fixture(); s.menu_open = true; Day(s, "next_morning", report: true);
        s.can_close_menu = !CandidateAdmission.DayBlocksOrdinary(s); s.legal_actions.Clear();
        s.interaction.operable = false; CandidateAdmission.EnsureValid(s); result["report_menu_suppressed"] = s;
        s = Fixture(); s.menu_open = true; s.can_close_menu = true;
        result["ordinary_close_menu"] = Admit(s, new LegalAction { action = "close_menu", arguments = new() });
        return result;
    }

    private static Snapshot Purchase()
    {
        var s = Fixture("m3a-sell"); s.business.details.customer.trade_type = "sell";
        foreach (var item in s.business.details.items.Where(x => x.container == "back")) item.owned = false;
        var p = s.legal_actions[0]; p.action = "purchase_visible_offer"; p.arguments.max_spend = p.arguments.amount;
        s.business.details.capacity.status = "sufficient";
        s.business.details.eligibility.purchase_enabled = true; s.business.details.eligibility.missing.Clear();
        return s;
    }

    public static string Export()
    {
        var root = JsonSerializer.SerializeToNode(Cases(), Json);
        void Clean(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (string key in new[] { "day", "inspection_handoff", "bargain", "request_text", "trade_panel" })
                    if (obj.ContainsKey(key) && obj[key] == null) obj.Remove(key);
                if (obj["arguments"] is JsonObject a) foreach (var pair in a.ToArray()) if (pair.Value == null) a.Remove(pair.Key);
                foreach (var pair in obj.ToArray()) if (pair.Value != null) Clean(pair.Value);
            }
            else if (node is JsonArray array) foreach (var value in array) if (value != null) Clean(value);
        }
        Clean(root); return root.ToJsonString();
    }
}
