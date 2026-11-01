using System;
using System.Collections.Generic;
using PawnPilot;

public static class BargainTests
{
    public static void Run(Action<string, bool> check)
    {
        var input = Input();
        var projection = new BusinessProjection();
        var closed = projection.Capture("w", true, () => input);
        check("bargain_optional_schema2_extension", BusinessWire.Validate(closed) && closed.details.bargain.phase == "closed");
        var old = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails() };
        check("bargain_old_schema1_still_valid", BusinessWire.Validate(old) && !old.details.ShouldSerializebargain());
        old.details.bargain = new BargainState();
        check("bargain_schema1_extension_rejected", !BusinessWire.Validate(old));
        old.details.schema_version = 2;
        check("bargain_schema2_unavailable_structural_state", BusinessWire.Validate(old));

        var option = new BusinessBargainOptionInput { Identity = new object(), Text = "-10%", Kind = "lower_purchase_price", PriceChangePercent = -10, Enabled = true };
        input.Bargain.Phase = "choosing"; input.Bargain.Options = new[] { option };
        var choosing = projection.Capture("w", true, () => input);
        string optionRef = choosing.details.bargain.options[0].option_ref;
        check("bargain_option_resolves_bound_identity", projection.TryResolveBargainOption("w", choosing.customer_ref, optionRef, out var identity) && ReferenceEquals(identity, option.Identity));
        check("bargain_option_rejects_wrong_world_customer", !projection.TryResolveBargainOption("other", choosing.customer_ref, optionRef, out _) && !projection.TryResolveBargainOption("w", "other", optionRef, out _));
        var same = projection.Capture("w", true, () => input);
        check("bargain_unchanged_option_context_stable", same.details.bargain.options[0].option_ref == optionRef);
        input.Ui.Status = "blocked"; input.Ui.Blockers = new[] { "bargain_panel", "global_ui_stack_unobservable" };
        var docked = projection.Capture("w", true, () => input);
        check("bargain_docked_sheet_preserves_normal_quote_and_item_refs", BusinessWire.Validate(docked) &&
            docked.details.quote.status == "complete" && docked.offer_ref == choosing.offer_ref && docked.item_refs.Count == 1 &&
            docked.details.containers[1].completeness == "complete");
        var modalSnapshot = Snapshot(Copy(docked), 2);
        modalSnapshot.interaction.phase = "OPEN";
        modalSnapshot.interaction.consistency = "blocked";
        modalSnapshot.interaction.operable = false;
        var modalChoose = Command("choose_bargain_option", modalSnapshot, docked.details.bargain.options[0].option_ref);
        var modalClose = Command("close_bargain_panel", modalSnapshot);
        modalSnapshot.legal_actions.Add(new LegalAction { action = modalChoose.action, arguments = modalChoose.arguments });
        modalSnapshot.legal_actions.Add(new LegalAction { action = modalClose.action, arguments = modalClose.arguments });
        check("bargain_blocked_full_snapshot_keeps_legal_local_actions_with_global_operable_false",
            BusinessActionPolicy.Validate(modalChoose, modalSnapshot) == null && BusinessActionPolicy.Validate(modalClose, modalSnapshot) == null &&
            ActionGate.IsSnapshotValid(modalSnapshot));
        modalSnapshot.interaction.operable = true;
        check("bargain_blocked_full_snapshot_still_rejects_global_operable_true", !ActionGate.IsSnapshotValid(modalSnapshot));
        var purchaseState = Snapshot(Copy(docked), 2);
        var purchase = new Command { action = "purchase_visible_offer", arguments = new ActionArguments { customer_ref = docked.customer_ref,
            offer_ref = docked.offer_ref, collection_fingerprint = docked.collection_fingerprint, item_refs = new List<string>(docked.item_refs), mode = "normal", amount = 10, max_spend = 10 } };
        check("bargain_docked_sheet_does_not_authorize_purchase", BusinessActionPolicy.ValidateCandidate(purchase, purchaseState) == "known_ui_blocker");
        check("bargain_observation_keeps_other_modal_gates", BusinessUiEvidence.BlocksTradeObservation("blocked", new[] { "bargain_panel", "confirmation" }) &&
            BusinessUiEvidence.BlocksTradeObservation("blocked", Array.Empty<string>()) &&
            BusinessUiEvidence.BlocksTradeObservation("unverified", new[] { "ui_reader_failed" }) &&
            !BusinessUiEvidence.BlocksTradeObservation("unverified", new[] { "global_ui_stack_unobservable" }));
        input.Ui.Status = "consistent"; input.Ui.Blockers = Array.Empty<string>();
        input.Bargain.TermsRevision++;
        var revised = projection.Capture("w", true, () => input);
        check("bargain_terms_revision_expires_ref_and_offer_even_unchanged_price", revised.details.bargain.options[0].option_ref != optionRef && !projection.TryResolveBargainOption("w", revised.customer_ref, optionRef, out _) && revised.offer_ref != choosing.offer_ref && revised.collection_fingerprint == choosing.collection_fingerprint);
        optionRef = revised.details.bargain.options[0].option_ref;
        option.Text = "Discount 10%";
        var relabelled = projection.Capture("w", true, () => input);
        check("bargain_option_label_context_expires_ref", relabelled.details.bargain.options[0].option_ref != optionRef);
        optionRef = relabelled.details.bargain.options[0].option_ref;
        input.Bargain.Phase = "closed"; input.Bargain.Options = Array.Empty<BusinessBargainOptionInput>();
        var reclosed = projection.Capture("w", true, () => input);
        check("bargain_closed_panel_revokes_all_refs", reclosed.details.bargain.options.Count == 0 && !projection.TryResolveBargainOption("w", reclosed.customer_ref, optionRef, out _));

        var clone = BusinessWire.Clone(choosing.details);
        clone.bargain.options[0].text = "changed";
        check("bargain_clone_detaches_options", choosing.details.bargain.options[0].text == "-10%");
        foreach (int percent in new[] { 0, -101, 101 })
        {
            clone = BusinessWire.Clone(choosing.details); clone.bargain.options[0].price_change_percent = percent;
            check("bargain_wire_rejects_percent_" + percent, !BusinessWire.ValidateBargain(clone.bargain));
        }
        clone = BusinessWire.Clone(choosing.details); clone.bargain.options.Add(clone.bargain.options[0]);
        check("bargain_wire_rejects_duplicate_option_ref", !BusinessWire.ValidateBargain(clone.bargain));
        clone = BusinessWire.Clone(choosing.details); clone.bargain.options[0].text = new string('x', 257);
        check("bargain_wire_bounds_visible_text", !BusinessWire.ValidateBargain(clone.bargain));
        clone = BusinessWire.Clone(choosing.details); clone.bargain.terms_revision = -1;
        check("bargain_wire_rejects_negative_terms_revision", !BusinessWire.ValidateBargain(clone.bargain));

        var before = Snapshot(closed, 1);
        var open = Command("open_bargain_panel", before);
        check("bargain_open_registered_enabled_arguments", BusinessActionPolicy.IsRegistered(open.action) && BusinessActionPolicy.IsEnabled(open.action) && BusinessActionPolicy.ArgumentsValid(open.action, open.arguments));
        check("bargain_open_admitted_closed", BusinessActionPolicy.ValidateCandidate(open, before) == null);
        var after = Snapshot(choosing, 2);
        check("bargain_open_verified_visible_choices", BusinessActionPolicy.Postcondition(open, before, after));
        var choose = Command("choose_bargain_option", after, choosing.details.bargain.options[0].option_ref);
        after.business.details.ui.blockers.Add("bargain_panel");
        check("bargain_choose_ignores_own_panel_blocker", BusinessActionPolicy.ValidateCandidate(choose, after) == null);
        choose.arguments.mode = "buy";
        check("bargain_choose_rejects_opposite_direction", BusinessActionPolicy.ValidateCandidate(choose, after) != null);
        choose.arguments.mode = "sell"; choose.arguments.option_ref = "stale";
        check("bargain_choose_rejects_stale_option", BusinessActionPolicy.ValidateCandidate(choose, after) != null);
        choose.arguments.option_ref = choosing.details.bargain.options[0].option_ref;
        choosing.details.bargain.options[0].selected = true;
        check("bargain_choose_rejects_selected_attempt", BusinessActionPolicy.ValidateCandidate(choose, after) != null);
        choosing.details.bargain.options[0].selected = false;
        choosing.details.bargain.options[0].enabled = false;
        check("bargain_choose_rejects_disabled_option", BusinessActionPolicy.ValidateCandidate(choose, after) != null);
        choosing.details.bargain.options[0].enabled = true;
        var response = Snapshot(Copy(choosing), 3);
        response.business.details.bargain.phase = "responding";
        response.business.details.bargain.result = "responding";
        response.business.details.bargain.last_option_ref = choose.arguments.option_ref;
        response.business.details.bargain.terms_revision = choosing.details.bargain.terms_revision + 1;
        response.business.details.customer.dialogue_ref = "dialogue-ack";
        response.business.details.customer.dialogue_stage = "typing";
        response.business.details.customer.phase = "dialogue";
        response.business.details.ui.blockers.Add("dialogue_active");
        check("bargain_choose_ack_needs_actual_ref_revision_result", BusinessActionPolicy.Postcondition(choose, after, response));
        response.business.details.customer.trade_type = "other";
        check("bargain_departure_intent_change_still_acknowledges_attempt", BusinessActionPolicy.Postcondition(choose, after, response));
        response.business.details.customer.trade_type = "sell";
        response.cash++;
        check("bargain_choose_ack_rejects_cash_change", !BusinessActionPolicy.Postcondition(choose, after, response));
        response.cash--;
        response.business.details.bargain.last_option_ref = "other";
        check("bargain_choose_ack_rejects_other_choice", !BusinessActionPolicy.Postcondition(choose, after, response));
        response.business.details.bargain.last_option_ref = choose.arguments.option_ref;
        response.business.details.bargain.terms_revision = choosing.details.bargain.terms_revision;
        check("bargain_choose_ack_rejects_no_terms_revision", !BusinessActionPolicy.Postcondition(choose, after, response));
        response.business.details.bargain.terms_revision++;
        response.business.details.bargain.result = "none";
        check("bargain_choose_ack_rejects_no_result", !BusinessActionPolicy.Postcondition(choose, after, response));
        response.business.details.bargain.result = "responding";
        var close = Command("close_bargain_panel", response);
        check("bargain_close_admitted_responding_with_dialogue", BusinessActionPolicy.ValidateCandidate(close, response) == null);
        var ended = Snapshot(Copy(response.business), 4);
        ended.business.details.bargain.phase = "closed"; ended.business.details.bargain.options.Clear();
        check("bargain_close_verified", BusinessActionPolicy.Postcondition(close, response, ended));
        response.business.details.customer.trade_type = "other";
        ended.business.details.customer.trade_type = "other";
        check("bargain_close_acknowledged_departure_response_admitted_and_verified", BusinessActionPolicy.ValidateCandidate(close, response) == null && BusinessActionPolicy.Postcondition(close, response, ended));
        response.business.details.bargain.last_option_ref = null;
        check("bargain_close_other_customer_requires_ack", BusinessActionPolicy.ValidateCandidate(close, response) != null);
        response.business.details.bargain.last_option_ref = choose.arguments.option_ref;
        response.business.details.bargain.terms_revision = 0;
        check("bargain_close_other_customer_requires_terms_revision", BusinessActionPolicy.ValidateCandidate(close, response) != null);
        response.business.details.bargain.terms_revision = 1;
        response.business.details.bargain.result = "unknown";
        check("bargain_close_other_customer_requires_responding_result", BusinessActionPolicy.ValidateCandidate(close, response) != null);
        response.business.details.bargain.result = "responding";
        response.business.details.customer.trade_type = "sell";
        var blocked = Snapshot(Copy(choosing), 2);
        blocked.business.details.ui.blockers.Add("confirmation");
        check("bargain_other_blockers_remain_effective", BusinessActionPolicy.ValidateCandidate(choose, blocked) != null);
        check("bargain_open_cannot_close_toggle", BusinessActionPolicy.ValidateCandidate(Command("open_bargain_panel", after), after) != null);
        var unavailable = Snapshot(Copy(closed), 1); unavailable.business.details.bargain.available = false;
        check("bargain_unavailable_open_denied", BusinessActionPolicy.ValidateCandidate(Command("open_bargain_panel", unavailable), unavailable) != null);
        var dialogued = Snapshot(Copy(choosing), 2); dialogued.business.details.customer.dialogue_ref = "dialogue-current";
        check("bargain_choose_during_dialogue_denied", BusinessActionPolicy.ValidateCandidate(choose, dialogued) != null);
        var noQuote = Snapshot(Copy(choosing), 2); noQuote.business.details.quote.status = "unavailable";
        check("bargain_choose_requires_visible_complete_quote", BusinessActionPolicy.ValidateCandidate(choose, noQuote) != null);
        noQuote = Snapshot(Copy(choosing), 2); noQuote.business.offer_ref = null;
        check("bargain_choose_requires_bound_offer", BusinessActionPolicy.ValidateCandidate(choose, noQuote) != null);
        noQuote = Snapshot(Copy(choosing), 2); noQuote.business.item_refs.Clear();
        check("bargain_choose_requires_nonempty_quote_items", BusinessActionPolicy.ValidateCandidate(choose, noQuote) != null);
        noQuote = Snapshot(Copy(choosing), 2); noQuote.business.details.quote.mode = "unknown";
        check("bargain_choose_requires_normal_quote", BusinessActionPolicy.ValidateCandidate(choose, noQuote) != null);
        noQuote = Snapshot(Copy(choosing), 2); noQuote.business.details.quote.displayed_total = null;
        check("bargain_choose_requires_displayed_price", BusinessActionPolicy.ValidateCandidate(choose, noQuote) != null);
        DuplicateCommandLifecycle(check);
    }

    private static void DuplicateCommandLifecycle(Action<string, bool> check)
    {
        // Pure managed callback: this exercises the production gate/driver lifecycle without
        // loading Unity or contacting a running game.
        var input = Input();
        var option = new BusinessBargainOptionInput { Identity = new object(), Text = "-10%", Kind = "lower_purchase_price",
            PriceChangePercent = -10, Enabled = true };
        input.Bargain.Phase = "choosing"; input.Bargain.Options = new[] { option };
        var projection = new BusinessProjection();
        var before = Snapshot(projection.Capture("w", true, () => input), 1);
        before.interaction.phase = "OPEN";
        string selectedRef = before.business.details.bargain.options[0].option_ref;
        var command = Command("choose_bargain_option", before, selectedRef);
        command.id = "bargain-once"; command.broker_epoch = "broker";
        command.expected_world_epoch = before.world_epoch; command.expected_version = before.version;
        command.expected_revision = before.revision; command.start_deadline_unix_ms = 100;
        command.execution_timeout_ms = 5000;
        before.legal_actions.Add(new LegalAction { action = command.action, arguments = command.arguments });
        var gate = new ActionGate();
        var driver = new ActionDriver(gate);
        int calls = 0;
        void Native(Command _)
        {
            calls++;
            input.Bargain.LastOptionRef = selectedRef;
            input.Bargain.TermsRevision++;
            input.Bargain.Result = "unchanged";
            option.Selected = true; option.Enabled = false;
        }
        check("bargain_lifecycle_accepts_duplicate_delivery_before_invocation", ActionGate.IsSnapshotValid(before) &&
            gate.Accept(command, before, "broker", 1) == null && gate.Accept(command, before, "broker", 1) == null && calls == 0);
        driver.Tick(() => { }, () => before, "broker", 2, 2, Native, BusinessActionPolicy.Verify);
        check("bargain_lifecycle_native_callback_invoked_once", calls == 1 && gate.CurrentProgress.stage == "invoking");
        check("bargain_lifecycle_duplicate_during_verification_does_not_reinvoke", gate.Accept(command, before, "broker", 3) == null && calls == 1);
        driver.Tick(() => { }, () => before, "broker", 3, 3, Native, BusinessActionPolicy.Verify);
        check("bargain_lifecycle_unacknowledged_frame_waits_without_retry", gate.PendingResult == null && gate.CurrentProgress.stage == "verifying" && calls == 1);
        var after = Snapshot(projection.Capture("w", true, () => input), 2);
        after.interaction.phase = "OPEN";
        driver.Tick(() => { }, () => after, "broker", 4, 4, Native, BusinessActionPolicy.Verify);
        check("bargain_lifecycle_unchanged_price_ack_succeeds_without_second_callback", gate.PendingResult?.status == "succeeded" && calls == 1 &&
            gate.PendingResult.before.business.details.quote.displayed_total == gate.PendingResult.after.business.details.quote.displayed_total &&
            gate.PendingResult.after.business.details.bargain.last_option_ref == selectedRef &&
            gate.PendingResult.after.business.details.bargain.terms_revision > gate.PendingResult.before.business.details.bargain.terms_revision);
        var duplicateResult = gate.Accept(command, after, "broker", 5);
        check("bargain_lifecycle_duplicate_completed_delivery_reuses_result", duplicateResult?.status == "succeeded" && calls == 1);
        check("bargain_lifecycle_duplicate_after_ack_is_rejected", gate.Acknowledge(command.id) &&
            gate.Accept(command, after, "broker", 6)?.reason == "command_already_acknowledged" && calls == 1);
        driver.Tick(() => { }, () => after, "broker", 7, 7, Native, BusinessActionPolicy.Verify);
        check("bargain_lifecycle_later_driver_tick_cannot_replay_acknowledged_command", calls == 1 && !gate.IsWriteBlocked && !driver.IsFailed);
    }

    private static BusinessObservation Copy(BusinessObservation b) => new BusinessObservation { support = b.support,
        customer_ref = b.customer_ref, offer_ref = b.offer_ref, collection_fingerprint = b.collection_fingerprint,
        item_refs = new List<string>(b.item_refs), details = BusinessWire.Clone(b.details) };
    private static Command Command(string action, Snapshot s, string option = null) => new Command { schema_version = 3, action = action,
        arguments = new ActionArguments { customer_ref = s.business.customer_ref, mode = "sell", option_ref = option } };
    private static Snapshot Snapshot(BusinessObservation b, int revision) => new Snapshot { schema_version = 3, loaded = true, ready = true,
        world_epoch = "w", scene = "shop", revision = revision, version = "w:" + revision, cash = 100, phase = "OPEN", business = b,
        interaction = new InteractionObservation { consistency = "consistent", operable = true }, legal_actions = new List<LegalAction>() };
    private static BusinessInput Input()
    {
        var item = new BusinessItemInput { Identity = new object(), Name = "Visible item", Units = 1 };
        return new BusinessInput { Customer = new BusinessCustomerInput { Identity = new object(), Presence = "present", Arrived = true,
                TradeType = "sell", Phase = "trading", DialogueStage = "absent", MultiBuyDisabled = false },
            Containers = new[] { new BusinessContainerInput { Container = "front", Completeness = "complete" },
                new BusinessContainerInput { Container = "back", Completeness = "complete", Items = new[] { item } },
                new BusinessContainerInput { Container = "player", Completeness = "complete" } },
            Quote = new BusinessQuoteInput { Status = "complete", Mode = "normal", BindingVerified = true, DisplayedTotal = 10,
                PurchaseIdentities = new[] { item.Identity } }, Ui = new BusinessUiInput { Status = "consistent", Coverage = "verified", Complete = true },
            Bargain = new BusinessBargainInput { Available = true, Phase = "closed", Direction = "sell" } };
    }
}
