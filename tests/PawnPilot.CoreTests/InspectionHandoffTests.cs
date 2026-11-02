using PawnPilot;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class InspectionHandoffTests
{
    private static readonly string[] Identifiers = { "packet_red_cigarette", "water_pitcher", "water_jug", "bottled_water",
        "small_bottled_water", "large_bottled_water", "mini_bottle" };

    public static void Run(Action<string, bool> check)
    {
        foreach (string id in Identifiers)
        {
            var p = new BusinessProjection();
            var s = Capture(p, Input(id), 1);
            check("inspection_production_pipeline_" + id, p.IsInspectionWaiting && ActionGate.IsSnapshotValid(s) &&
                s.business.details.inspection_handoff.reason == "manual_item_inspection" &&
                s.legal_actions.Single().action == "resume_inspection_handoff" && !s.business.details.eligibility.purchase_enabled);
            string encoded = JsonSerializer.Serialize(s.business.details.inspection_handoff, new JsonSerializerOptions { IncludeFields = true });
            check("inspection_no_identifier_or_result_export_" + id, !encoded.Contains(id) && !encoded.Contains("identifier") && !encoded.Contains("fake"));
        }
        var real = Capture(new BusinessProjection(), Input("packet_red_cigarette", "Visible packet"), 1);
        var variant = Capture(new BusinessProjection(), Input("packet_red_cigarette", "Visible packet variant"), 1);
        check("inspection_same_family_real_and_fake_input_have_identical_public_reason", real.business.details.inspection_handoff.reason == variant.business.details.inspection_handoff.reason);
        check("inspection_new_projection_never_reuses_old_handoff_reference", real.business.details.inspection_handoff.handoff_ref != variant.business.details.inspection_handoff.handoff_ref);
        var crowded = Input("water_jug");
        // Stay inside the existing 8192-character input cap while exceeding the escaped
        // business-wire budget. The handoff itself must remain available for confirmation.
        crowded.Containers[1].Items = Enumerable.Range(0, 64).Select(index => new BusinessItemInput { Identity = new object(),
            Identifier = "water_jug", Name = new string('w', index < 8 ? 256 : 100), Units = 1 }).ToArray();
        crowded.Quote = new BusinessQuoteInput();
        var crowdedProjection = new BusinessProjection();
        var bounded = crowdedProjection.Capture("w", true, () => crowded);
        check("inspection_large_batch_keeps_all_refs_and_bounded_complete_names_in_valid_fallback", bounded.details.status == "unavailable" &&
            BusinessWire.Validate(bounded) && bounded.details.inspection_handoff.items.Count == 64 &&
            bounded.details.inspection_handoff.items.Sum(item => item.name?.Length ?? 0) == 2048 &&
            bounded.details.inspection_handoff.items.Count(item => item.name == null) == 56);
        var ordinary = Capture(new BusinessProjection(), Input("box_tampon", "Visible water"), 1);
        check("inspection_same_name_different_identifier_not_classified", ordinary.business.details.inspection_handoff == null);
        foreach (string id in new[] { "cigarette_guide", "cigarette_color", "water_test_strip", "bottled_water_premium", "water_ration" })
            check("inspection_confirmed_tool_or_excluded_product_not_classified_" + id,
                Capture(new BusinessProjection(), Input(id), 1).business.details.inspection_handoff == null);
        var buyerInput = Input("water_jug"); buyerInput.Customer.TradeType = "buy";
        var buyer = Capture(new BusinessProjection(), buyerInput, 1);
        check("inspection_npc_buy_never_classifies", buyer.business.details.inspection_handoff == null);
        var ownedInput = Input("water_jug"); ownedInput.Containers[1].Items[0].Owned = true;
        check("inspection_owned_goods_not_incoming", Capture(new BusinessProjection(), ownedInput, 1).business.details.inspection_handoff == null);

        var p1 = new BusinessProjection(); var input = Input("water_jug");
        var first = Capture(p1, input, 1); string reference = first.business.details.inspection_handoff.handoff_ref;
        check("inspection_same_item_observation_does_not_replace_pending", Capture(p1, input, 2).business.details.inspection_handoff.handoff_ref == reference);
        var unavailable = p1.Capture("w", false, () => throw new Exception());
        check("inspection_pending_survives_not_ready", unavailable.details.inspection_handoff?.handoff_ref == reference && BusinessWire.Validate(unavailable));
        var absent = Input("box_tampon"); absent.Customer = new BusinessCustomerInput { Presence = "absent", Phase = "absent", DialogueStage = "absent" };
        absent.Containers[1].Items = Array.Empty<BusinessItemInput>(); absent.Quote = new BusinessQuoteInput();
        var left = Capture(p1, absent, 3);
        check("inspection_manual_departure_preserves_historical_refs", left.business.customer_ref == null &&
            left.business.details.inspection_handoff.customer_ref == first.business.customer_ref && left.business.details.inspection_handoff.handoff_ref == reference);
        check("inspection_wrong_world_or_stale_ref_cannot_resume", !p1.ResumeInspectionHandoff("old", reference) && !p1.ResumeInspectionHandoff("w", "stale"));
        check("inspection_resume_after_manual_departure", p1.ResumeInspectionHandoff("w", reference));
        var resumed = Capture(p1, absent, 4);
        var command = Resume(first, "resume-1");
        check("inspection_resume_postcondition_does_not_require_current_customer_quote_or_cash", BusinessActionPolicy.Postcondition(command, first, resumed));
        resumed.cash += 9;
        check("inspection_resume_ack_ignores_manual_economic_changes", BusinessActionPolicy.Postcondition(command, first, resumed));
        check("inspection_same_pending_cannot_resume_twice", !p1.ResumeInspectionHandoff("w", reference));
        var reused = Capture(p1, input, 5);
        check("inspection_departed_and_reused_visit_object_retriggers", reused.business.details.inspection_handoff.requires_manual_inspection);
        string reusedRef = reused.business.details.inspection_handoff.handoff_ref;
        p1.ResumeInspectionHandoff("w", reusedRef);
        p1.Capture("w", false, () => input);
        check("inspection_same_visit_item_not_retriggered_after_ui_ref_regeneration", Capture(p1, input, 5).business.details.inspection_handoff.status == "resumed");
        command = Resume(reused, "resume-reused");
        var added = new BusinessItemInput { Identity = new object(), Identifier = "water_pitcher", Name = "New water", Units = 1 };
        input.Containers[1].Items = new[] { input.Containers[1].Items[0], added };
        var second = Capture(p1, input, 6);
        check("inspection_new_actual_item_retriggers_without_old_item", second.business.details.inspection_handoff.handoff_ref != reference &&
            second.business.details.inspection_handoff.items.Count == 1 && second.business.details.inspection_handoff.items[0].name == "New water" &&
            second.business.details.inspection_handoff.last_resumed_handoff_ref == reusedRef);
        check("inspection_previous_ack_proves_resume_even_new_pending", BusinessActionPolicy.Postcondition(command, reused, second));
        input.Customer.Identity = new object();
        string secondRef = second.business.details.inspection_handoff.handoff_ref;
        Capture(p1, input, 7);
        p1.ResumeInspectionHandoff("w", secondRef);
        var newVisit = Capture(p1, input, 8);
        check("inspection_new_visit_items_not_marked_processed_by_old_pending", newVisit.business.details.inspection_handoff.requires_manual_inspection && newVisit.business.details.inspection_handoff.items.Count == 2);

        var worldProjection = new BusinessProjection(); var worldInput = Input("water_jug");
        var worldFirst = Capture(worldProjection, worldInput, 1); string oldRef = worldFirst.business.details.inspection_handoff.handoff_ref;
        var changedWorld = Capture(worldProjection, worldInput, 1, "w2");
        check("inspection_pending_survives_world_with_historical_handoff", changedWorld.business.details.inspection_handoff.handoff_ref == oldRef);
        check("inspection_old_world_ack_rejected", !worldProjection.ResumeInspectionHandoff("w", oldRef));
        worldProjection.ResumeInspectionHandoff("w2", oldRef);
        var newWorldPending = Capture(worldProjection, worldInput, 2, "w2");
        check("inspection_world_change_clears_completed_identity_knowledge", newWorldPending.business.details.inspection_handoff.requires_manual_inspection && newWorldPending.business.details.inspection_handoff.handoff_ref != oldRef);

        var pendingItemsProjection = new BusinessProjection(); var pendingInput = Input("water_jug");
        var one = Capture(pendingItemsProjection, pendingInput, 1);
        pendingInput.Containers[1].Items = new[] { pendingInput.Containers[1].Items[0], added };
        Capture(pendingItemsProjection, pendingInput, 2);
        pendingItemsProjection.ResumeInspectionHandoff("w", one.business.details.inspection_handoff.handoff_ref);
        check("inspection_item_arriving_while_pending_not_auto_processed", Capture(pendingItemsProjection, pendingInput, 3).business.details.inspection_handoff.items.Single().name == "New water");

        var testProjection = new BusinessProjection(); var testInput = Input("box_tampon");
        var testState = Capture(testProjection, testInput, 1);
        check("inspection_test_trigger_requires_complete_quote", testProjection.TryTriggerTestInspection(testState.business) && testProjection.IsInspectionWaiting &&
            testState.business.details.inspection_handoff.reason == "test_inspection_handoff");
        string testRef = testState.business.details.inspection_handoff.handoff_ref;
        testProjection.ResumeInspectionHandoff("w", testRef); Capture(testProjection, testInput, 2);
        check("inspection_test_trigger_once_per_projection_session", !testProjection.TryTriggerTestInspection(testState.business));
        var noQuote = new BusinessProjection(); var incomplete = Input("box_tampon"); incomplete.Quote = new BusinessQuoteInput();
        var noQuoteState = Capture(noQuote, incomplete, 1);
        check("inspection_test_trigger_rejects_incomplete_quote", !noQuote.TryTriggerTestInspection(noQuoteState.business));
        var front = Input("water_jug"); front.Containers[0].Items = front.Containers[1].Items; front.Containers[1].Items = Array.Empty<BusinessItemInput>(); front.Quote = new BusinessQuoteInput();
        check("inspection_production_triggers_front_before_quote", Capture(new BusinessProjection(), front, 1).business.details.inspection_handoff?.requires_manual_inspection == true);

        GateTests(check);
        var copy = BusinessWire.Clone(first.business.details); copy.inspection_handoff.items[0].name = "mutated";
        check("inspection_clone_detaches_history_items", first.business.details.inspection_handoff.items[0].name != "mutated");
        copy = BusinessWire.Clone(first.business.details); copy.inspection_handoff.requires_manual_inspection = false;
        check("inspection_wire_rejects_status_bool_disagreement", !BusinessWire.ValidateInspection(copy.inspection_handoff));
        copy = BusinessWire.Clone(first.business.details); copy.inspection_handoff.items.Add(copy.inspection_handoff.items[0]);
        check("inspection_wire_rejects_duplicate_refs", !BusinessWire.ValidateInspection(copy.inspection_handoff));
        copy = BusinessWire.Clone(first.business.details); copy.schema_version = 1;
        var schemaOne = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = copy };
        check("inspection_extension_not_allowed_on_schema1", !BusinessWire.Validate(schemaOne));
    }

    private static void GateTests(Action<string, bool> check)
    {
        var projection = new BusinessProjection(); var input = Input("water_jug"); var before = Capture(projection, input, 1);
        foreach (string action in new[] { "close_menu", "purchase_visible_offer", "sell_visible_offer", "prepare_purchase_items", "prepare_sale_items",
            "start_business", "receive_next_customer", "advance_dialogue", "dismiss_customer", "sort_inventory_default", "open_bargain_panel", "choose_bargain_option" })
        {
            var cmd = Resume(before, "blocked-" + action); cmd.action = action; cmd.arguments = new ActionArguments();
            check("inspection_all_other_writes_blocked_" + action, new ActionGate().Accept(cmd, before, "broker", 1)?.reason == "inspection_handoff");
        }
        var resume = Resume(before, "resume-once"); var gate = new ActionGate(); int calls = 0;
        check("inspection_resume_admission", gate.Accept(resume, before, "broker", 1) == null);
        gate.Accept(resume, before, "broker", 1);
        gate.Advance("broker", before, 2, 2, _ => { calls++; projection.ResumeInspectionHandoff("w", resume.arguments.handoff_ref); }, BusinessActionPolicy.Verify);
        var after = Capture(projection, input, 2);
        var result = gate.Advance("broker", after, 3, 3, _ => calls++, BusinessActionPolicy.Verify).result;
        check("inspection_resume_ack_and_single_invocation", result?.status == "succeeded" && calls == 1);
        check("inspection_duplicate_result_does_not_invoke", gate.Accept(resume, after, "broker", 4)?.status == "succeeded" && calls == 1);
        gate.Acknowledge(resume.id);
        check("inspection_acknowledged_command_never_replayed", gate.Accept(resume, after, "broker", 5)?.reason == "command_already_acknowledged" && calls == 1);
        var stale = Resume(after, "stale-handoff"); stale.arguments.handoff_ref = "stale-handoff";
        check("inspection_stale_handoff_not_admitted", new ActionGate().Accept(stale, after, "broker", 1)?.status == "rejected");

        var clean = Capture(new BusinessProjection(), Input("box_tampon"), 1); clean.menu_open = true; clean.can_close_menu = true;
        clean.legal_actions.Add(new LegalAction { action = "close_menu", arguments = new ActionArguments() });
        var close = Resume(clean, "already-invoking"); close.action = "close_menu"; close.arguments = new ActionArguments();
        var active = new ActionGate(); int nativeCalls = 0; active.Accept(close, clean, "broker", 1);
        active.Advance("broker", clean, 2, 2, _ => nativeCalls++, ActionGate.VerifyCloseMenu);
        var nowPending = Capture(new BusinessProjection(), Input("water_jug"), 2);
        check("inspection_new_pending_does_not_cancel_entered_native_command", active.Advance("broker", nowPending, 3, 3, _ => nativeCalls++, ActionGate.VerifyCloseMenu).result?.status == "succeeded" && nativeCalls == 1);

        var menu = Capture(new BusinessProjection(), Input("water_jug"), 1); menu.menu_open = true;
        check("inspection_resume_is_legal_with_menu_without_automatic_close", ActionGate.IsSnapshotValid(menu) && BusinessActionPolicy.Validate(Resume(menu, "menu-resume"), menu) == null);
        var fingerprint = Resume(before, "fingerprint"); string hash = ActionGate.Fingerprint(fingerprint); fingerprint.arguments.handoff_ref = "different";
        check("inspection_command_fingerprint_includes_handoff_ref", ActionGate.Fingerprint(fingerprint) != hash);
    }

    public static string Export()
    {
        var snapshots = Identifiers.Select(id => Capture(new BusinessProjection(), Input(id), 1)).ToList();
        var p = new BusinessProjection(); var input = Input("water_jug"); var before = Capture(p, input, 1); var command = Resume(before, "managed-resume");
        p.ResumeInspectionHandoff("w", command.arguments.handoff_ref); var resumed = Capture(p, input, 2);
        input.Containers[1].Items = new[] { new BusinessItemInput { Identity = new object(), Identifier = "water_pitcher", Name = "New visible water", Units = 1 } };
        var newPending = Capture(p, input, 3);
        var options = ManagedVersionedJson.Create();
        var root = JsonSerializer.SerializeToNode(new { snapshots, before, resume_command = command, resumed_snapshot = resumed, new_pending_snapshot = newPending }, options);
        void Clean(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (string key in new[] { "inspection_handoff", "bargain", "request_text", "trade_panel" }) if (obj.ContainsKey(key) && obj[key] == null) obj.Remove(key);
                if (obj["arguments"] is JsonObject a) foreach (var pair in a.ToArray()) if (pair.Value == null) a.Remove(pair.Key);
                foreach (var pair in obj.ToArray()) if (pair.Value != null) Clean(pair.Value);
            }
            else if (node is JsonArray array) foreach (var value in array) if (value != null) Clean(value);
        }
        Clean(root); return root.ToJsonString();
    }

    private static Command Resume(Snapshot state, string id) => new Command { schema_version = 3, action = "resume_inspection_handoff", id = id, broker_epoch = "broker",
        expected_world_epoch = state.world_epoch, expected_revision = state.revision, expected_version = state.version,
        arguments = new ActionArguments { handoff_ref = state.business.details.inspection_handoff?.handoff_ref ?? "old-handoff" }, start_deadline_unix_ms = 100, execution_timeout_ms = 5000 };
    private static Snapshot Capture(BusinessProjection p, BusinessInput input, int revision, string world = "w")
    {
        var state = new Snapshot { schema_version = 3, world_epoch = world, revision = revision, version = world + ":" + revision,
            scene = "shop", phase = "OPEN", loaded = true, ready = true, cash = 100, business = p.Capture(world, true, () => input),
            legal_actions = new List<LegalAction>(), interaction = new InteractionObservation { phase = "OPEN", consistency = "consistent", operable = false } };
        p.ApplyInspectionGate(state); return state;
    }
    private static BusinessInput Input(string id, string name = "Visible item")
    {
        var item = new BusinessItemInput { Identity = new object(), Identifier = id, Name = name, Units = 1 };
        return new BusinessInput { Customer = new BusinessCustomerInput { Identity = new object(), Presence = "present", Arrived = true,
            TradeType = "sell", Phase = "trading", DialogueStage = "absent", MultiBuyDisabled = false },
            Containers = new[] { new BusinessContainerInput { Container = "front", Completeness = "complete" },
                new BusinessContainerInput { Container = "back", Completeness = "complete", Items = new[] { item } },
                new BusinessContainerInput { Container = "player", Completeness = "complete" } },
            Quote = new BusinessQuoteInput { Status = "complete", Mode = "normal", DisplayedTotal = 10, BindingVerified = true, PurchaseIdentities = new[] { item.Identity } },
            Ui = new BusinessUiInput { Status = "consistent", Coverage = "verified", Complete = true } };
    }
}
