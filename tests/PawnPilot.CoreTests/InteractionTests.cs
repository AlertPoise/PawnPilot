using PawnPilot;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class InteractionTests
{
    private static readonly JsonSerializerOptions Json = ManagedVersionedJson.Create();

    public static void Run(Action<string, bool> check)
    {
        var typing = DialogueLifecycle.Evaluate(Signals(typing: true, displayPending: true));
        check("m5b_typing_remains_progressing_during_display", typing.phase == "typing" && typing.status == "progressing");
        var waiting = DialogueLifecycle.Evaluate(Signals(waiting: true, endPending: true));
        check("m5b_manual_wait_survives_end_pending", waiting.phase == "waiting_for_advance" && waiting.status == "awaiting_agent");
        var choices = DialogueLifecycle.Evaluate(Signals(choices: true, choicesComplete: true, choiceDefinition: true));
        check("m5b_complete_visible_choices_actionable", choices.phase == "awaiting_choice" && choices.status == "awaiting_agent");
        var partialChoices = DialogueLifecycle.Evaluate(Signals(choices: true, choicesComplete: false, choiceDefinition: true));
        check("m5b_partial_choices_without_pending_are_unproven", partialChoices.phase == "unknown" && partialChoices.status == "unknown");
        var partialChoicesPending = DialogueLifecycle.Evaluate(Signals(choices: true, choicesComplete: false,
            choiceDefinition: true, displayPending: true));
        check("m5b_partial_choices_with_pending_stay_progressing", partialChoicesPending.phase == "progressing");
        var choicesPending = DialogueLifecycle.Evaluate(Signals(choices: true, choicesComplete: true,
            choiceDefinition: true, autoPending: true));
        check("m5b_complete_choices_conflict_with_pending", choicesPending.phase == "progressing" &&
            choicesPending.reason == "choice_surface_conflicts_with_pending_transition");
        var completed = DialogueLifecycle.Evaluate(Signals(customer: true, arrived: true, current: false,
            visibleDialogue: false, completedStable: true));
        check("m5b_completed_requires_stable_no_current_dialogue", completed.phase == "completed" && completed.status == "awaiting_agent");
        var staleCoroutine = DialogueLifecycle.Evaluate(Signals(customer: true, arrived: true,
            completedStable: false, current: false, visibleDialogue: false, scheduled: 0));
        check("m5b_coroutine_count_not_used_as_pending_signal", staleCoroutine.phase == "unknown" && staleCoroutine.status == "unknown");
        var absent = DialogueLifecycle.Evaluate(Signals(customer: false));
        check("m5b_absent_customer_idle_without_pending", absent.phase == "absent" && absent.status == "idle");
        var pendingWithoutCustomer = DialogueLifecycle.Evaluate(Signals(customer: false, endPending: true));
        check("m5b_pending_without_customer_progressing", pendingWithoutCustomer.phase == "progressing");
        var unsupported = DialogueLifecycle.Evaluate(Signals(customer: true, arrived: true, otherSurface: true));
        check("m5b_other_surface_unsupported", unsupported.phase == "unsupported" && unsupported.status == "unsupported");
        var unready = DialogueLifecycle.Evaluate(Signals(ready: false));
        check("m5b_unready_lifecycle_unproven", unready.phase == "unknown" && !unready.evidence_complete);

        var candidate = Candidate("presentation");
        check("m5b_generic_candidate_does_not_require_final_surface_status",
            InteractionPolicy.ValidateCandidate(candidate, candidate.legal_actions[0], requireFinalStatus: false) == null);
        check("m5b_generic_final_surface_requires_awaiting_agent", !ActionGate.IsSnapshotValid(candidate));
        candidate.surface.status = "awaiting_agent";
        check("m5b_generic_surface_finalizes_with_candidate", ActionGate.IsSnapshotValid(candidate));
        check("m5b_legacy_policy_rejects_surface_refs",
            !BusinessActionPolicy.ArgumentsValid("choose_dialogue_option", candidate.legal_actions[0].arguments));
        var multipleChoices = Copy(candidate);
        multipleChoices.business.details.customer.option_refs.Add("option-2");
        multipleChoices.business.details.customer.options.Add(new DialogueOptionState { option_ref = "option-2", text = "No" });
        multipleChoices.surface.controls.Add(new SurfaceControl { control_ref = "control-2", action = "choose_dialogue_option",
            option_ref = "option-2", enabled = true, effect_contract = "presentation", reason = "native_callback_verified",
            text = "No", effects = Effects("presentation") });
        multipleChoices.legal_actions.Add(new LegalAction { action = "choose_dialogue_option", arguments = new ActionArguments
        {
            customer_ref = "customer-1", dialogue_ref = "dialogue-1", option_ref = "option-2",
            surface_ref = "surface-1", control_ref = "control-2"
        } });
        check("m5b_multiple_choice_controls_can_be_legal_together", ActionGate.IsSnapshotValid(multipleChoices));
        var duplicateSemantic = Copy(candidate);
        duplicateSemantic.surface.controls.Add(new SurfaceControl { control_ref = "control-2", action = "choose_dialogue_option",
            option_ref = "option-1", enabled = true, effect_contract = "presentation", reason = "native_callback_verified",
            text = "Continue", effects = Effects("presentation") });
        var duplicateArgs = CloneArgs(duplicateSemantic.legal_actions[0].arguments); duplicateArgs.control_ref = "control-2";
        duplicateSemantic.legal_actions.Add(new LegalAction { action = "choose_dialogue_option", arguments = duplicateArgs });
        check("m5b_same_choice_not_duplicated_across_controls", !ActionGate.IsSnapshotValid(duplicateSemantic));
        var legacyAndGeneric = Copy(candidate);
        var oldArgs = CloneArgs(legacyAndGeneric.legal_actions[0].arguments); oldArgs.surface_ref = null; oldArgs.control_ref = null;
        legacyAndGeneric.legal_actions.Add(new LegalAction { action = "choose_dialogue_option", arguments = oldArgs });
        check("m5b_same_semantic_choice_not_duplicated_across_routes", !ActionGate.IsSnapshotValid(legacyAndGeneric));

        var blocked = Candidate("presentation");
        blocked.surface.status = "awaiting_agent";
        blocked.business.details.ui.blockers.Add("dialogue_active");
        check("m5b_named_dialogue_blocker_permitted", InteractionPolicy.ValidateCandidate(blocked,
            blocked.legal_actions[0], requireFinalStatus: true) == null);
        blocked.business.details.ui.blockers.Add("generic_confirmation");
        check("m5b_unknown_modal_blocks_generic_action", InteractionPolicy.ValidateCandidate(blocked,
            blocked.legal_actions[0], requireFinalStatus: true) == "known_ui_blocker");
        blocked = Candidate("presentation"); blocked.surface.status = "awaiting_agent";
        blocked.business.details.ui.status = "updating";
        check("m5b_updating_ui_blocks_generic_action", InteractionPolicy.ValidateCandidate(blocked,
            blocked.legal_actions[0], requireFinalStatus: true) == "known_ui_blocker");
        blocked = Candidate("unknown"); blocked.surface.status = "awaiting_agent";
        check("m5b_unknown_effect_contract_never_legal", InteractionPolicy.ValidateCandidate(blocked,
            blocked.legal_actions[0], requireFinalStatus: true) == "effect_contract_unverified");
        var departureCandidate = Candidate("customer_departure", "dismiss_customer");
        departureCandidate.surface.status = "awaiting_agent";
        check("m5b_customer_departure_candidate_admitted", ActionGate.IsSnapshotValid(departureCandidate));

        var v3 = LegacyCandidate();
        check("m5b_v3_snapshot_remains_valid_without_surface", ActionGate.IsSnapshotValid(v3));
        var forbiddenSurface = Copy(v3); forbiddenSurface.surface = InteractionState.Unavailable();
        check("m5b_v3_snapshot_rejects_surface", !ActionGate.IsSnapshotValid(forbiddenSurface));
        var forbiddenV3Args = Copy(v3); forbiddenV3Args.schema_version = 3;
        forbiddenV3Args.legal_actions[0].arguments.surface_ref = "surface-1";
        forbiddenV3Args.legal_actions[0].arguments.control_ref = "control-1";
        check("m5b_v3_legal_action_rejects_new_refs", !ActionGate.IsSnapshotValid(forbiddenV3Args));
        var v4Legacy = Copy(v3); v4Legacy.schema_version = 4; v4Legacy.surface = InteractionState.Unavailable();
        v4Legacy.surface.status = "awaiting_agent";
        check("m5b_v4_legacy_candidate_keeps_old_policy", ActionGate.IsSnapshotValid(v4Legacy) &&
            CandidateAdmission.Validate(v4Legacy, v4Legacy.legal_actions[0]) == null);
        var requiredSurface = Copy(v4Legacy); requiredSurface.surface = null;
        check("m5b_v4_snapshot_requires_surface", !ActionGate.IsSnapshotValid(requiredSurface));

        var coherentMismatch = Copy(candidate); coherentMismatch.surface.customer_ref = "other-customer";
        check("m5b_complete_surface_customer_must_match_business", !ActionGate.IsSnapshotValid(coherentMismatch));
        var noneWithCustomer = Copy(candidate); noneWithCustomer.surface.kind = "none";
        noneWithCustomer.surface.phase = "absent"; noneWithCustomer.surface.controls.Clear();
        noneWithCustomer.legal_actions.Clear(); noneWithCustomer.surface.status = "idle";
        check("m5b_none_surface_cannot_claim_present_customer", !ActionGate.IsSnapshotValid(noneWithCustomer));
        var dialogueMissingRef = Copy(candidate); dialogueMissingRef.surface.dialogue_ref = null;
        check("m5b_complete_dialogue_requires_matching_dialogue_ref", !ActionGate.IsSnapshotValid(dialogueMissingRef));
        var dialogueMismatchRef = Copy(candidate); dialogueMismatchRef.surface.dialogue_ref = "dialogue-other";
        check("m5b_complete_dialogue_ref_must_match_business", !ActionGate.IsSnapshotValid(dialogueMismatchRef));
        var absentDialogue = Copy(candidate); absentDialogue.surface.phase = "absent";
        check("m5b_absent_phase_requires_none_surface", !ActionGate.IsSnapshotValid(absentDialogue));
        var completedMissingCustomer = Candidate("customer_departure", "dismiss_customer");
        completedMissingCustomer.surface.status = "awaiting_agent";
        completedMissingCustomer.business.details.customer.presence = "absent";
        completedMissingCustomer.business.customer_ref = null;
        completedMissingCustomer.surface.customer_ref = null;
        check("m5b_complete_customer_completion_requires_present_customer", !ActionGate.IsSnapshotValid(completedMissingCustomer));
        var completedWithDialogue = Candidate("customer_departure", "dismiss_customer");
        completedWithDialogue.surface.status = "awaiting_agent";
        completedWithDialogue.surface.dialogue_ref = "dialogue-1";
        check("m5b_complete_customer_completion_rejects_dialogue_ref", !ActionGate.IsSnapshotValid(completedWithDialogue));

        var giftZero = InteractionPolicy.Verify(Command(Candidate("document_gift")),
            Candidate("document_gift"), Progressed(Candidate("document_gift")), nativeReturned: true);
        check("m5b_document_gift_allows_no_immediate_item", giftZero.outcome == "succeeded" &&
            giftZero.interaction_result.observed_effects.Contains("dialogue_transition") &&
            giftZero.interaction_result.unverified_effects.Contains("inventory_transfer"));
        var giftOneBefore = Candidate("document_gift");
        var giftOneCommand = Command(giftOneBefore);
        var giftOneAfter = Progressed(giftOneBefore);
        AddFrontGift(giftOneAfter, "document-1");
        var giftOne = InteractionPolicy.Verify(giftOneCommand, giftOneBefore, giftOneAfter, nativeReturned: true);
        check("m5b_document_gift_reports_one_owned_front_addition", giftOne.outcome == "succeeded" &&
            giftOne.interaction_result.added_item_refs.SequenceEqual(new[] { "document-1" }) &&
            giftOne.interaction_result.observed_effects.Contains("inventory_transfer"));
        var giftTooMany = Copy(giftOneAfter); AddFrontGift(giftTooMany, "document-2");
        var giftManyDecision = InteractionPolicy.Verify(giftOneCommand, giftOneBefore, giftTooMany, nativeReturned: true);
        check("m5b_document_gift_rejects_multiple_additions", giftManyDecision.outcome == "uncertain");
        var removal = Progressed(giftOneBefore); RemoveFrontItem(removal, "owned-sale-item");
        var removalDecision = InteractionPolicy.Verify(Command(giftOneBefore), giftOneBefore, removal, nativeReturned: true);
        check("m5b_presentation_rejects_inventory_removal_and_reports_unknown_delta",
            removalDecision.outcome == "uncertain" &&
            removalDecision.interaction_result.removed_item_refs.SequenceEqual(new[] { "owned-sale-item" }));
        var cash = Progressed(giftOneBefore); cash.cash++;
        var cashDecision = InteractionPolicy.Verify(Command(giftOneBefore), giftOneBefore, cash, nativeReturned: true);
        check("m5b_generic_action_rejects_cash_delta", cashDecision.outcome == "uncertain" && cashDecision.interaction_result.cash_delta == 1);
        var cashAndInventory = Progressed(giftOneBefore); cashAndInventory.cash++;
        AddFrontGift(cashAndInventory, "unexpected-item");
        RemoveFrontItem(cashAndInventory, "owned-sale-item");
        var unknownDelta = InteractionPolicy.UnknownResult(Command(giftOneBefore), giftOneBefore, cashAndInventory,
            nativeReturned: false);
        check("m5b_unknown_receipt_reports_comparable_cash_and_inventory_deltas",
            unknownDelta.cash_delta == 1 && unknownDelta.added_item_refs.SequenceEqual(new[] { "unexpected-item" }) &&
            unknownDelta.removed_item_refs.SequenceEqual(new[] { "owned-sale-item" }) && unknownDelta.input_receipt == "unknown");
        var partialDelta = Copy(cashAndInventory); partialDelta.business.details.containers[0].completeness = "partial";
        var unprovenDelta = InteractionPolicy.UnknownResult(Command(giftOneBefore), giftOneBefore, partialDelta,
            nativeReturned: false);
        check("m5b_unknown_receipt_omits_unproven_inventory_delta",
            unprovenDelta.added_item_refs.Count == 0 && unprovenDelta.removed_item_refs.Count == 0);
        var incompleteAfter = Progressed(giftOneBefore); incompleteAfter.surface.completeness = "partial";
        var incompleteDecision = InteractionPolicy.Verify(Command(giftOneBefore), giftOneBefore, incompleteAfter, nativeReturned: true);
        check("m5b_incomplete_after_evidence_stays_pending", incompleteDecision.outcome == "pending" &&
            incompleteDecision.interaction_result.verification == "unknown");
        var departedBefore = Candidate("customer_departure", "dismiss_customer");
        var replaced = ReplacementSnapshot(departedBefore);
        var departDecision = InteractionPolicy.Verify(Command(departedBefore), departedBefore, replaced, nativeReturned: true);
        check("m5b_customer_departure_allows_immediate_replacement", departDecision.outcome == "succeeded" &&
            departDecision.interaction_result.observed_effects.Contains("customer_departure"));
        var noReceipt = InteractionPolicy.Verify(Command(giftOneBefore), giftOneBefore,
            Progressed(giftOneBefore), nativeReturned: false);
        check("m5b_dto_delta_cannot_forge_native_receipt", noReceipt.outcome == "uncertain" &&
            noReceipt.interaction_result.input_receipt == "unknown");

        var gateBefore = Candidate("presentation"); gateBefore.surface.status = "awaiting_agent";
        var gateCommand = Command(gateBefore, "gate-presentation");
        var gate = new ActionGate(); int nativeCalls = 0;
        check("m5b_gate_accepts_generic_v4_command", gate.Accept(gateCommand, gateBefore, "broker", 1) == null);
        var invoked = gate.Advance("broker", gateBefore, 2, 2, _ => nativeCalls++,
            (command, beforeState, afterState) => InteractionPolicy.Verify(command, beforeState, afterState, nativeReturned: true));
        check("m5b_generic_starter_return_is_not_input_receipt", !gate.MarkNativeReturned("wrong-id"));
        check("m5b_native_callback_can_mark_returned_receipt", gate.MarkNativeReturned(gateCommand.id));
        var gateAfter = Progressed(gateBefore);
        var success = gate.Advance("broker", gateAfter, 3, 3, _ => nativeCalls++,
            (command, beforeState, afterState) => InteractionPolicy.Verify(command, beforeState, afterState, nativeReturned: true)).result;
        check("m5b_gate_confirms_only_returned_verified_input", invoked.progress.stage == "invoking" && nativeCalls == 1 &&
            success.status == "succeeded" && success.interaction_result.input_receipt == "returned" &&
            success.interaction_result.verification == "confirmed");
        var exposed = gate.PendingResult;
        exposed.interaction_result.observed_effects.Clear();
        check("m5b_interaction_result_surface_is_detached", gate.PendingResult.interaction_result.observed_effects.Count > 0);
        check("m5b_gate_ack_releases_verified_result", gate.Acknowledge(gateCommand.id) && !gate.IsWriteBlocked);
        check("m5b_late_native_receipt_cannot_rewrite_terminal_result", !gate.MarkNativeReturned(gateCommand.id));

        var unreportedBefore = Candidate("presentation"); unreportedBefore.surface.status = "awaiting_agent";
        var unreportedCommand = Command(unreportedBefore, "unreported-native-return"); var unreportedGate = new ActionGate();
        unreportedGate.Accept(unreportedCommand, unreportedBefore, "broker", 1);
        unreportedGate.Advance("broker", unreportedBefore, 2, 2, _ => { },
            (command, beforeState, afterState) => InteractionPolicy.Verify(command, beforeState, afterState, nativeReturned: true));
        var unreportedAfter = Progressed(unreportedBefore);
        var unreported = unreportedGate.Advance("broker", unreportedAfter, 3, 3, _ => { },
            (command, beforeState, afterState) => InteractionPolicy.Verify(command, beforeState, afterState, nativeReturned: true)).result;
        check("m5b_unreported_native_return_cannot_succeed", unreported.status == "outcome_unknown" &&
            unreported.interaction_result.input_receipt == "unknown");

        var crossWorldBefore = Candidate("presentation"); crossWorldBefore.surface.status = "awaiting_agent";
        var crossWorldCommand = Command(crossWorldBefore, "cross-world-after"); var crossWorldGate = new ActionGate();
        crossWorldGate.Accept(crossWorldCommand, crossWorldBefore, "broker", 1);
        crossWorldGate.Advance("broker", crossWorldBefore, 2, 2, _ => { },
            (_, _, _) => VerificationDecision.Pending());
        crossWorldGate.MarkNativeReturned(crossWorldCommand.id);
        var crossWorldAfter = Progressed(crossWorldBefore);
        crossWorldAfter.world_epoch = "other-world";
        crossWorldAfter.version = crossWorldAfter.world_epoch + ":" + crossWorldAfter.revision;
        crossWorldAfter.cash++;
        AddFrontGift(crossWorldAfter, "cross-world-item");
        var crossWorldResult = crossWorldGate.Advance("broker", crossWorldAfter, 3, 3, _ => { },
            (_, _, _) => VerificationDecision.Pending()).result;
        check("m5b_cross_world_unknown_does_not_claim_incomparable_deltas",
            crossWorldResult.status == "outcome_unknown" && crossWorldResult.after == null &&
            crossWorldResult.interaction_result.cash_delta == null &&
            crossWorldResult.interaction_result.added_item_refs.Count == 0 &&
            crossWorldResult.interaction_result.removed_item_refs.Count == 0);

        var rejectBefore = Candidate("presentation"); rejectBefore.surface.status = "awaiting_agent";
        var rejectCommand = Command(rejectBefore, "preentry-reject"); var rejectGate = new ActionGate(); int rejectedCalls = 0;
        rejectGate.Accept(rejectCommand, rejectBefore, "broker", 1);
        var stale = Copy(rejectBefore); stale.revision++; stale.version = stale.world_epoch + ":" + stale.revision;
        var preentry = rejectGate.Advance("broker", stale, 2, 2, _ => rejectedCalls++,
            (_, _, _) => VerificationDecision.Succeeded()).result;
        check("m5b_preentry_reject_has_no_input_evidence", preentry.status == "rejected" &&
            preentry.interaction_result == null && rejectedCalls == 0);

        var noEffectBefore = Candidate("presentation"); noEffectBefore.surface.status = "awaiting_agent";
        var noEffectCommand = Command(noEffectBefore, "receipt-only"); var noEffectGate = new ActionGate();
        noEffectGate.Accept(noEffectCommand, noEffectBefore, "broker", 1);
        noEffectGate.Advance("broker", noEffectBefore, 2, 2, _ => { }, (_, _, _) => VerificationDecision.Pending());
        var forged = new VerificationDecision
        {
            outcome = "succeeded", reason = "forged_receipt",
            interaction_result = new InteractionResult { input_receipt = "returned", verification = "confirmed",
                effect_contract = "presentation", observed_effects = new() { "dialogue_transition", "presentation" },
                unverified_effects = new() { "story_state" }, cash_delta = 0,
                added_item_refs = new(), removed_item_refs = new() }
        };
        var noEffectResult = noEffectGate.Advance("broker", Copy(noEffectBefore), 3, 3, _ => { }, (_, _, _) => forged).result;
        check("m5b_gate_rechecks_effect_beyond_receipt", noEffectResult.status == "outcome_unknown" &&
            noEffectGate.Acknowledge(noEffectCommand.id) && noEffectGate.IsWriteBlocked);
    }

    private static DialogueFlowSignals Signals(bool ready = true, bool customer = true, bool arrived = true,
        bool current = true, bool typing = false, bool waiting = false, bool autoPending = false,
        bool endPending = false, bool displayPending = false, int scheduled = 0, bool choices = false,
        bool choicesComplete = false, bool choiceDefinition = false, bool visibleDialogue = true,
        bool otherSurface = false, bool aboutToStart = false, bool completedStable = false) => new DialogueFlowSignals
    {
        Ready = ready, HasCustomer = customer, Arrived = arrived, HasCurrentDialogue = current,
        IsTyping = typing, WaitingForAdvance = waiting, AutoAdvancePending = autoPending,
        EndPending = endPending, DisplayPending = displayPending, ScheduledEndActions = scheduled,
        HasVisibleChoices = choices, ChoicesComplete = choicesComplete, HasChoiceDefinition = choiceDefinition,
        VisibleDialogue = visibleDialogue, OtherSurface = otherSurface, AboutToStart = aboutToStart,
        CompletedStable = completedStable
    };

    private static Snapshot Candidate(string contract, string action = "choose_dialogue_option")
    {
        var state = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "m3a-sort.json")), Json);
        foreach (string container in new[] { "front", "back" })
            state.business.details.containers.Add(new ContainerState { container = container, completeness = "complete" });
        state.schema_version = Protocol.InteractionSchemaVersion;
        state.business.customer_ref = "customer-1";
        var customer = state.business.details.customer;
        customer.presence = "present"; customer.arrived = true; customer.phase = "dialogue";
        customer.trade_type = "unknown"; customer.dialogue_ref = action == "dismiss_customer" ? null : "dialogue-1";
        customer.dialogue_stage = action == "dismiss_customer" ? "unknown" : "choices";
        customer.option_refs = action == "choose_dialogue_option" ? new List<string> { "option-1" } : new List<string>();
        customer.options = action == "choose_dialogue_option"
            ? new List<DialogueOptionState> { new DialogueOptionState { option_ref = "option-1", text = "Continue" } }
            : new List<DialogueOptionState>();
        customer.is_typing = action == "dismiss_customer" ? null : false;
        customer.waiting_for_advance = action == "dismiss_customer" ? null : action == "advance_dialogue";
        state.business.details.ui.status = "unverified";
        state.business.details.ui.blockers = new List<string>();
        state.interaction.consistency = "unverified"; state.interaction.operable = true;
        var args = action switch
        {
            "advance_dialogue" => new ActionArguments { customer_ref = "customer-1", dialogue_ref = "dialogue-1", mode = "next", surface_ref = "surface-1", control_ref = "control-1" },
            "choose_dialogue_option" => new ActionArguments { customer_ref = "customer-1", dialogue_ref = "dialogue-1", option_ref = "option-1", surface_ref = "surface-1", control_ref = "control-1" },
            _ => new ActionArguments { customer_ref = "customer-1", surface_ref = "surface-1", control_ref = "control-1" }
        };
        var phase = action == "dismiss_customer" ? "completed" : action == "advance_dialogue" ? "waiting_for_advance" : "awaiting_choice";
        var control = new SurfaceControl
        {
            control_ref = "control-1", action = action, mode = args.mode, option_ref = args.option_ref,
            enabled = true, effect_contract = contract, reason = "native_callback_verified",
            text = "Continue", effects = Effects(contract)
        };
        state.surface = new SurfaceObservation
        {
            schema_version = 1, surface_ref = "surface-1", kind = action == "dismiss_customer" ? "customer_completion" : "dialogue",
            phase = phase, status = "progressing", completeness = "complete", reason = "visible_current_surface",
            customer_ref = "customer-1", dialogue_ref = action == "dismiss_customer" ? null : "dialogue-1",
            controls = new List<SurfaceControl> { control }
        };
        state.legal_actions = new List<LegalAction> { new LegalAction { action = action, arguments = args } };
        return state;
    }

    private static Snapshot LegacyCandidate()
    {
        var state = Candidate("presentation");
        state.schema_version = Protocol.BusinessSchemaVersion;
        state.surface = null;
        state.legal_actions = new List<LegalAction> { new LegalAction { action = "sort_inventory_default",
            arguments = new ActionArguments { container = "player" } } };
        state.business.customer_ref = null;
        state.business.details.customer = new CustomerState { presence = "absent", phase = "absent", trade_type = "unknown",
            dialogue_stage = "unknown", option_refs = new List<string>(), options = new List<DialogueOptionState>() };
        state.interaction.phase = "business"; state.interaction.operable = true;
        return state;
    }

    private static ActionArguments Args(Snapshot before) => before.legal_actions[0].arguments;
    private static Command Command(Snapshot before, string id = "interaction-command") => new Command
    {
        schema_version = Protocol.InteractionSchemaVersion, id = id, broker_epoch = "broker", action = before.legal_actions[0].action,
        arguments = CloneArgs(Args(before)), expected_world_epoch = before.world_epoch, expected_revision = before.revision,
        expected_version = before.version, start_deadline_unix_ms = 100, execution_timeout_ms = 5000
    };

    private static ActionArguments CloneArgs(ActionArguments args) => new ActionArguments
    {
        customer_ref = args.customer_ref, dialogue_ref = args.dialogue_ref, option_ref = args.option_ref, mode = args.mode,
        surface_ref = args.surface_ref, control_ref = args.control_ref, container = args.container
    };

    private static Snapshot Progressed(Snapshot before)
    {
        var after = Copy(before);
        after.revision = before.revision + 1; after.version = after.world_epoch + ":" + after.revision;
        after.surface.surface_ref = "surface-2"; after.surface.phase = "progressing";
        after.surface.status = "progressing"; after.surface.reason = "dialogue_transition_observed";
        after.surface.dialogue_ref = "dialogue-2";
        after.surface.controls = new List<SurfaceControl> { new SurfaceControl
        {
            control_ref = "control-progress", action = "advance_dialogue", mode = "next", enabled = false,
            effect_contract = "presentation", reason = "dialogue_transition_pending", effects = Effects("presentation")
        } };
        after.legal_actions.Clear();
        after.business.details.customer.dialogue_ref = "dialogue-2";
        after.business.details.customer.dialogue_stage = "typing";
        after.business.details.customer.is_typing = true;
        after.business.details.customer.option_refs.Clear();
        after.business.details.customer.options.Clear();
        return after;
    }

    private static Snapshot ReplacementSnapshot(Snapshot before)
    {
        // Model the old customer leaving while a replacement dialogue is already
        // visible and progressing, with complete surface refs matching business state.
        var after = Progressed(before);
        after.business.customer_ref = "customer-2";
        after.surface.kind = "dialogue";
        after.surface.customer_ref = "customer-2";
        after.business.details.customer.presence = "present";
        after.business.details.customer.arrived = true;
        after.business.details.customer.phase = "dialogue";
        after.business.details.customer.dialogue_ref = after.surface.dialogue_ref;
        after.business.details.customer.dialogue_stage = "typing";
        after.business.details.customer.is_typing = true;
        after.business.details.customer.waiting_for_advance = false;
        return after;
    }

    private static List<string> Effects(string contract) => contract switch
    {
        "presentation" => new() { "presentation", "dialogue_transition", "story_state" },
        "document_gift" => new() { "presentation", "dialogue_transition", "story_state", "inventory_transfer" },
        "customer_departure" => new() { "customer_departure", "story_state" },
        _ => new() { "story_state" }
    };

    private static void AddFrontGift(Snapshot snapshot, string itemRef)
    {
        snapshot.business.details.containers.Find(item => item.container == "front").item_refs.Add(itemRef);
        snapshot.business.details.items.Add(new ItemState { item_ref = itemRef, container = "front", name = "Document",
            units = 1, owned = true, shape_status = "unverified" });
    }

    private static void RemoveFrontItem(Snapshot snapshot, string itemRef)
    {
        var container = snapshot.business.details.containers.Find(item => item.container == "player");
        container.item_refs.Remove(itemRef);
        snapshot.business.details.items.RemoveAll(item => item.item_ref == itemRef);
    }

    private static Snapshot Copy(Snapshot state) => JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(state, Json), Json);

    public static string Export()
    {
        var cases = new Dictionary<string, object>();
        cases["presentation_confirmed"] = RunCase(Candidate("presentation"), Progressed, "presentation_confirmed");
        cases["document_gift_zero_confirmed"] = RunCase(Candidate("document_gift"), Progressed, "document_gift_zero_confirmed");
        cases["document_gift_one_confirmed"] = RunCase(Candidate("document_gift"), before =>
        {
            var after = Progressed(before); AddFrontGift(after, "document-1"); return after;
        }, "document_gift_one_confirmed");
        cases["customer_departure_confirmed"] = RunCase(Candidate("customer_departure", "dismiss_customer"), before =>
        {
            return ReplacementSnapshot(before);
        }, "customer_departure_confirmed");
        cases["cash_change_uncertain"] = RunCase(Candidate("presentation"), before =>
        {
            var after = Progressed(before); after.cash++; return after;
        }, "cash_change_uncertain");
        cases["cash_and_inventory_delta_unknown"] = RunCase(Candidate("presentation"), before =>
        {
            var after = Progressed(before); after.cash++; AddFrontGift(after, "unexpected-item");
            RemoveFrontItem(after, "owned-sale-item"); return after;
        }, "cash_and_inventory_delta_unknown");
        var thrownBefore = Candidate("presentation"); thrownBefore.surface.status = "awaiting_agent";
        AssertExportSnapshot("invoked_exception_unknown", "before", thrownBefore);
        var thrownCommand = Command(thrownBefore, "invoked-exception-unknown"); var thrownGate = new ActionGate();
        thrownGate.Accept(thrownCommand, thrownBefore, "broker", 1);
        var thrownResult = thrownGate.Advance("broker", thrownBefore, 2, 2,
            _ => throw new InvalidOperationException("native handler threw"), (_, _, _) => VerificationDecision.Pending()).result;
        AssertExportSnapshot("invoked_exception_unknown", "after", thrownResult?.after);
        cases["invoked_exception_unknown"] = new { command = thrownCommand, before = thrownBefore,
            after = thrownResult?.after, result = thrownResult, progress = thrownResult?.execution };

        var rejectBefore = Candidate("presentation"); rejectBefore.surface.status = "awaiting_agent";
        AssertExportSnapshot("preentry_rejected", "before", rejectBefore);
        var rejectCommand = Command(rejectBefore, "preentry-rejected");
        rejectCommand.expected_revision = 0; rejectCommand.expected_version = "fixture-world:0";
        var rejectGate = new ActionGate();
        var rejected = rejectGate.Accept(rejectCommand, rejectBefore, "broker", 1);
        cases["preentry_rejected"] = new { command = rejectCommand, before = rejectBefore, after = (Snapshot)null,
            result = rejected, progress = rejected?.execution };

        var options = ManagedVersionedJson.Create();
        var root = new JsonObject { ["source"] = "fixture", ["schema_version"] = 4,
            ["cases"] = JsonSerializer.SerializeToNode(cases, options) };
        Clean(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static object RunCase(Snapshot before, Func<Snapshot, Snapshot> makeAfter, string id)
    {
        before.surface.status = "awaiting_agent";
        AssertExportSnapshot(id, "before", before);
        var command = Command(before, id);
        var after = makeAfter(before);
        AssertExportSnapshot(id, "after", after);
        var gate = new ActionGate();
        var rejected = gate.Accept(command, before, "broker", 1);
        if (rejected != null)
            return new { command, before, after = (Snapshot)null, result = rejected, progress = rejected.execution };
        gate.Advance("broker", before, 2, 2, _ => { },
            (cmd, b, a) => InteractionPolicy.Verify(cmd, b, a, nativeReturned: true));
        gate.MarkNativeReturned(command.id);
        var result = gate.Advance("broker", after, 3, 3, _ => { },
            (cmd, b, a) => InteractionPolicy.Verify(cmd, b, a, nativeReturned: true)).result;
        return new { command, before, after, result, progress = result?.execution };
    }

    private static void AssertExportSnapshot(string caseName, string position, Snapshot snapshot)
    {
        if (snapshot != null && !ActionGate.IsSnapshotValid(snapshot))
            throw new InvalidOperationException($"interaction fixture {caseName} has invalid {position} snapshot");
    }

    private static void Clean(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            // System.Text.Json includes nullable fields; the managed wire serializer omits these
            // optional fields, matching the production Json.NET ShouldSerialize contract.
            foreach (string key in new[] { "day", "inspection_handoff", "bargain", "request_text", "trade_panel" })
                if (obj.ContainsKey(key) && obj[key] == null) obj.Remove(key);
            if (obj.ContainsKey("interaction_result") && obj["interaction_result"] == null) obj.Remove("interaction_result");
            if (obj.ContainsKey("surface") && obj["surface"] == null) obj.Remove("surface");
            if (obj["arguments"] is JsonObject args)
                foreach (var pair in args.ToArray()) if (pair.Value == null) args.Remove(pair.Key);
            foreach (var pair in obj.ToArray()) Clean(pair.Value);
        }
        else if (node is JsonArray array)
            foreach (var item in array) Clean(item);
    }
}
