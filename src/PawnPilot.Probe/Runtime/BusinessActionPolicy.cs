using System;
using System.Collections.Generic;
using System.Linq;

namespace PawnPilot
{
    // Process-local facts gathered immediately before a native action. Never attach this type to
    // Snapshot, JSON, progress, or a result. Each enabled action has its own explicit local scope.
    internal sealed class NativeActionEvidence
    {
        internal string Action, Reason;
        internal bool Captured, SceneReady, UiCoverageVerified, UiConsistent, NoBlockers;
        internal bool NativeTargetAvailable, NativeEntryVerified, CapabilityScopeVerified;
        internal bool StoreClosed, StoreOpen, CanStartDay, NoLocks, NoCurrentCustomer, CanNextClient;
        internal bool AnyCurrentCustomer, StoreLockReleased, MainDialogueReady, NoCallNextPending;
        internal bool CurrentCustomerMatches, CustomerArrived, OrdinarySeller, CallbackScopeVerified;
        internal bool DialogueMatches, DialogueTyping, WaitingForAdvance, DialogueChoice, NextDialogueExists;
        internal bool OptionVisibleEnabled, ItemSetMatches, TransferScopeVerified, TransferTargetCapacityVerified;
        internal bool ConfirmationOpen, ConfirmationCancelSafe, OnlyTargetBlocker;
        internal string ConfirmationKind;
        internal object NativeTarget = null;
        public bool Dismissable, DismissInterceptClear, NoPendingEndAction, AutoCallNextDisabled;
    }

    // Pure DTO-only action policy. Runtime candidates are descriptive snapshot entries; Invoke
    // always rechecks current native target and preconditions before dispatch.
    public static class BusinessActionPolicy
    {
        // The initial open/receive path is scoped to exact native button targets and live
        // store-state gates. Other registered actions still require their action-specific
        // evidence and are not advertised by the runtime until those scopes pass.
        public static bool IsEnabled(string action) => action == "start_business" || action == "receive_next_customer" ||
            action == "advance_dialogue" || action == "choose_dialogue_option" ||
            action == "prepare_purchase_items" || action == "dismiss_customer" || action == "purchase_visible_offer" ||
            action == "prepare_sale_items" || action == "sell_visible_offer" || action == "sort_inventory_default" || action == "open_trade_panel" || action == "resume_inspection_handoff" || IsBargainAction(action) || DayActionPolicy.Handles(action);
        [Obsolete("Use IsEnabled(action); runtime actions are enabled per action scope.")]
        public static bool ProductionDispatchEnabled => IsEnabled("start_business") || IsEnabled("receive_next_customer");

        private static readonly string[] Registered =
        {
            "start_business", "receive_next_customer", "advance_dialogue", "choose_dialogue_option",
            "prepare_purchase_items", "cancel_confirmation", "refuse_trade", "dismiss_customer", "purchase_visible_offer",
            "prepare_sale_items", "sell_visible_offer", "sort_inventory_default", "open_trade_panel",
            "open_bargain_panel", "close_bargain_panel", "choose_bargain_option", "resume_inspection_handoff",
            "close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report"
        };

        public static bool IsRegistered(string action) => action != null && Array.IndexOf(Registered, action) >= 0;

        public static bool ArgumentsValid(string action, ActionArguments a)
        {
            if (!IsRegistered(action) || a == null) return false;
            if (DayActionPolicy.Handles(action)) return DayActionPolicy.ArgumentsValid(action, a);
            if (a.settlement_ref != null) return false;
            if (action != "resume_inspection_handoff" && a.handoff_ref != null) return false;
            if (action != "sort_inventory_default" && a.container != null) return false;
            switch (action)
            {
                case "resume_inspection_handoff":
                    return Ref(a.handoff_ref) && a.customer_ref == null && a.item_refs == null && a.offer_ref == null &&
                        a.collection_fingerprint == null && a.option_ref == null && a.dialogue_ref == null && a.mode == null &&
                        a.authorization_ref == null && a.container == null && !a.amount.HasValue && !a.max_spend.HasValue;
                case "open_bargain_panel":
                case "close_bargain_panel":
                case "choose_bargain_option":
                    return Ref(a.customer_ref) && (a.mode == "buy" || a.mode == "sell") &&
                        (action == "choose_bargain_option" ? Ref(a.option_ref) : a.option_ref == null) &&
                        a.item_refs == null && a.offer_ref == null && a.collection_fingerprint == null && a.dialogue_ref == null &&
                        a.authorization_ref == null && !a.amount.HasValue && !a.max_spend.HasValue;
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
                case "purchase_visible_offer":
                    return Ref(a.customer_ref) && Ref(a.offer_ref) && Ref(a.collection_fingerprint) &&
                        a.item_refs != null && a.item_refs.Count > 0 && a.item_refs.Count <= 64 &&
                        a.item_refs.All(Ref) && a.item_refs.Distinct(StringComparer.Ordinal).Count() == a.item_refs.Count &&
                        a.mode == "normal" && a.amount.HasValue && a.amount.Value >= 0 &&
                        a.max_spend.HasValue && a.max_spend.Value >= a.amount.Value &&
                        a.dialogue_ref == null && a.option_ref == null && a.authorization_ref == null;
                case "prepare_sale_items":
                    return Ref(a.customer_ref) && a.item_refs != null && a.item_refs.Count == 1 && Ref(a.item_refs[0]) &&
                        Ref(a.collection_fingerprint) && a.offer_ref == null && a.option_ref == null && a.dialogue_ref == null &&
                        a.mode == null && a.authorization_ref == null && a.container == null && !a.amount.HasValue && !a.max_spend.HasValue;
                case "sell_visible_offer":
                    return Ref(a.customer_ref) && Ref(a.offer_ref) && Ref(a.collection_fingerprint) &&
                        a.item_refs != null && a.item_refs.Count == 1 &&
                        a.item_refs.All(Ref) && a.item_refs.Distinct(StringComparer.Ordinal).Count() == a.item_refs.Count &&
                        a.mode == "normal" && a.amount.HasValue && a.amount.Value >= 0 &&
                        a.container == null && a.dialogue_ref == null && a.option_ref == null && a.authorization_ref == null && !a.max_spend.HasValue;
                case "sort_inventory_default":
                    return a.container == "player" && a.customer_ref == null && a.item_refs == null && a.offer_ref == null &&
                        a.collection_fingerprint == null && a.option_ref == null && a.dialogue_ref == null && a.mode == null &&
                        a.authorization_ref == null && !a.amount.HasValue && !a.max_spend.HasValue;
                case "refuse_trade":
                case "dismiss_customer":
                case "open_trade_panel":
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
                a.dialogue_ref != b.dialogue_ref || a.mode != b.mode || a.authorization_ref != b.authorization_ref || a.container != b.container ||
                a.handoff_ref != b.handoff_ref || a.settlement_ref != b.settlement_ref || a.amount != b.amount || a.max_spend != b.max_spend) return false;
            if (a.item_refs == null || b.item_refs == null) return a.item_refs == null && b.item_refs == null;
            return a.item_refs.SequenceEqual(b.item_refs, StringComparer.Ordinal);
        }

        public static string Validate(Command command, Snapshot snapshot)
        {
            string dtoReason = ValidateDto(command, snapshot, requireLegalCandidate: true);
            return dtoReason;
        }

        internal static string ValidateCandidate(Command command, Snapshot snapshot) =>
            ValidateDto(command, snapshot, requireLegalCandidate: false);

        internal static string Validate(Command command, Snapshot snapshot, NativeActionEvidence evidence)
        {
            string reason = ValidateDto(command, snapshot, requireLegalCandidate: true);
            if (reason != null) return reason;
            // These adapters have dedicated native preflight/verification paths. Their contract
            // remains the DTO boundary here; do not require generic NativeActionEvidence routing.
            if (command.action == "prepare_sale_items" || command.action == "sell_visible_offer" || command.action == "sort_inventory_default" || command.action == "resume_inspection_handoff" || IsBargainAction(command.action) || DayActionPolicy.Handles(command.action)) return null;
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
            var state = snapshot.business.details.customer;
            var args = command.arguments;
            if (command.action == "open_trade_panel")
                return evidence.NativeTargetAvailable && evidence.NativeEntryVerified && evidence.CapabilityScopeVerified &&
                    evidence.CurrentCustomerMatches && evidence.CustomerArrived
                    ? null : evidence.Reason ?? "trade_panel_open_precondition";
            if (command.action == "start_business" || command.action == "receive_next_customer")
            {
                if (!evidence.NativeTargetAvailable || !evidence.NativeEntryVerified || !evidence.NoBlockers ||
                    !evidence.CapabilityScopeVerified) return evidence.Reason ?? "native_scope_unverified";
                if (command.action == "start_business")
                    return snapshot.phase == "MORNING" && evidence.StoreClosed && evidence.CanStartDay && evidence.NoLocks &&
                        evidence.NoCurrentCustomer && evidence.NoCallNextPending
                        ? null : "start_business_precondition";
                return snapshot.phase == "OPEN" && evidence.StoreOpen && evidence.NoLocks && evidence.NoCurrentCustomer &&
                    evidence.NoCallNextPending && evidence.CanNextClient
                    ? null : "receive_next_customer_precondition";
            }
            if (command.action == "advance_dialogue" || command.action == "choose_dialogue_option" ||
                command.action == "prepare_purchase_items" || command.action == "dismiss_customer")
            {
                if (!evidence.NativeTargetAvailable || !evidence.NativeEntryVerified || !evidence.CapabilityScopeVerified)
                    return evidence.Reason ?? "native_scope_unverified";
            }
            else if (!evidence.UiCoverageVerified || !evidence.UiConsistent || !evidence.NoBlockers ||
                !evidence.NativeTargetAvailable || !evidence.NativeEntryVerified || !evidence.CapabilityScopeVerified)
                return evidence.Reason ?? "ui_scope_unverified";
            switch (command.action)
            {
                case "advance_dialogue":
                    if (!CustomerMatches(snapshot.business.customer_ref, state, args.customer_ref) || state.dialogue_ref != args.dialogue_ref ||
                        !evidence.CurrentCustomerMatches || !evidence.CustomerArrived || !evidence.OrdinarySeller ||
                        !evidence.DialogueMatches || evidence.DialogueChoice)
                        return "dialogue_scope_or_callback_unverified";
                    if (args.mode == "skip") return evidence.DialogueTyping ? null : "skip_requires_typing";
                    if (args.mode == "next") return !evidence.DialogueTyping && evidence.WaitingForAdvance
                        ? null : "next_requires_manual_advance";
                    return "invalid_arguments";
                case "choose_dialogue_option":
                    return CustomerMatches(snapshot.business.customer_ref, state, args.customer_ref) &&
                        state.dialogue_ref == args.dialogue_ref && state.option_refs != null &&
                        state.option_refs.Contains(args.option_ref) && evidence.CurrentCustomerMatches &&
                        evidence.CustomerArrived && evidence.OrdinarySeller && evidence.DialogueMatches &&
                        evidence.DialogueChoice && evidence.OptionVisibleEnabled
                        ? null : "choice_scope_or_callback_unverified";
                case "prepare_purchase_items":
                    return evidence.CurrentCustomerMatches && CustomerMatches(snapshot.business.customer_ref, state, args.customer_ref) &&
                        evidence.ItemSetMatches && evidence.TransferScopeVerified &&
                        evidence.OrdinarySeller
                        ? null : "prepare_scope_or_capacity_unverified";
                case "cancel_confirmation":
                    return "confirmation_scope_unverified";
                case "refuse_trade":
                    return "refuse_native_path_unsupported";
                case "dismiss_customer":
                    if (!CustomerMatches(snapshot.business.customer_ref, state, args.customer_ref) || !evidence.CurrentCustomerMatches ||
                        !evidence.CustomerArrived || !evidence.StoreOpen || !evidence.NoLocks || state.phase != "trading" ||
                        state.dialogue_ref != null || !evidence.Dismissable || !evidence.DismissInterceptClear)
                        return "dismiss_state_or_callback_unverified";
                    return null;
                default:
                    return "unknown_action";
            }
        }

        private static string ValidateDto(Command command, Snapshot snapshot, bool requireLegalCandidate)
        {
            if (command == null || !IsRegistered(command.action)) return "unknown_action";
            if (!ArgumentsValid(command.action, command.arguments)) return "invalid_arguments";
            if (!IsEnabled(command.action)) return "action_not_enabled";
            if (!BaseSnapshotReady(snapshot)) return "not_ready";
            if (BusinessProjection.IsInspectionWaitingIn(snapshot) && command.action != "resume_inspection_handoff") return "inspection_handoff";
            var observedDay = snapshot.business.details.day;
            if (observedDay != null && (observedDay.transition_pending || observedDay.report_open || observedDay.stage == "settlement") &&
                !DayActionPolicy.Handles(command.action) && command.action != "resume_inspection_handoff") return "day_ui_blocked";
            if (requireLegalCandidate && !HasMatchingLegalAction(command, snapshot)) return "action_not_legal_in_snapshot";
            if (command.action == "resume_inspection_handoff") return BusinessProjection.IsInspectionWaitingIn(snapshot) &&
                snapshot.business.details.inspection_handoff.handoff_ref == command.arguments.handoff_ref ? null : "inspection_handoff_changed";
            if (DayActionPolicy.Handles(command.action)) return DayActionPolicy.Validate(command, snapshot);
            var ui = snapshot.business.details.ui;
            if (snapshot.menu_open) return "ui_scope_unverified";
            bool localNativeAction = IsEnabled(command.action);
            if (command.action == "cancel_confirmation")
            {
                if (!HasExactlyOneConfirmationBlocker(ui.blockers) || ui.status != "blocked" ||
                    snapshot.interaction.consistency != "blocked" || snapshot.interaction.operable)
                    return "confirmation_scope_unverified";
            }
            else if (!localNativeAction && (snapshot.interaction.consistency != "consistent" || !snapshot.interaction.operable ||
                ui.status != "consistent" || ui.coverage != "verified")) return "ui_scope_unverified";
            else if (localNativeAction && HasActionBlockingEvidence(ui.blockers, command.action)) return "known_ui_blocker";
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
                case "open_bargain_panel":
                case "close_bargain_panel":
                case "choose_bargain_option":
                    return BargainPrecondition(command, snapshot) ? null : "bargain_state_changed";
                case "start_business":
                    return snapshot.phase == "MORNING" && customer.presence == "absent"
                        ? null : "store_not_closed_for_business";
                case "receive_next_customer":
                    return snapshot.phase == "OPEN" && customer.presence == "absent"
                        ? null : "customer_already_present_or_store_closed";
                case "open_trade_panel":
                    return CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref) &&
                        customer.arrived == true && (customer.trade_type == "buy" || customer.trade_type == "sell") &&
                        customer.trade_panel == "closed" && customer.dialogue_ref == null &&
                        customer.phase != "dialogue" && customer.is_typing != true && customer.waiting_for_advance != true
                        ? null : "trade_panel_open_precondition";
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
                        args.collection_fingerprint == snapshot.business.details.front_collection_fingerprint &&
                        ExactFrontSelection(snapshot, args.item_refs) ? null : "front_collection_changed";
                case "dismiss_customer":
                case "refuse_trade":
                    return CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref)
                        ? null : "customer_changed";
                case "purchase_visible_offer":
                    return CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref) &&
                        customer.trade_type == "sell" && customer.phase == "trading" && customer.arrived == true &&
                        snapshot.business.offer_ref == args.offer_ref &&
                        snapshot.business.collection_fingerprint == args.collection_fingerprint &&
                        snapshot.business.item_refs.SequenceEqual(args.item_refs, StringComparer.Ordinal) &&
                        snapshot.business.details.quote.status == "complete" && snapshot.business.details.quote.mode == "normal" &&
                        snapshot.business.details.quote.displayed_total == args.amount &&
                        snapshot.business.details.capacity.status == "sufficient" &&
                        snapshot.business.details.eligibility.purchase_enabled && args.max_spend <= snapshot.cash
                        ? null : "purchase_state_changed";
                case "prepare_sale_items":
                    return CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref) &&
                        customer.trade_type == "buy" && customer.phase == "trading" && customer.arrived == true &&
                        ExactPlayerSaleSelection(snapshot, args) ? null : "sale_preparation_state_changed";
                case "sell_visible_offer":
                    return CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref) &&
                        customer.trade_type == "buy" && customer.phase == "trading" && customer.arrived == true &&
                        snapshot.business.offer_ref == args.offer_ref &&
                        snapshot.business.collection_fingerprint == args.collection_fingerprint &&
                        snapshot.business.item_refs.SequenceEqual(args.item_refs, StringComparer.Ordinal) &&
                        snapshot.business.details.quote.status == "complete" && snapshot.business.details.quote.mode == "normal" &&
                        snapshot.business.details.quote.displayed_total == args.amount && ExactOwnedBackSubset(snapshot, args.item_refs)
                        ? null : "sale_offer_state_changed";
                case "sort_inventory_default":
                    return ExactPlayerInventory(snapshot) && customer.dialogue_ref == null
                        ? null : "inventory_sort_state_changed";
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
                !TryGetCompleteContainer(snapshot, "back", out _)) return false;
            return refs.Count == front.item_refs.Count && refs.ToHashSet(StringComparer.Ordinal)
                .SetEquals(front.item_refs);
        }

        private static bool ExactPlayerSaleSelection(Snapshot snapshot, ActionArguments args)
        {
            if (args?.item_refs == null || args.item_refs.Count != 1 ||
                !TryGetCompleteContainer(snapshot, "player", out var player) ||
                !TryGetCompleteContainer(snapshot, "back", out _)) return false;
            var item = FindItem(snapshot.business.details.items, args.item_refs[0]);
            if (item == null || item.container != "player" || !item.owned || !player.item_refs.Contains(item.item_ref)) return false;
            var member = new BusinessCollectionMember { item_ref = item.item_ref, container = "player", units = item.units, owned = true };
            string expected = BusinessCollection.Fingerprint(snapshot.world_epoch, args.customer_ref, "buy",
                new[] { member }, "unknown", "unverified", null);
            return expected != null && expected == args.collection_fingerprint;
        }

        private static bool ExactOwnedBackSubset(Snapshot snapshot, List<string> refs)
        {
            if (refs == null || !TryGetCompleteContainer(snapshot, "back", out var back)) return false;
            foreach (var reference in refs)
            {
                var item = FindItem(snapshot.business.details.items, reference);
                if (item == null || item.container != "back" || !item.owned || !back.item_refs.Contains(reference)) return false;
            }
            return true;
        }

        private static bool ExactPlayerInventory(Snapshot snapshot) =>
            TryGetCompleteContainer(snapshot, "player", out _);

        private static bool BaseSnapshotReady(Snapshot s) => s != null && s.schema_version == Protocol.BusinessSchemaVersion &&
            s.loaded && s.ready && s.business != null && s.business.details != null &&
            s.business.details.customer != null && s.business.details.ui != null &&
            s.interaction != null && s.legal_actions != null;

        private static bool HasMatchingLegalAction(Command c, Snapshot s)
        {
            foreach (var legal in s.legal_actions)
                if (legal != null && legal.action == c.action && SameArguments(legal.arguments, c.arguments)) return true;
                else if (DayActionPolicy.MatchesLegal(c, legal)) return true;
            return false;
        }

        private static bool CustomerMatches(string observedRef, CustomerState state, string customerRef) =>
            state != null && state.presence == "present" && !string.IsNullOrEmpty(customerRef) &&
            string.Equals(observedRef, customerRef, StringComparison.Ordinal) && state.phase != "leaving";

        public static bool Postcondition(string action, Snapshot before, Snapshot after)
        {
            if (action == "sort_inventory_default") return SortInventoryPostcondition(before, after);
            if (before == null || after == null || !before.ready || !after.ready ||
                before.schema_version != Protocol.BusinessSchemaVersion || after.schema_version != Protocol.BusinessSchemaVersion ||
                before.world_epoch != after.world_epoch || before.scene != after.scene || after.revision <= before.revision ||
                before.cash != after.cash || before.business?.details?.customer == null || after.business?.details?.customer == null)
                return false;

            var oldCustomer = before.business.details.customer;
            var newCustomer = after.business.details.customer;
            switch (action)
            {
                case "open_bargain_panel":
                    return SameCustomerRef(before, after) && before.business.details.bargain?.phase == "closed" &&
                        after.business.details.bargain?.phase == "choosing" && after.business.details.bargain.options.Count > 0 &&
                        before.business.details.bargain.direction == after.business.details.bargain.direction;
                case "close_bargain_panel":
                    return SameCustomerRef(before, after) && ActiveBargain(before.business.details.bargain) &&
                        after.business.details.bargain?.phase == "closed" && before.business.details.bargain.direction == after.business.details.bargain.direction;
                case "open_trade_panel":
                    return SameCustomerRef(before, after) && oldCustomer.presence == "present" &&
                        oldCustomer.trade_panel == "closed" && newCustomer.presence == "present" && newCustomer.arrived == true &&
                        (oldCustomer.trade_type == "buy" || oldCustomer.trade_type == "sell") &&
                        newCustomer.trade_type == oldCustomer.trade_type && newCustomer.trade_panel == oldCustomer.trade_type &&
                        newCustomer.phase == "trading" && newCustomer.dialogue_ref == null &&
                        after.business.details.quote?.mode == "normal" && after.business.details.quote.displayed_total.HasValue;
                case "start_business":
                    return before.phase == "MORNING" && after.phase == "OPEN";
                case "receive_next_customer":
                    return oldCustomer.presence == "absent" && newCustomer.presence == "present" &&
                        newCustomer.arrived == true && (newCustomer.phase == "dialogue" || newCustomer.phase == "trading") &&
                        (newCustomer.phase == "trading" || (newCustomer.dialogue_ref != null &&
                         (newCustomer.dialogue_stage == "typing" || newCustomer.dialogue_stage == "waiting" ||
                          newCustomer.dialogue_stage == "choices" || newCustomer.dialogue_stage == "updating")));
                case "advance_dialogue":
                    return oldCustomer.presence == "present" && oldCustomer.dialogue_ref != null &&
                        ((newCustomer.presence == "present" && SameCustomerRef(before, after) &&
                          (oldCustomer.dialogue_ref != newCustomer.dialogue_ref || oldCustomer.dialogue_stage != newCustomer.dialogue_stage)) ||
                         (newCustomer.presence == "absent" && newCustomer.dialogue_ref == null));
                case "choose_dialogue_option":
                    return oldCustomer.presence == "present" && newCustomer.presence == "present" &&
                        SameCustomerRef(before, after) && oldCustomer.dialogue_ref != null &&
                        (oldCustomer.dialogue_ref != newCustomer.dialogue_ref ||
                         oldCustomer.dialogue_stage != newCustomer.dialogue_stage || newCustomer.phase == "trading");
                case "prepare_purchase_items":
                    return false; // requested refs are required; only the Command overload can prove the delta.
                case "prepare_sale_items":
                case "sell_visible_offer":
                    return false; // these deltas are proved by their command-specific overload below.
                case "cancel_confirmation":
                    return ConfirmationRemoved(before, after);
                case "refuse_trade":
                    return false;
                case "dismiss_customer":
                    return oldCustomer.presence == "present" && before.business.customer_ref != null &&
                        (newCustomer.presence == "absent" && after.business.customer_ref == null ||
                         newCustomer.presence == "present" && after.business.customer_ref != null &&
                         after.business.customer_ref != before.business.customer_ref);
                case "close_menu":
                    return before.menu_open && !after.menu_open;
                case "sort_inventory_default":
                    return SortInventoryPostcondition(before, after);
                default:
                    return false;
            }
        }

        public static VerificationDecision Verify(Command command, Snapshot before, Snapshot after)
        {
            if (command == null || !IsRegistered(command.action) && command.action != "close_menu")
                return VerificationDecision.Uncertain("unsupported_verifier_action");
            if (DayActionPolicy.Handles(command.action)) return DayActionPolicy.Verify(command, before, after);
            if (command.action == "resume_inspection_handoff") return Postcondition(command, before, after)
                ? VerificationDecision.Succeeded() : VerificationDecision.Pending();
            if (after == null || !after.ready) return VerificationDecision.Pending();
            if (before == null || before.world_epoch != after.world_epoch || before.scene != after.scene)
                return VerificationDecision.Uncertain("world_changed_after_action");
            if (before.cash != after.cash && command.action != "sell_visible_offer") return VerificationDecision.Uncertain("cash_changed_during_non_economic_action");
            if (command.action == "open_trade_panel" && !SameCustomerRef(before, after))
                return VerificationDecision.Uncertain("customer_changed_after_panel_open");
            if (IsBargainAction(command.action) && !SameCustomerRef(before, after))
                return VerificationDecision.Uncertain("customer_changed_after_bargain_action");
            if (Postcondition(command, before, after)) return VerificationDecision.Succeeded();
            if (command.action == "prepare_purchase_items" && StoreCollectionChanged(before, after))
                return VerificationDecision.Uncertain("prepare_partial_or_unexpected_collection_change");
            if (command.action == "prepare_sale_items" && StoreCollectionChanged(before, after, includePlayer: true))
                return VerificationDecision.Uncertain("sale_prepare_partial_or_unexpected_collection_change");
            return VerificationDecision.Pending();
        }

        private static bool StoreCollectionChanged(Snapshot before, Snapshot after, bool includePlayer = false)
        {
            foreach (string name in includePlayer ? new[] { "front", "back", "player" } : new[] { "front", "back" })
            {
                if (!TryGetCompleteContainer(before, name, out var left) ||
                    !TryGetCompleteContainer(after, name, out var right)) continue;
                if (!left.item_refs.SequenceEqual(right.item_refs, StringComparer.Ordinal)) return true;
            }
            return false;
        }

        public static bool Postcondition(Command command, Snapshot before, Snapshot after)
        {
            if (command == null) return false;
            if (DayActionPolicy.Handles(command.action)) return DayActionPolicy.Postcondition(command, before, after);
            if (command.action == "resume_inspection_handoff")
                return ArgumentsValid(command.action, command.arguments) && before != null && after != null &&
                    before.world_epoch == after.world_epoch && after.revision > before.revision &&
                    BusinessProjection.IsInspectionWaitingIn(before) &&
                    before.business.details.inspection_handoff.handoff_ref == command.arguments.handoff_ref &&
                    after.business?.details?.inspection_handoff?.last_resumed_handoff_ref == command.arguments.handoff_ref;
            if (IsBargainAction(command.action))
            {
                if (!ArgumentsValid(command.action, command.arguments) || !BaselinePostcondition(before, after) ||
                    !BargainPrecondition(command, before) || !SameCustomerRef(before, after)) return false;
                var oldBargain = before.business.details.bargain;
                var newBargain = after.business.details.bargain;
                if (newBargain == null || newBargain.direction != command.arguments.mode) return false;
                if (command.action == "choose_bargain_option")
                    return (after.business.details.customer.trade_type == command.arguments.mode || after.business.details.customer.trade_type == "other") &&
                        newBargain.last_option_ref == command.arguments.option_ref &&
                        newBargain.terms_revision > oldBargain.terms_revision && newBargain.result != "none";
                if (after.business.details.customer.trade_type != command.arguments.mode &&
                    !(command.action == "close_bargain_panel" && before.business.details.customer.trade_type == "other" &&
                      after.business.details.customer.trade_type == "other")) return false;
                return Postcondition(command.action, before, after);
            }
            // The native purchase verifier has already checked retained item identities and
            // their exact source/target membership. This final gate checks the DTO boundary.
            if (command.action == "purchase_visible_offer")
                return before != null && after != null && before.ready && after.ready &&
                    before.world_epoch == after.world_epoch && before.scene == after.scene &&
                    after.revision > before.revision && command.arguments?.amount.HasValue == true &&
                    before.cash - (long)after.cash == command.arguments.amount.Value;
            if (command.action == "sell_visible_offer")
                return before != null && after != null && before.ready && after.ready &&
                    before.world_epoch == after.world_epoch && before.scene == after.scene &&
                    after.revision > before.revision && command.arguments?.amount.HasValue == true &&
                    (long)after.cash - before.cash == command.arguments.amount.Value;
            if (command.action == "prepare_sale_items") return BaselinePostcondition(before, after) && ExactPlayerToBackMove(command.arguments, before, after);
            if (command.action == "sort_inventory_default") return SortInventoryPostcondition(before, after);
            if (command.action != "prepare_purchase_items") return Postcondition(command.action, before, after);
            return BaselinePostcondition(before, after) && ExactFrontToBackMove(command.arguments, before, after);
        }

        private static bool SortInventoryPostcondition(Snapshot before, Snapshot after)
        {
            if (before == null || after == null || !before.ready || !after.ready ||
                before.schema_version != Protocol.BusinessSchemaVersion || after.schema_version != Protocol.BusinessSchemaVersion ||
                before.world_epoch != after.world_epoch || before.scene != after.scene || after.revision < before.revision ||
                before.cash != after.cash || !ExactPlayerInventory(before) || !ExactPlayerInventory(after)) return false;
            var oldItems = before.business.details.items;
            var newItems = after.business.details.items;
            if (oldItems == null || newItems == null || oldItems.Count != newItems.Count) return false;
            foreach (var item in oldItems)
            {
                var next = FindItem(newItems, item.item_ref);
                if (next == null || item.units != next.units || item.owned != next.owned || item.container != next.container) return false;
            }
            return true;
        }

        private static bool ExactPlayerToBackMove(ActionArguments args, Snapshot before, Snapshot after)
        {
            if (args?.item_refs == null || args.item_refs.Count != 1 ||
                !SameCustomerRef(before, after) || !TryGetCompleteContainer(before, "player", out var oldPlayer) ||
                !TryGetCompleteContainer(before, "back", out var oldBack) ||
                !TryGetCompleteContainer(after, "player", out var newPlayer) ||
                !TryGetCompleteContainer(after, "back", out var newBack)) return false;
            string moved = args.item_refs[0];
            var selected = FindItem(before.business.details.items, moved);
            if (selected == null || selected.container != "player" || !selected.owned || !oldPlayer.item_refs.Contains(moved)) return false;
            var expectedPlayer = new HashSet<string>(oldPlayer.item_refs, StringComparer.Ordinal);
            if (!expectedPlayer.Remove(moved)) return false;
            var expectedBack = new HashSet<string>(oldBack.item_refs, StringComparer.Ordinal) { moved };
            if (!expectedPlayer.SetEquals(newPlayer.item_refs) || !expectedBack.SetEquals(newBack.item_refs)) return false;
            var oldItems = before.business.details.items;
            var newItems = after.business.details.items;
            if (oldItems == null || newItems == null || oldItems.Count != newItems.Count) return false;
            foreach (var item in oldItems)
            {
                var next = FindItem(newItems, item.item_ref);
                if (next == null || item.units != next.units || item.owned != next.owned ||
                    next.container != (item.item_ref == moved ? "back" : item.container)) return false;
            }
            return true;
        }

        private static bool BaselinePostcondition(Snapshot before, Snapshot after) =>
            before != null && after != null && before.ready && after.ready &&
            before.schema_version == Protocol.BusinessSchemaVersion && after.schema_version == Protocol.BusinessSchemaVersion &&
            string.Equals(before.world_epoch, after.world_epoch, StringComparison.Ordinal) &&
            string.Equals(before.scene, after.scene, StringComparison.Ordinal) && after.revision > before.revision &&
            before.cash == after.cash && before.business?.details?.customer != null &&
            after.business?.details?.customer != null;

        // Full source-set differential against the direct back-counter target. A partial native
        // placement is unknown; player inventory capacity/completeness is not a precondition.
        private static bool ExactFrontToBackMove(ActionArguments args, Snapshot before, Snapshot after)
        {
            if (args?.item_refs == null || args.collection_fingerprint != before.business.details.front_collection_fingerprint ||
                before.business.details.front_collection_fingerprint == null || !SameCustomerRef(before, after)) return false;
            if (!TryGetCompleteContainer(before, "front", out var oldFront) ||
                !TryGetCompleteContainer(before, "back", out var oldBack) ||
                !TryGetCompleteContainer(after, "front", out var newFront) ||
                !TryGetCompleteContainer(after, "back", out var newBack)) return false;
            var requested = new HashSet<string>(args.item_refs, StringComparer.Ordinal);
            var source = new HashSet<string>(oldFront.item_refs, StringComparer.Ordinal);
            var target = new HashSet<string>(oldBack.item_refs, StringComparer.Ordinal);
            if (!requested.SetEquals(source)) return false;
            var expectedFront = new HashSet<string>(source, StringComparer.Ordinal);
            expectedFront.ExceptWith(requested);
            var expectedBack = new HashSet<string>(target, StringComparer.Ordinal);
            expectedBack.UnionWith(requested);
            if (!expectedFront.SetEquals(newFront.item_refs) || !expectedBack.SetEquals(newBack.item_refs)) return false;
            if (!MetadataStable(requested, before.business.details.items, after.business.details.items)) return false;
            var unchanged = new HashSet<string>(source, StringComparer.Ordinal);
            unchanged.ExceptWith(requested);
            unchanged.UnionWith(target);
            return MetadataStable(unchanged, before.business.details.items, after.business.details.items);
        }

        private static bool IsOwned(List<ItemState> items, string reference)
        {
            var item = FindItem(items, reference);
            return item == null || item.owned;
        }

        private static bool TryGetCompleteContainer(Snapshot s, string name, out ContainerState result)
        {
            result = null;
            var containers = s?.business?.details?.containers;
            if (containers == null || containers.Count < 2 || containers.Count > 3) return false;
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

        private static bool HasActionBlockingEvidence(List<string> blockers, string action)
        {
            if (blockers == null) return true;
            foreach (var blocker in blockers)
            {
                if (blocker == "global_ui_stack_unobservable" || blocker == "global_raycast_order_unobservable" ||
                    blocker == "quote_rebuild_completion_unobservable" || blocker == "tutorial_group_active_unverified" ||
                    blocker == "control_hints_active_unverified") continue;
                if ((action == "advance_dialogue" || action == "choose_dialogue_option") && blocker == "dialogue_active") continue;
                if ((action == "prepare_purchase_items" || action == "dismiss_customer") && blocker == "dialogue_active") continue;
                if (IsBargainAction(action) && blocker == "bargain_panel") continue;
                if (action == "close_bargain_panel" && blocker == "dialogue_active") continue;
                return true;
            }
            return false;
        }

        private static bool SameCustomerRef(Snapshot before, Snapshot after) =>
            !string.IsNullOrEmpty(before?.business?.customer_ref) &&
            string.Equals(before.business.customer_ref, after?.business?.customer_ref, StringComparison.Ordinal);

        private static bool IsBargainAction(string action) => action == "open_bargain_panel" || action == "close_bargain_panel" || action == "choose_bargain_option";
        private static bool ActiveBargain(BargainState b) => b != null && (b.phase == "choosing" || b.phase == "responding");
        private static bool BargainPrecondition(Command command, Snapshot snapshot)
        {
            var customer = snapshot?.business?.details?.customer;
            var b = snapshot?.business?.details?.bargain;
            var args = command?.arguments;
            bool closeAcknowledgedDeparture = command?.action == "close_bargain_panel" && b?.phase == "responding" &&
                customer?.trade_type == "other" && b.result == "responding" && b.last_option_ref != null && b.terms_revision > 0;
            if (args == null || b == null || !BusinessWire.ValidateBargain(b) ||
                snapshot.business.details.ui?.status == "updating" || snapshot.business.details.ui?.blockers == null ||
                (snapshot.business.details.ui.status == "blocked" && snapshot.business.details.ui.blockers.Count == 0) ||
                !(CustomerMatches(snapshot.business.customer_ref, customer, args.customer_ref) || (closeAcknowledgedDeparture &&
                    customer.presence == "present" && snapshot.business.customer_ref == args.customer_ref)) || customer.arrived != true ||
                (args.mode != "buy" && args.mode != "sell") || (customer.trade_type != args.mode && !closeAcknowledgedDeparture) || b.direction != args.mode) return false;
            if (command.action == "close_bargain_panel") return ActiveBargain(b);
            bool noDialogue = customer.dialogue_ref == null && customer.phase != "dialogue" && customer.is_typing != true &&
                customer.waiting_for_advance != true && (customer.dialogue_stage == "absent" || customer.dialogue_stage == "unknown");
            if (!b.available || !noDialogue) return false;
            if (command.action == "open_bargain_panel") return b.phase == "closed";
            if (b.phase != "choosing") return false;
            var quote = snapshot.business.details.quote;
            if (quote?.status != "complete" || quote.mode != "normal" || !quote.displayed_total.HasValue ||
                string.IsNullOrEmpty(snapshot.business.offer_ref) || snapshot.business.item_refs == null || snapshot.business.item_refs.Count == 0) return false;
            foreach (var option in b.options)
                if (option.option_ref == args.option_ref) return option.enabled && !option.selected;
            return false;
        }

        private static bool Empty(ActionArguments a) => a.customer_ref == null && a.item_refs == null &&
            a.offer_ref == null && a.collection_fingerprint == null && a.option_ref == null &&
            a.dialogue_ref == null && a.mode == null && a.authorization_ref == null && a.container == null &&
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
