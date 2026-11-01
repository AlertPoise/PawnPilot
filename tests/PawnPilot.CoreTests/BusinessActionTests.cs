using System;
using System.Collections.Generic;
using PawnPilot;

public static class BusinessActionTests
{
    public static void Run(Action<string, bool> check)
    {
        check("business_actions_registered_without_purchase", BusinessActionPolicy.IsRegistered("start_business") &&
            BusinessActionPolicy.IsRegistered("dismiss_customer") && !BusinessActionPolicy.IsRegistered("purchase_visible_offer"));
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
        check("argument_equality_preserves_item_order", !BusinessActionPolicy.SameArguments(
            new ActionArguments { item_refs = new List<string> { "item:1", "item:2" } },
            new ActionArguments { item_refs = new List<string> { "item:2", "item:1" } }));

        var args = new ActionArguments { customer_ref = "customer:1", dialogue_ref = "dialogue:1", mode = "next" };
        var before = State("present", "waiting", "dialogue:1", "customer:1", 4);
        before.legal_actions.Add(new LegalAction { action = "advance_dialogue", arguments = args });
        var command = new Command { schema_version = 3, action = "advance_dialogue", arguments = args };
        check("dto_legal_action_does_not_self_authorize_native_action",
            BusinessActionPolicy.Validate(command, before) == "native_action_evidence_required");
        var badEvidence = new NativeActionEvidence { Action = "advance_dialogue", Captured = true,
            SceneReady = true, UiCoverageVerified = false, UiConsistent = true, NoBlockers = true,
            NativeTargetAvailable = true, NativeEntryVerified = true, CapabilityScopeVerified = true };
        check("unknown_global_ui_coverage_fails_closed",
            BusinessActionPolicy.Validate(command, before, badEvidence) == "ui_scope_unverified");

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
        var pendingArrival = State("present", "arriving", null, "customer:next", 2, arrived: false);
        check("receive_customer_rejects_arrival_pending_boundary",
            !BusinessActionPolicy.Postcondition("receive_next_customer", absent, pendingArrival));

        var dismissed = State("absent", "absent", null, null, 6);
        var customerBeforeDismiss = State("present", "trading", null, "customer:1", 5);
        check("dismiss_postcondition_requires_customer_invalidation",
            BusinessActionPolicy.Postcondition("dismiss_customer", customerBeforeDismiss, dismissed));
        check("start_business_postcondition_requires_morning_to_open_transition",
            BusinessActionPolicy.Postcondition("start_business",
                State("absent", "absent", null, null, 1, storePhase: "MORNING"),
                State("absent", "absent", null, null, 2, storePhase: "OPEN")));
        var prepareBefore = CollectionState(8, new[] { "item:1", "item:2" }, new[] { "item:3" }, new[] { "item:4" });
        var prepareAfter = CollectionState(9, Array.Empty<string>(), new[] { "item:3", "item:1", "item:2" }, new[] { "item:4" });
        var prepareArgs = new ActionArguments { customer_ref = "customer:1", item_refs = new List<string> { "item:1", "item:2" }, collection_fingerprint = "collection:1" };
        prepareBefore.business.collection_fingerprint = "collection:1";
        prepareAfter.business.collection_fingerprint = "collection:2";
        prepareBefore.legal_actions.Add(new LegalAction { action = "prepare_purchase_items", arguments = prepareArgs });
        var prepareCommand = new Command { schema_version = 3, action = "prepare_purchase_items", arguments = prepareArgs };
        check("prepare_postcondition_requires_exact_front_to_back_set_delta",
            BusinessActionPolicy.Postcondition(prepareCommand, prepareBefore, prepareAfter));
        var partialPrepare = CollectionState(9, new[] { "item:2" }, new[] { "item:3", "item:1" }, new[] { "item:4" });
        partialPrepare.business.collection_fingerprint = "collection:2";
        check("prepare_postcondition_rejects_partial_native_transfer",
            !BusinessActionPolicy.Postcondition(prepareCommand, prepareBefore, partialPrepare));
        var subsetArgsCommand = new Command { schema_version = 3, action = "prepare_purchase_items",
            arguments = new ActionArguments { customer_ref = "customer:1", item_refs = new List<string> { "item:1" }, collection_fingerprint = "collection:1" } };
        check("prepare_postcondition_requires_complete_front_argument_set",
            !BusinessActionPolicy.Postcondition(subsetArgsCommand, prepareBefore, prepareAfter));
        var extraMutation = CollectionState(9, new[] { "item:2" }, new[] { "item:3", "item:1" }, new[] { "item:4", "item:2" });
        extraMutation.business.collection_fingerprint = "collection:2";
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
            UiCoverageVerified = true, UiConsistent = true, NoBlockers = true, NativeTargetAvailable = true,
            NativeEntryVerified = true, CapabilityScopeVerified = true, CurrentCustomerMatches = true,
            ItemSetMatches = true, TransferScopeVerified = true, TransferTargetCapacityVerified = false,
            OrdinaryScavGeneral = true, CallbackScopeVerified = true };
        check("prepare_requires_target_specific_capacity_proof",
            BusinessActionPolicy.Validate(prepareCommand, prepareBefore, prepareEvidence) == "prepare_scope_or_capacity_unverified");
        prepareEvidence.TransferTargetCapacityVerified = true;
        prepareEvidence.CallbackScopeVerified = false;
        check("prepare_requires_callback_scope_proof",
            BusinessActionPolicy.Validate(prepareCommand, prepareBefore, prepareEvidence) == "prepare_scope_or_capacity_unverified");
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
        check("cancel_confirmation_unknown_callback_is_blocked",
            BusinessActionPolicy.Validate(cancelCommand, cancelBefore, cancelEvidence) == "confirmation_cancel_callback_unverified");
        cancelEvidence.ConfirmationCancelSafe = true;
        check("cancel_confirmation_exact_typed_callback_can_pass_local_policy",
            BusinessActionPolicy.Validate(cancelCommand, cancelBefore, cancelEvidence) == null);
        var unknownCancel = State("present", "trading", null, "customer:1", 10);
        unknownCancel.business.details.ui.status = "blocked";
        unknownCancel.business.details.ui.blockers.Add("unknown_confirmation");
        unknownCancel.interaction.consistency = "blocked";
        unknownCancel.interaction.operable = false;
        unknownCancel.legal_actions.Add(new LegalAction { action = "cancel_confirmation", arguments = new ActionArguments() });
        check("cancel_confirmation_unknown_root_is_blocked",
            BusinessActionPolicy.Validate(cancelCommand, unknownCancel, cancelEvidence) == "confirmation_scope_unverified");
        var refusalBefore = State("present", "dialogue", "dialogue:1", "customer:1", 10, arrived: true);
        var refusalArgs = new ActionArguments { customer_ref = "customer:1" };
        refusalBefore.legal_actions.Add(new LegalAction { action = "refuse_trade", arguments = refusalArgs });
        check("refuse_trade_has_no_supported_native_postcondition",
            !BusinessActionPolicy.Postcondition("refuse_trade", refusalBefore,
                State("present", "dialogue", "dialogue:2", "customer:1", 11, arrived: true)));
        check("production_business_dispatch_hard_gate_closed", !BusinessActionPolicy.ProductionDispatchEnabled);
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
