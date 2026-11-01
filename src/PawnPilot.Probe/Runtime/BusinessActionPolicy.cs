using System;
using System.Collections.Generic;
using System.Linq;

namespace PawnPilot
{
    // Process-local facts gathered immediately before a native action. Never attach this type to
    // Snapshot, JSON, progress, or a result. The production reader leaves capability fields false
    // unless the complete scene and exact callback chain have been verified.
    internal sealed class NativeActionEvidence
    {
        internal string Action, Reason;
        internal bool Captured, SceneReady, UiCoverageVerified, UiConsistent, NoBlockers;
        internal bool NativeTargetAvailable, NativeEntryVerified, CapabilityScopeVerified;
        internal bool StoreClosed, StoreOpen, CanStartDay, NoLocks, NoCurrentCustomer, CanNextClient;
        internal bool AnyCurrentCustomer, StoreLockReleased, MainDialogueReady, NoCallNextPending;
        internal bool CurrentCustomerMatches, CustomerArrived, OrdinaryScavGeneral, CallbackScopeVerified;
        internal bool DialogueMatches, DialogueTyping, WaitingForAdvance, DialogueChoice, NextDialogueExists;
        internal bool OptionVisibleEnabled, ItemSetMatches, TransferScopeVerified, TransferTargetCapacityVerified;
        internal bool ConfirmationOpen, ConfirmationCancelSafe, OnlyTargetBlocker;
        internal string ConfirmationKind;
        internal object NativeTarget = null;
        public bool Dismissable, DismissInterceptClear, NoPendingEndAction, AutoCallNextDisabled;
    }

    // Pure DTO-only action policy. Static IL evidence identifies candidate native entries, but no
    // business action is admitted until GameBusinessActions supplies fresh local evidence.
    public static class BusinessActionPolicy
    {
        // Static UI/callback/scene scope has not passed the required independent live review.
        // Keep this false until that review is complete; registration remains descriptive only.
        public static bool ProductionDispatchEnabled => false;

        private static readonly string[] Registered =
        {
            "start_business", "receive_next_customer", "advance_dialogue", "choose_dialogue_option",
            "prepare_purchase_items", "cancel_confirmation", "refuse_trade", "dismiss_customer"
        };

        public static bool IsRegistered(string action) => action != null && Array.IndexOf(Registered, action) >= 0;

        public static bool ArgumentsValid(string action, ActionArguments a)
        {
            if (!IsRegistered(action) || a == null) return false;
            switch (action)
            {
                case "start_business":
                case "receive_next_customer":
                case "cancel_confirmation":
                    return Empty(a);
                case "advance_dialogue":
                    return Ref(a.customer_ref) && Ref(a.dialogue_ref) &&
                        (a.mode == "skip" || a.mode == "next") &&
                        a.item_refs == null && a.offer_ref == null && a.collection_fingerprint == null &&
                        a.option_ref == null && a.authorization_ref == null && !a.amount.HasValue && !a.max_spend.HasValue;
                case "choose_dialogue_option":
                    return Ref(a.customer_ref) && Ref(a.dialogue_ref) && Ref(a.option_ref) &&
                        a.item_refs == null && a.offer_ref == null && a.collection_fingerprint == null &&
                        a.mode == null && a.authorization_ref == null && !a.amount.HasValue && !a.max_spend.HasValue;
                case "prepare_purchase_items":
                    return Ref(a.customer_ref) && a.item_refs != null && a.item_refs.Count > 0 && a.item_refs.Count <= 64 &&
                        a.item_refs.All(Ref) && a.item_refs.Distinct(StringComparer.Ordinal).Count() == a.item_refs.Count &&
                        Ref(a.collection_fingerprint) && a.offer_ref == null && a.option_ref == null &&
                        a.dialogue_ref == null && a.mode == null && a.authorization_ref == null &&
                        !a.amount.HasValue && !a.max_spend.HasValue;
                case "refuse_trade":
                case "dismiss_customer":
                    return Ref(a.customer_ref) && a.item_refs == null && a.offer_ref == null &&
                        a.collection_fingerprint == null && a.option_ref == null && a.dialogue_ref == null &&
                        a.mode == null && a.authorization_ref == null && !a.amount.HasValue && !a.max_spend.HasValue;
                default:
                    return false;
            }
        }

        // Used by snapshot validation before policy admission. It checks only argument shape; a
        // matching legal_actions entry alone is never evidence that native execution is safe.
        public static bool SameArguments(ActionArguments a, ActionArguments b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.customer_ref != b.customer_ref || a.offer_ref != b.offer_ref ||
                a.collection_fingerprint != b.collection_fingerprint || a.option_ref != b.option_ref ||
                a.dialogue_ref != b.dialogue_ref || a.mode != b.mode || a.authorization_ref != b.authorization_ref ||
                a.amount != b.amount || a.max_spend != b.max_spend) return false;
            if (a.item_refs == null || b.item_refs == null) return a.item_refs == null && b.item_refs == null;
            return a.item_refs.SequenceEqual(b.item_refs, StringComparer.Ordinal);
        }

        public static string Validate(Command command, Snapshot snapshot)
        {
            string dtoReason = ValidateDto(command, snapshot);
            if (dtoReason != null) return dtoReason;
            // DTO evidence cannot prove Unity target identity, topmost hitability, complete modal
            // coverage, or the per-customer callback closure. Require Check(command,before)'s local
            // evidence overload even after the scene-specific policy becomes independently proved.
            return "native_action_evidence_required";
        }

        internal static string Validate(Command command, Snapshot snapshot, NativeActionEvidence evidence)
        {
            string reason = ValidateDto(command, snapshot);
            if (reason != null) return reason;
            if (evidence == null || !evidence.Captured || evidence.Action != command.action) return "native_action_evidence_missing";
            if (!evidence.SceneReady) return evidence.Reason ?? "scene_not_ready";
            if (command.action == "cancel_confirmation")
            {
                if (!evidence.UiCoverageVerified || !evidence.UiConsistent || !evidence.OnlyTargetBlocker)
                    return evidence.Reason ?? "confirmation_scope_unverified";
                if (!evidence.NativeTargetAvailable || !evidence.NativeEntryVerified || !evidence.CapabilityScopeVerified)
                    return evidence.Reason ?? "native_scope_unverified";
                return evidence.ConfirmationOpen && evidence.ConfirmationCancelSafe &&
                    string.Equals(evidence.ConfirmationKind, snapshot.business.details.ui.blockers[0], StringComparison.Ordinal)
                    ? null : (evidence.ConfirmationOpen ? "confirmation_cancel_callback_unverified" : "no_confirmation");
            }
            if (!evidence.UiCoverageVerified || !evidence.UiConsistent || !evidence.NoBlockers)
                return evidence.Reason ?? "ui_scope_unverified";
            if (!evidence.NativeTargetAvailable || !evidence.NativeEntryVerified || !evidence.CapabilityScopeVerified)
                return evidence.Reason ?? "native_scope_unverified";

            var state = snapshot.business.details.customer;
            var args = command.arguments;
            switch (command.action)
            {
                case "start_business":
                    return snapshot.phase == "MORNING" && evidence.StoreClosed && evidence.CanStartDay && evidence.NoLocks
                        ? null : "start_business_precondition";
                case "receive_next_customer":
                    return snapshot.phase == "OPEN" && evidence.StoreOpen && evidence.NoLocks && evidence.NoCurrentCustomer && evidence.CanNextClient
                        ? null : "receive_next_customer_precondition";
                case "advance_dialogue":
                    if (!CustomerMatches(snapshot.business.customer_ref, state, args.customer_ref) || state.dialogue_ref != args.dialogue_ref ||
                        !evidence.CurrentCustomerMatches || !evidence.CustomerArrived || !evidence.OrdinaryScavGeneral ||
                        !evidence.CallbackScopeVerified || !evidence.DialogueMatches || evidence.DialogueChoice)
                        return "dialogue_scope_or_callback_unverified";
                    if (args.mode == "skip") return evidence.DialogueTyping ? null : "skip_requires_typing";
                    if (args.mode == "next") return !evidence.DialogueTyping && evidence.WaitingForAdvance && evidence.NextDialogueExists
                        ? null : "next_requires_manual_advance";
                    return "invalid_arguments";
                case "choose_dialogue_option":
                    return CustomerMatches(snapshot.business.customer_ref, state, args.customer_ref) &&
                        state.dialogue_ref == args.dialogue_ref && state.option_refs != null &&
                        state.option_refs.Contains(args.option_ref) && evidence.CurrentCustomerMatches &&
                        evidence.CustomerArrived && evidence.CallbackScopeVerified && evidence.DialogueMatches &&
                        evidence.DialogueChoice && evidence.OptionVisibleEnabled
                        ? null : "choice_scope_or_callback_unverified";
                case "prepare_purchase_items":
                    return evidence.CurrentCustomerMatches && CustomerMatches(snapshot.business.customer_ref, state, args.customer_ref) &&
                        evidence.ItemSetMatches && evidence.TransferScopeVerified && evidence.TransferTargetCapacityVerified &&
                        evidence.OrdinaryScavGeneral && evidence.CallbackScopeVerified
                        ? null : "prepare_scope_or_capacity_unverified";
                case "cancel_confirmation":
                    return "confirmation_scope_unverified";
                case "refuse_trade":
                    return "refuse_native_path_unsupported";
                case "dismiss_customer":
                    if (!CustomerMatches(snapshot.business.customer_ref, state, args.customer_ref) || !evidence.CurrentCustomerMatches ||
                        !evidence.OrdinaryScavGeneral || !evidence.CallbackScopeVerified || !evidence.Dismissable ||
                        !evidence.DismissInterceptClear || !evidence.NoPendingEndAction || !evidence.AutoCallNextDisabled)
                        return "dismiss_callback_or_autocall_unverified";
                    return null;
                default:
                    return "unknown_action";
            }
        }

        private static string ValidateDto(Command command, Snapshot snapshot)
        {
            if (command == null || !IsRegistered(command.action)) return "unknown_action";
            if (!ArgumentsValid(command.action, command.arguments)) return "invalid_arguments";
            if (!BaseSnapshotReady(snapshot)) return "not_ready";
            if (!HasMatchingLegalAction(command, snapshot)) return "action_not_legal_in_snapshot";
            var ui = snapshot.business.details.ui;
            if (snapshot.menu_open || ui.coverage != "verified") return "ui_scope_unverified";
            if (command.action == "cancel_confirmation")
            {
                if (!HasExactlyOneConfirmationBlocker(ui.blockers) || ui.status != "blocked" ||
                    snapshot.interaction.consistency != "blocked" || snapshot.interaction.operable)
                    return "confirmation_scope_unverified";
            }
            else if (snapshot.interaction.consistency != "consistent" || !snapshot.interaction.operable ||
                ui.status != "consistent") return "ui_scope_unverified";
            string dtoReason = ValidateDtoPreconditions(command, snapshot);
            if (dtoReason != null) return dtoReason;
            return null;
        }

        private static string ValidateDtoPreconditions(Command command, Snapshot snapshot)
        {
            var args = command.arguments;
            var customer = snapshot.business.details.customer;
            switch (command.action)
            {
                case "start_business":
                    return snapshot.phase == "MORNING" ? null : "store_not_closed_for_business";
                case "receive_next_customer":
                    return snapshot.phase == "OPEN" && customer.presence == "absent"
                        ? null : "customer_already_present_or_store_closed";
                case "advance_dialogue":
                    return CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref) &&
                        customer.dialogue_ref == args.dialogue_ref &&
                        (args.mode == "skip" ? customer.dialogue_stage == "typing" : customer.dialogue_stage == "waiting")
                        ? null : "dialogue_state_changed";
                case "choose_dialogue_option":
                    return CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref) &&
                        customer.dialogue_ref == args.dialogue_ref && customer.dialogue_stage == "choices" &&
                        customer.option_refs != null && customer.option_refs.Contains(args.option_ref)
                        ? null : "choice_not_visible";
                case "prepare_purchase_items":
                    return CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref) &&
                        args.collection_fingerprint == snapshot.business.collection_fingerprint &&
                        ExactFrontSelection(snapshot, args.item_refs) ? null : "front_collection_changed";
                case "dismiss_customer":
                case "refuse_trade":
                    return CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref)
                        ? null : "customer_changed";
                case "cancel_confirmation":
                    return HasExactlyOneConfirmationBlocker(snapshot.business.details.ui.blockers)
                        ? null : "confirmation_kind_unverified";
                default:
                    return "unknown_action";
            }
        }

        private static bool ExactFrontSelection(Snapshot snapshot, List<string> refs)
        {
            if (refs == null || !TryGetCompleteContainer(snapshot, "front", out var front) ||
                !TryGetCompleteContainer(snapshot, "back", out _) || !TryGetCompleteContainer(snapshot, "player", out _)) return false;
            return refs.Count == front.item_refs.Count && refs.ToHashSet(StringComparer.Ordinal)
                .SetEquals(front.item_refs);
        }

        private static bool BaseSnapshotReady(Snapshot s) => s != null && s.schema_version == Protocol.BusinessSchemaVersion &&
            s.loaded && s.ready && s.business != null && s.business.support == "available" && s.business.details != null &&
            s.business.details.status == "complete" && s.business.details.customer != null && s.business.details.ui != null &&
            s.interaction != null && s.legal_actions != null;

        private static bool HasMatchingLegalAction(Command c, Snapshot s)
        {
            foreach (var legal in s.legal_actions)
                if (legal != null && legal.action == c.action && SameArguments(legal.arguments, c.arguments)) return true;
            return false;
        }

        private static bool CustomerMatches(string observedRef, CustomerState state, string customerRef) =>
            state != null && state.presence == "present" && !string.IsNullOrEmpty(customerRef) &&
            string.Equals(observedRef, customerRef, StringComparison.Ordinal) && state.phase != "leaving";

        public static bool Postcondition(string action, Snapshot before, Snapshot after)
        {
            if (before == null || after == null || !before.ready || !after.ready ||
                before.schema_version != Protocol.BusinessSchemaVersion || after.schema_version != Protocol.BusinessSchemaVersion ||
                before.world_epoch != after.world_epoch || before.scene != after.scene || after.revision <= before.revision ||
                before.cash != after.cash || before.business?.details?.customer == null || after.business?.details?.customer == null)
                return false;

            var oldCustomer = before.business.details.customer;
            var newCustomer = after.business.details.customer;
            switch (action)
            {
                case "start_business":
                    return before.phase == "MORNING" && after.phase == "OPEN";
                case "receive_next_customer":
                    return oldCustomer.presence == "absent" && newCustomer.presence == "present" &&
                        newCustomer.arrived == true && newCustomer.phase == "dialogue" &&
                        newCustomer.dialogue_ref != null &&
                        (newCustomer.dialogue_stage == "typing" || newCustomer.dialogue_stage == "waiting" ||
                         newCustomer.dialogue_stage == "choices");
                case "advance_dialogue":
                    return oldCustomer.presence == "present" && newCustomer.presence == "present" &&
                        SameCustomerRef(before, after) && (oldCustomer.dialogue_ref != newCustomer.dialogue_ref ||
                            oldCustomer.dialogue_stage != newCustomer.dialogue_stage);
                case "choose_dialogue_option":
                    return oldCustomer.presence == "present" && newCustomer.presence == "present" &&
                        SameCustomerRef(before, after) && oldCustomer.dialogue_ref != null &&
                        oldCustomer.dialogue_ref != newCustomer.dialogue_ref;
                case "prepare_purchase_items":
                    return false; // requested refs are required; only the Command overload can prove the delta.
                case "cancel_confirmation":
                    return ConfirmationRemoved(before, after);
                case "refuse_trade":
                    return false;
                case "dismiss_customer":
                    return oldCustomer.presence == "present" && newCustomer.presence == "absent" &&
                        before.business.customer_ref != null && after.business.customer_ref == null;
                case "close_menu":
                    return before.menu_open && !after.menu_open;
                default:
                    return false;
            }
        }

        public static VerificationDecision Verify(Command command, Snapshot before, Snapshot after)
        {
            if (command == null || !IsRegistered(command.action) && command.action != "close_menu")
                return VerificationDecision.Uncertain("unsupported_verifier_action");
            if (after == null || !after.ready) return VerificationDecision.Pending();
            if (before == null || before.world_epoch != after.world_epoch || before.scene != after.scene)
                return VerificationDecision.Uncertain("world_changed_after_action");
            if (before.cash != after.cash) return VerificationDecision.Uncertain("cash_changed_during_non_economic_action");
            if (Postcondition(command, before, after)) return VerificationDecision.Succeeded();
            return VerificationDecision.Pending();
        }

        public static bool Postcondition(Command command, Snapshot before, Snapshot after)
        {
            if (command == null) return false;
            if (command.action != "prepare_purchase_items") return Postcondition(command.action, before, after);
            return BaselinePostcondition(before, after) && ExactFrontToBackMove(command.arguments, before, after);
        }

        private static bool BaselinePostcondition(Snapshot before, Snapshot after) =>
            before != null && after != null && before.ready && after.ready &&
            before.schema_version == Protocol.BusinessSchemaVersion && after.schema_version == Protocol.BusinessSchemaVersion &&
            string.Equals(before.world_epoch, after.world_epoch, StringComparison.Ordinal) &&
            string.Equals(before.scene, after.scene, StringComparison.Ordinal) && after.revision > before.revision &&
            before.cash == after.cash && before.business?.details?.customer != null &&
            after.business?.details?.customer != null;

        // Full ordinary collection differential: requested direct references must leave front and
        // appear in back, all unrequested members and the player inventory must be unchanged, and
        // item units/ownership must remain stable. Names and list positions are never identities.
        private static bool ExactFrontToBackMove(ActionArguments args, Snapshot before, Snapshot after)
        {
            if (args?.item_refs == null || args.collection_fingerprint != before.business.collection_fingerprint ||
                before.business.collection_fingerprint == null || !SameCustomerRef(before, after)) return false;
            if (!TryGetCompleteContainer(before, "front", out var oldFront) ||
                !TryGetCompleteContainer(before, "back", out var oldBack) ||
                !TryGetCompleteContainer(before, "player", out var oldPlayer) ||
                !TryGetCompleteContainer(after, "front", out var newFront) ||
                !TryGetCompleteContainer(after, "back", out var newBack) ||
                !TryGetCompleteContainer(after, "player", out var newPlayer)) return false;
            var requested = new HashSet<string>(args.item_refs, StringComparer.Ordinal);
            var source = new HashSet<string>(oldFront.item_refs, StringComparer.Ordinal);
            var target = new HashSet<string>(oldBack.item_refs, StringComparer.Ordinal);
            if (!requested.SetEquals(source)) return false; // this button transfers the whole visible front set.
            var expectedFront = new HashSet<string>(source, StringComparer.Ordinal);
            expectedFront.ExceptWith(requested);
            var expectedBack = new HashSet<string>(target, StringComparer.Ordinal);
            expectedBack.UnionWith(requested);
            if (!expectedFront.SetEquals(newFront.item_refs) || !expectedBack.SetEquals(newBack.item_refs) ||
                !oldPlayer.item_refs.SequenceEqual(newPlayer.item_refs, StringComparer.Ordinal)) return false;
            if (!MetadataStable(requested, before.business.details.items, after.business.details.items)) return false;
            var unchanged = new HashSet<string>(source, StringComparer.Ordinal);
            unchanged.ExceptWith(requested);
            unchanged.UnionWith(target);
            unchanged.UnionWith(oldPlayer.item_refs);
            return MetadataStable(unchanged, before.business.details.items, after.business.details.items);
        }

        private static bool TryGetCompleteContainer(Snapshot s, string name, out ContainerState result)
        {
            result = null;
            var containers = s?.business?.details?.containers;
            if (containers == null || containers.Count != 3) return false;
            foreach (var container in containers)
                if (container != null && container.container == name)
                {
                    result = container;
                    return container.completeness == "complete" && container.item_refs != null &&
                        container.item_refs.Count <= 64 && container.item_refs.Distinct(StringComparer.Ordinal).Count() == container.item_refs.Count;
                }
            return false;
        }

        private static bool MetadataStable(HashSet<string> refs, List<ItemState> before, List<ItemState> after)
        {
            if (before == null || after == null) return false;
            foreach (string reference in refs)
            {
                var left = FindItem(before, reference);
                var right = FindItem(after, reference);
                if (left == null || right == null || left.units != right.units || left.owned != right.owned) return false;
            }
            return true;
        }

        private static ItemState FindItem(List<ItemState> items, string reference)
        {
            if (items == null) return null;
            foreach (var item in items)
                if (item != null && item.item_ref == reference) return item;
            return null;
        }

        private static bool ConfirmationRemoved(Snapshot before, Snapshot after)
        {
            var oldBlockers = before?.business?.details?.ui?.blockers;
            var newBlockers = after?.business?.details?.ui?.blockers;
            bool sameCustomer = before?.business?.customer_ref == after?.business?.customer_ref;
            return sameCustomer && oldBlockers != null && newBlockers != null &&
                oldBlockers.Any(IsConfirmationBlocker) && !newBlockers.Any(IsConfirmationBlocker);
        }

        private static bool IsConfirmationBlocker(string value) => value == "confirmation" ||
            value == "generic_confirmation" || value == "shutter_confirmation" || value == "trade_confirmation";

        private static bool HasConfirmationBlocker(List<string> blockers) =>
            blockers != null && blockers.Any(IsConfirmationBlocker);

        private static bool HasExactlyOneConfirmationBlocker(List<string> blockers) =>
            blockers != null && blockers.Count == 1 && IsConfirmationBlocker(blockers[0]);

        private static bool SameCustomerRef(Snapshot before, Snapshot after) =>
            !string.IsNullOrEmpty(before?.business?.customer_ref) &&
            string.Equals(before.business.customer_ref, after?.business?.customer_ref, StringComparison.Ordinal);

        private static bool Empty(ActionArguments a) => a.customer_ref == null && a.item_refs == null &&
            a.offer_ref == null && a.collection_fingerprint == null && a.option_ref == null &&
            a.dialogue_ref == null && a.mode == null && a.authorization_ref == null &&
            !a.amount.HasValue && !a.max_spend.HasValue;

        private static bool Ref(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (char ch in value)
                if (!((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                    (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == ':' || ch == '-')) return false;
            return true;
        }
    }
}
