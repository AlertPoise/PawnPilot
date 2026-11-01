using System;
using System.Collections.Generic;
using PawnPilot;

public static class BusinessActionTests
{
    public static void Run(Action<string, bool> check)
    {
        check("purchase_and_customer_actions_registered", BusinessActionPolicy.IsRegistered("start_business") &&
            BusinessActionPolicy.IsRegistered("dismiss_customer") && BusinessActionPolicy.IsRegistered("purchase_visible_offer"));
        check("implemented_business_actions_are_enabled", BusinessActionPolicy.IsEnabled("start_business") &&
            BusinessActionPolicy.IsEnabled("receive_next_customer") && BusinessActionPolicy.IsEnabled("advance_dialogue") &&
            BusinessActionPolicy.IsEnabled("choose_dialogue_option") && BusinessActionPolicy.IsEnabled("prepare_purchase_items") &&
            BusinessActionPolicy.IsEnabled("dismiss_customer") && BusinessActionPolicy.IsEnabled("purchase_visible_offer") &&
            !BusinessActionPolicy.IsEnabled("refuse_trade"));
        check("empty_action_arguments_are_exact", BusinessActionPolicy.ArgumentsValid("start_business", new ActionArguments()) &&
            !BusinessActionPolicy.ArgumentsValid("start_business", new ActionArguments { customer_ref = "customer:1" }));
        check("advance_requires_exact_mode_and_refs", BusinessActionPolicy.ArgumentsValid("advance_dialogue",
                new ActionArguments { customer_ref = "customer:1", dialogue_ref = "dialogue:2", mode = "skip" }) &&
            !BusinessActionPolicy.ArgumentsValid("advance_dialogue",
                new ActionArguments { customer_ref = "customer:1", dialogue_ref = "dialogue:2", mode = "arbitrary" }) &&
            !BusinessActionPolicy.ArgumentsValid("advance_dialogue",
                new ActionArguments { customer_ref = "customer:1", dialogue_ref = "dialogue:2", mode = "next", option_ref = "option:3" }));
        check("prepare_requires_bounded_unique_refs_and_fingerprint", BusinessActionPolicy.ArgumentsValid("prepare_purchase_items",
                new ActionArguments { customer_ref = "customer:1", item_refs = new List<string> { "item:1", "item:2" }, collection_fingerprint = "collection:1" }) &&
            !BusinessActionPolicy.ArgumentsValid("prepare_purchase_items",
                new ActionArguments { customer_ref = "customer:1", item_refs = new List<string> { "item:1", "item:1" }, collection_fingerprint = "collection:1" }));
        var purchaseArgs = new ActionArguments { customer_ref = "customer:1", offer_ref = "offer:1",
            collection_fingerprint = "collection:1", item_refs = new List<string> { "item:1" },
            mode = "normal", amount = 12, max_spend = 15 };
        check("purchase_arguments_require_exact_bound_normal_offer",
            BusinessActionPolicy.ArgumentsValid("purchase_visible_offer", purchaseArgs) &&
            !BusinessActionPolicy.ArgumentsValid("purchase_visible_offer", new ActionArguments {
                customer_ref = "customer:1", offer_ref = "offer:1", collection_fingerprint = "collection:1",
                item_refs = new List<string> { "item:1" }, mode = "wholesale", amount = 12, max_spend = 15 }));
        check("argument_equality_preserves_item_order", !BusinessActionPolicy.SameArguments(
            new ActionArguments { item_refs = new List<string> { "item:1", "item:2" } },
            new ActionArguments { item_refs = new List<string> { "item:2", "item:1" } }));

        var startBefore = State("absent", "absent", null, null, 1, storePhase: "MORNING");
        var startArgs = new ActionArguments();
        startBefore.legal_actions.Add(new LegalAction { action = "start_business", arguments = startArgs });
        var startCommand = new Command { schema_version = 3, action = "start_business", arguments = startArgs };
        check("start_business_dto_uses_captured_legal_candidate",
            BusinessActionPolicy.Validate(startCommand, startBefore) == null);
        startBefore.business.details.ui.status = "unverified";
        startBefore.business.details.ui.coverage = "known_roots_only";
        startBefore.business.details.ui.blockers.AddRange(new[] { "global_ui_stack_unobservable",
            "global_raycast_order_unobservable", "quote_rebuild_completion_unobservable",
            "tutorial_group_active_unverified", "control_hints_active_unverified" });
        startBefore.interaction.consistency = "unverified";
        startBefore.interaction.operable = false;
        check("start_ignores_only_unobservable_global_findings",
            BusinessActionPolicy.Validate(startCommand, startBefore) == null);
        startBefore.business.details.ui.blockers.Add("ui_reader_failed");
        check("start_rejects_reader_failure_even_with_global_findings",
            BusinessActionPolicy.Validate(startCommand, startBefore) == "known_ui_blocker");
        startBefore.business.details.ui.blockers.Remove("ui_reader_failed");
        startBefore.business.details.ui.blockers.Add("dialogue_active");
        check("start_rejects_actual_dialogue_modal",
            BusinessActionPolicy.Validate(startCommand, startBefore) == "known_ui_blocker");
        startBefore.business.details.ui.blockers.Clear();
        startBefore.business.details.ui.blockers.Add("item_drag_active");
        check("start_rejects_actual_item_drag",
            BusinessActionPolicy.Validate(startCommand, startBefore) == "known_ui_blocker");
        var receiveBefore = State("absent", "absent", null, null, 1, storePhase: "OPEN");
        receiveBefore.legal_actions.Add(new LegalAction { action = "receive_next_customer", arguments = new ActionArguments() });
        check("receive_next_dto_has_no_inventory_requirement",
            BusinessActionPolicy.Validate(new Command { schema_version = 3, action = "receive_next_customer",
                arguments = new ActionArguments() }, receiveBefore) == null);

        var args = new ActionArguments { customer_ref = "customer:1", dialogue_ref = "dialogue:1", mode = "next" };
        var before = State("present", "waiting", "dialogue:1", "customer:1", 4);
        before.legal_actions.Add(new LegalAction { action = "advance_dialogue", arguments = args });
        var command = new Command { schema_version = 3, action = "advance_dialogue", arguments = args };
        check("dialogue_action_uses_matching_legal_candidate",
            BusinessActionPolicy.Validate(command, before) == null);
        var badEvidence = new NativeActionEvidence { Action = "advance_dialogue", Captured = true,
            SceneReady = true, UiCoverageVerified = false, UiConsistent = true, NoBlockers = true,
            NativeTargetAvailable = true, NativeEntryVerified = true, CapabilityScopeVerified = true };
        check("dialogue_action_requires_current_visible_native_scope",
            BusinessActionPolicy.Validate(command, before, badEvidence) == "dialogue_scope_or_callback_unverified");

        var choiceArgs = new ActionArguments { customer_ref = "customer:1", dialogue_ref = "dialogue:1", option_ref = "option:1" };
        var choiceBefore = State("present", "choices", "dialogue:1", "customer:1", 6, arrived: true);
        choiceBefore.business.details.customer.dialogue_stage = "choices";
        choiceBefore.business.details.customer.option_refs.Add("option:1");
        choiceBefore.business.details.ui.blockers.AddRange(new[] { "global_ui_stack_unobservable", "dialogue_active" });
        choiceBefore.legal_actions.Add(new LegalAction { action = "choose_dialogue_option", arguments = choiceArgs });
        var choiceCommand = new Command { schema_version = 3, action = "choose_dialogue_option", arguments = choiceArgs };
        var choiceEvidence = new NativeActionEvidence { Action = "choose_dialogue_option", Captured = true, SceneReady = true,
            NativeTargetAvailable = true, NativeEntryVerified = true, CapabilityScopeVerified = true,
            CurrentCustomerMatches = true, CustomerArrived = true, OrdinarySeller = true,
            DialogueMatches = true, DialogueChoice = true, OptionVisibleEnabled = true };
        check("choice_accepts_visible_current_option_despite_dialogue_modal_marker",
            BusinessActionPolicy.Validate(choiceCommand, choiceBefore, choiceEvidence) == null);
        choiceEvidence.OptionVisibleEnabled = false;
        check("choice_rejects_option_no_longer_visible_or_enabled",
            BusinessActionPolicy.Validate(choiceCommand, choiceBefore, choiceEvidence) == "choice_scope_or_callback_unverified");

        var after = State("present", "typing", "dialogue:2", "customer:1", 5);
        check("dialogue_advance_postcondition_requires_same_customer_and_progress",
            BusinessActionPolicy.Postcondition("advance_dialogue", before, after));
        var replaced = State("present", "typing", "dialogue:2", "customer:2", 5);
        check("dialogue_postcondition_rejects_customer_replacement",
            !BusinessActionPolicy.Postcondition("advance_dialogue", before, replaced));
        after.cash++;
        check("non_economic_dialogue_postcondition_rejects_cash_change",
            !BusinessActionPolicy.Postcondition("advance_dialogue", before, after));

        var absent = State("absent", "absent", null, null, 1);
        var arriving = State("present", "dialogue", "dialogue:next", "customer:next", 2, arrived: true);
        arriving.business.details.customer.dialogue_stage = "typing";
        check("receive_customer_postcondition_waits_for_arrival_and_dialogue",
            BusinessActionPolicy.Postcondition("receive_next_customer", absent, arriving));
        var arrivedTrading = State("present", "trading", null, "customer:next", 2, arrived: true);
        check("receive_customer_accepts_arrived_customer_without_opening_dialogue",
            BusinessActionPolicy.Postcondition("receive_next_customer", absent, arrivedTrading));
        var pendingArrival = State("present", "arriving", null, "customer:next", 2, arrived: false);
        check("receive_customer_rejects_arrival_pending_boundary",
            !BusinessActionPolicy.Postcondition("receive_next_customer", absent, pendingArrival));

        var dismissed = State("absent", "absent", null, null, 6);
        var customerBeforeDismiss = State("present", "trading", null, "customer:1", 5);
        check("dismiss_postcondition_requires_customer_invalidation",
            BusinessActionPolicy.Postcondition("dismiss_customer", customerBeforeDismiss, dismissed));
        var nextCustomer = State("present", "arriving", null, "customer:next", 6, arrived: false);
        check("dismiss_postcondition_accepts_auto_called_replacement_customer",
            BusinessActionPolicy.Postcondition("dismiss_customer", customerBeforeDismiss, nextCustomer));
        check("start_business_postcondition_requires_morning_to_open_transition",
            BusinessActionPolicy.Postcondition("start_business",
                State("absent", "absent", null, null, 1, storePhase: "MORNING"),
                State("absent", "absent", null, null, 2, storePhase: "OPEN")));
        var dismissArgs = new ActionArguments { customer_ref = "customer:1" };
        customerBeforeDismiss.business.details.customer.trade_type = "other";
        customerBeforeDismiss.legal_actions.Add(new LegalAction { action = "dismiss_customer", arguments = dismissArgs });
        var dismissCommand = new Command { schema_version = 3, action = "dismiss_customer", arguments = dismissArgs };
        var dismissEvidence = new NativeActionEvidence { Action = "dismiss_customer", Captured = true, SceneReady = true,
            NativeTargetAvailable = true, NativeEntryVerified = true, CapabilityScopeVerified = true,
            CurrentCustomerMatches = true, CustomerArrived = true, StoreOpen = true, NoLocks = true,
            OrdinarySeller = false, Dismissable = true, DismissInterceptClear = true };
        check("dismiss_other_trade_without_enabling_purchase", BusinessActionPolicy.Validate(dismissCommand,
            customerBeforeDismiss, dismissEvidence) == null);
        dismissEvidence.NoLocks = false;
        check("dismiss_rejects_native_bell_lock", BusinessActionPolicy.Validate(dismissCommand,
            customerBeforeDismiss, dismissEvidence) == "dismiss_state_or_callback_unverified");
        dismissEvidence.NoLocks = true;
        dismissEvidence.DismissInterceptClear = false;
        check("dismiss_rejects_special_dismiss_callback", BusinessActionPolicy.Validate(dismissCommand,
            customerBeforeDismiss, dismissEvidence) == "dismiss_state_or_callback_unverified");

        var prepareBefore = CollectionState(8, new[] { "item:1", "item:2" }, new[] { "item:3" }, new[] { "item:4" });
        var prepareAfter = CollectionState(9, Array.Empty<string>(), new[] { "item:3", "item:1", "item:2" }, new[] { "item:4" });
        var prepareArgs = new ActionArguments { customer_ref = "customer:1", item_refs = new List<string> { "item:1", "item:2" }, collection_fingerprint = "collection:1" };
        prepareBefore.business.collection_fingerprint = "collection:1";
        prepareBefore.business.details.front_collection_fingerprint = "collection:1";
        prepareAfter.business.collection_fingerprint = "collection:2";
        prepareAfter.business.details.front_collection_fingerprint = "collection:2";
        prepareBefore.legal_actions.Add(new LegalAction { action = "prepare_purchase_items", arguments = prepareArgs });
        var prepareCommand = new Command { schema_version = 3, action = "prepare_purchase_items", arguments = prepareArgs };
        check("prepare_postcondition_requires_exact_front_to_back_set_delta",
            BusinessActionPolicy.Postcondition(prepareCommand, prepareBefore, prepareAfter));
        var partialPrepare = CollectionState(9, new[] { "item:2" }, new[] { "item:3", "item:1" }, new[] { "item:4" });
        partialPrepare.business.collection_fingerprint = "collection:2";
        partialPrepare.business.details.front_collection_fingerprint = "collection:2";
        check("prepare_postcondition_rejects_partial_native_transfer",
            !BusinessActionPolicy.Postcondition(prepareCommand, prepareBefore, partialPrepare));
        check("prepare_partial_transfer_is_uncertain_and_not_retryable",
            BusinessActionPolicy.Verify(prepareCommand, prepareBefore, partialPrepare).reason == "prepare_partial_or_unexpected_collection_change");
        var subsetArgsCommand = new Command { schema_version = 3, action = "prepare_purchase_items",
            arguments = new ActionArguments { customer_ref = "customer:1", item_refs = new List<string> { "item:1" }, collection_fingerprint = "collection:1" } };
        check("prepare_postcondition_requires_complete_front_argument_set",
            !BusinessActionPolicy.Postcondition(subsetArgsCommand, prepareBefore, prepareAfter));
        var extraMutation = CollectionState(9, new[] { "item:2" }, new[] { "item:3", "item:1" }, new[] { "item:4", "item:2" });
        extraMutation.business.collection_fingerprint = "collection:2";
        extraMutation.business.details.front_collection_fingerprint = "collection:2";
        extraMutation.business.details.containers[0].item_refs = new List<string> { "item:2" };
        extraMutation.business.details.containers[1].item_refs = new List<string> { "item:3", "item:1" };
        extraMutation.business.details.containers[2].item_refs = new List<string> { "item:4", "item:2" };
        check("prepare_postcondition_rejects_extra_player_mutation",
            !BusinessActionPolicy.Postcondition(prepareCommand, prepareBefore, extraMutation));
        var metadataMutation = CollectionState(9, Array.Empty<string>(), new[] { "item:3", "item:1", "item:2" }, new[] { "item:4" });
        metadataMutation.business.collection_fingerprint = "collection:2";
        metadataMutation.business.details.items.Find(item => item.item_ref == "item:2").units = 2;
        check("prepare_postcondition_rejects_unit_mutation",
            !BusinessActionPolicy.Postcondition(prepareCommand, prepareBefore, metadataMutation));
        var prepareEvidence = new NativeActionEvidence { Action = "prepare_purchase_items", Captured = true, SceneReady = true,
            NativeTargetAvailable = true, NativeEntryVerified = true, CapabilityScopeVerified = true,
            CurrentCustomerMatches = true, CustomerArrived = true, ItemSetMatches = true,
            TransferScopeVerified = true, TransferTargetCapacityVerified = false,
            OrdinarySeller = true, CallbackScopeVerified = false };
        prepareBefore.business.details.containers.RemoveAll(container => container.container == "player");
        check("prepare_needs_complete_front_set_but_not_player_capacity_or_callbacks",
            BusinessActionPolicy.Validate(prepareCommand, prepareBefore, prepareEvidence) == null);
        var cancelBefore = State("present", "trading", null, "customer:1", 10);
        cancelBefore.business.details.ui.status = "blocked";
        cancelBefore.business.details.ui.blockers.Add("generic_confirmation");
        cancelBefore.interaction.consistency = "blocked";
        cancelBefore.interaction.operable = false;
        cancelBefore.legal_actions.Add(new LegalAction { action = "cancel_confirmation", arguments = new ActionArguments() });
        var cancelAfter = State("present", "trading", null, "customer:1", 11);
        check("cancel_confirmation_requires_known_blocker_disappearance",
            BusinessActionPolicy.Postcondition("cancel_confirmation", cancelBefore, cancelAfter));
        var cancelCommand = new Command { schema_version = 3, action = "cancel_confirmation", arguments = new ActionArguments() };
        var cancelEvidence = new NativeActionEvidence { Action = "cancel_confirmation", Captured = true, SceneReady = true,
            UiCoverageVerified = true, UiConsistent = true, OnlyTargetBlocker = true, NativeTargetAvailable = true,
            NativeEntryVerified = true, CapabilityScopeVerified = true, ConfirmationOpen = true,
            ConfirmationKind = "generic_confirmation", ConfirmationCancelSafe = false };
        check("cancel_confirmation_remains_disabled",
            BusinessActionPolicy.Validate(cancelCommand, cancelBefore, cancelEvidence) == "action_not_enabled");
        cancelEvidence.ConfirmationCancelSafe = true;
        check("cancel_confirmation_cannot_be_enabled_by_dto_evidence",
            BusinessActionPolicy.Validate(cancelCommand, cancelBefore, cancelEvidence) == "action_not_enabled");
        var unknownCancel = State("present", "trading", null, "customer:1", 10);
        unknownCancel.business.details.ui.status = "blocked";
        unknownCancel.business.details.ui.blockers.Add("unknown_confirmation");
        unknownCancel.interaction.consistency = "blocked";
        unknownCancel.interaction.operable = false;
        unknownCancel.legal_actions.Add(new LegalAction { action = "cancel_confirmation", arguments = new ActionArguments() });
        check("cancel_confirmation_unknown_root_stays_disabled",
            BusinessActionPolicy.Validate(cancelCommand, unknownCancel, cancelEvidence) == "action_not_enabled");
        var refusalBefore = State("present", "dialogue", "dialogue:1", "customer:1", 10, arrived: true);
        var refusalArgs = new ActionArguments { customer_ref = "customer:1" };
        refusalBefore.legal_actions.Add(new LegalAction { action = "refuse_trade", arguments = refusalArgs });
        check("refuse_trade_has_no_supported_native_postcondition",
            !BusinessActionPolicy.Postcondition("refuse_trade", refusalBefore,
                State("present", "dialogue", "dialogue:2", "customer:1", 11, arrived: true)));
        check("dialogue_candidate_runtime_enabled", BusinessActionPolicy.IsEnabled("choose_dialogue_option"));
        check("cash_change_after_non_economic_action_is_uncertain",
            BusinessActionPolicy.Verify(command, before, State("present", "typing", "dialogue:2", "customer:1", 5, cash:11)).outcome == "uncertain");
    }

    private static Snapshot State(string presence, string phase, string dialogueRef, string customerRef,
        int revision, bool? arrived = null, int cash = 10, string storePhase = "MORNING")
    {
        var details = new BusinessDetails { status = "complete" };
        details.customer.presence = presence;
        details.customer.phase = phase;
        details.customer.dialogue_ref = dialogueRef;
        details.customer.dialogue_stage = dialogueRef == null ? "absent" : phase;
        details.customer.arrived = arrived;
        var ui = details.ui;
        ui.status = "consistent";
        ui.coverage = "verified";
        var business = new BusinessObservation { support = "available", customer_ref = customerRef, details = details };
        return new Snapshot
        {
            schema_version = 3, version = "world:1:" + revision, world_epoch = "world:1", revision = revision,
            loaded = true, ready = true, scene = "InventoryScene", phase = storePhase, cash = cash,
            interaction = new InteractionObservation { consistency = "consistent", operable = true, phase = storePhase },
            business = business, legal_actions = new List<LegalAction>()
        };
    }

    private static Snapshot CollectionState(int revision, string[] front, string[] back, string[] player)
    {
        var snapshot = State("present", "trading", null, "customer:1", revision);
        snapshot.business.details.containers = new List<ContainerState>
        {
            Container("front", front), Container("back", back), Container("player", player)
        };
        var all = new HashSet<string>(front);
        all.UnionWith(back);
        all.UnionWith(player);
        snapshot.business.details.items = new List<ItemState>();
        foreach (var reference in all)
            snapshot.business.details.items.Add(new ItemState { item_ref = reference, units = 1, owned = reference == "item:4" });
        return snapshot;
    }

    private static ContainerState Container(string name, string[] refs) => new ContainerState
    {
        container = name, completeness = "complete", item_refs = new List<string>(refs)
    };
}
