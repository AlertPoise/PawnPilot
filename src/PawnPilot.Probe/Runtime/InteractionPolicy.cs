using System;
using System.Collections.Generic;
using System.Linq;

namespace PawnPilot
{
    // DTO-only admission and completion policy for the three bounded interaction actions.
    // Native code still binds opaque refs to live Unity objects and supplies the trusted
    // nativeReturned receipt; no DTO reference authorizes a generic click or key event.
    public static class InteractionPolicy
    {
        private static readonly string[] Containers = { "front", "back", "player" };

        public static bool IsGenericAction(string action) => action == "advance_dialogue" ||
            action == "choose_dialogue_option" || action == "dismiss_customer";

        public static bool HasSurfaceBinding(ActionArguments args) => args != null &&
            (args.surface_ref != null || args.control_ref != null);

        public static bool IsGenericCommand(Command command) => command != null && IsGenericAction(command.action) &&
            HasSurfaceBinding(command.arguments);

        public static bool ArgumentsValid(string action, ActionArguments args)
        {
            if (!IsGenericAction(action) || args == null || args.surface_ref == null || args.control_ref == null ||
                !InteractionState.Ref(args.surface_ref) || !InteractionState.Ref(args.control_ref) ||
                !InteractionState.Ref(args.customer_ref)) return false;
            switch (action)
            {
                case "advance_dialogue":
                    return InteractionState.Ref(args.dialogue_ref) && args.dialogue_ref != null &&
                        (args.mode == "skip" || args.mode == "next") && args.option_ref == null &&
                        args.item_refs == null && args.offer_ref == null && args.collection_fingerprint == null &&
                        args.container == null && args.authorization_ref == null && args.handoff_ref == null &&
                        args.settlement_ref == null && !args.amount.HasValue && !args.max_spend.HasValue;
                case "choose_dialogue_option":
                    return InteractionState.Ref(args.dialogue_ref) && args.dialogue_ref != null &&
                        InteractionState.Ref(args.option_ref) && args.option_ref != null && args.mode == null &&
                        args.item_refs == null && args.offer_ref == null && args.collection_fingerprint == null &&
                        args.container == null && args.authorization_ref == null && args.handoff_ref == null &&
                        args.settlement_ref == null && !args.amount.HasValue && !args.max_spend.HasValue;
                case "dismiss_customer":
                    return args.dialogue_ref == null && args.option_ref == null && args.mode == null &&
                        args.item_refs == null && args.offer_ref == null && args.collection_fingerprint == null &&
                        args.container == null && args.authorization_ref == null && args.handoff_ref == null &&
                        args.settlement_ref == null && !args.amount.HasValue && !args.max_spend.HasValue;
                default: return false;
            }
        }

        public static string ValidateCandidate(Snapshot snapshot, LegalAction candidate, bool requireFinalStatus = true)
        {
            if (candidate == null || !IsGenericAction(candidate.action) || !HasSurfaceBinding(candidate.arguments))
                return "interaction_binding_required";
            if (!ArgumentsValid(candidate.action, candidate.arguments)) return "invalid_arguments";
            if (!BaseSnapshotReady(snapshot)) return "not_ready";
            if (snapshot.schema_version != Protocol.InteractionSchemaVersion) return "interaction_schema_required";
            if (snapshot.menu_open) return "ui_scope_unverified";
            if (!string.Equals(snapshot.phase, "OPEN", StringComparison.Ordinal)) return "store_not_open";
            if (CandidateAdmission.DayBlocksOrdinary(snapshot)) return "day_ui_blocked";
            if (BusinessProjection.IsInspectionWaitingIn(snapshot)) return "inspection_handoff";

            var details = snapshot.business.details;
            var customer = details?.customer;
            var surface = snapshot.surface;
            var args = candidate.arguments;
            if (details == null || details.schema_version != 2 || customer == null || customer.presence != "present" ||
                customer.arrived != true || snapshot.business.customer_ref == null ||
                snapshot.business.customer_ref != args.customer_ref) return "customer_changed";
            if (!InteractionState.IsValid(surface) || surface.completeness != "complete" || surface.surface_ref != args.surface_ref ||
                surface.customer_ref != args.customer_ref) return "surface_changed";
            if (requireFinalStatus && surface.status != "awaiting_agent") return "surface_not_actionable";
            if (details.ui == null || details.ui.blockers == null || details.ui.status == "updating" ||
                BusinessActionPolicy.HasActionBlockingEvidence(details.ui.blockers, candidate.action)) return "known_ui_blocker";

            string expectedKind, expectedPhase;
            switch (candidate.action)
            {
                case "advance_dialogue": expectedKind = "dialogue"; expectedPhase = args.mode == "skip" ? "typing" : "waiting_for_advance"; break;
                case "choose_dialogue_option": expectedKind = "dialogue"; expectedPhase = "awaiting_choice"; break;
                case "dismiss_customer": expectedKind = "customer_completion"; expectedPhase = "completed"; break;
                default: return "unknown_action";
            }
            if (surface.kind != expectedKind || surface.phase != expectedPhase) return "surface_phase_changed";
            if (candidate.action != "dismiss_customer" && surface.dialogue_ref != args.dialogue_ref)
                return "dialogue_ref_changed";

            var control = surface.controls.Find(item => item != null && item.control_ref == args.control_ref);
            if (control == null || !control.enabled || control.action != candidate.action ||
                control.mode != args.mode || control.option_ref != args.option_ref) return "control_changed";
            if (control.effect_contract == "unknown" || !ContractMatchesAction(candidate.action, control.effect_contract))
                return "effect_contract_unverified";
            if (requireFinalStatus && !snapshot.legal_actions.Any(item => item != null && item.action == candidate.action &&
                BusinessActionPolicy.SameArguments(item.arguments, args))) return "action_not_legal_in_snapshot";
            return null;
        }

        public static string ValidateCommand(Command command, Snapshot snapshot)
        {
            if (command == null || !IsGenericCommand(command)) return "interaction_binding_required";
            string reason = ValidateCandidate(snapshot,
                new LegalAction { action = command.action, arguments = command.arguments }, requireFinalStatus: true);
            if (reason != null) return reason;
            return snapshot.legal_actions.Any(item => item != null && item.action == command.action &&
                BusinessActionPolicy.SameArguments(item.arguments, command.arguments))
                ? null : "action_not_legal_in_snapshot";
        }

        public static bool ValidateSnapshot(Snapshot snapshot, bool requireFinalStatus)
        {
            if (snapshot == null) return false;
            if (snapshot.schema_version != Protocol.InteractionSchemaVersion)
                return snapshot.surface == null && !snapshot.legal_actions.Any(item => item != null &&
                    HasSurfaceBinding(item.arguments));
            if (!InteractionState.IsValid(snapshot.surface)) return false;
            if (!SurfaceBusinessCoherent(snapshot)) return false;
            if (!snapshot.ready)
                return snapshot.legal_actions.Count == 0 && (!requireFinalStatus || snapshot.surface.status != "awaiting_agent");

            bool inspection = BusinessProjection.IsInspectionWaitingIn(snapshot);
            if (requireFinalStatus)
            {
                if (inspection && snapshot.surface.status != "awaiting_user") return false;
                if (!inspection && snapshot.legal_actions.Count > 0 && snapshot.surface.status != "awaiting_agent") return false;
                if (snapshot.surface.status == "awaiting_agent" && snapshot.legal_actions.Count == 0) return false;
                if (snapshot.surface.status == "idle" && snapshot.surface.phase != "absent") return false;
                if (snapshot.surface.status == "progressing" && snapshot.surface.phase != "typing" && snapshot.surface.phase != "progressing") return false;
            }

            for (int index = 0; index < snapshot.legal_actions.Count; index++)
            {
                var action = snapshot.legal_actions[index];
                if (action == null) continue;
                if (HasSurfaceBinding(action.arguments) &&
                    (snapshot.schema_version != Protocol.InteractionSchemaVersion || !IsGenericAction(action.action) ||
                     ValidateCandidate(snapshot, action, requireFinalStatus) != null)) return false;
                if (IsGenericAction(action.action))
                    for (int earlier = 0; earlier < index; earlier++)
                        if (SameSemantics(action, snapshot.legal_actions[earlier])) return false;
            }
            return true;
        }

        private static bool SurfaceBusinessCoherent(Snapshot snapshot)
        {
            var surface = snapshot.surface;
            var customer = snapshot.business?.details?.customer;
            if (surface.phase == "absent" && surface.kind != "none") return false;
            if (!snapshot.ready) return true;
            if (surface.kind == "none" && customer?.presence == "present") return false;
            if (surface.completeness != "complete") return true;
            if (customer == null || surface.customer_ref != snapshot.business.customer_ref) return false;
            if (surface.kind == "dialogue")
                return customer.presence == "present" && surface.dialogue_ref != null &&
                    surface.dialogue_ref == customer.dialogue_ref;
            if (surface.kind == "customer_completion")
                return customer.presence == "present" && surface.dialogue_ref == null && customer.dialogue_ref == null;
            return true;
        }

        public static VerificationDecision Verify(Command command, Snapshot before, Snapshot after, bool nativeReturned)
        {
            if (!IsGenericCommand(command) || !ArgumentsValid(command.action, command.arguments) ||
                before?.schema_version != Protocol.InteractionSchemaVersion)
                return VerificationDecision.Uncertain("interaction_verifier_input_invalid");

            var result = UnknownResult(command, before, after, nativeReturned);
            var control = FindControl(before.surface, command.arguments.control_ref);
            if (!nativeReturned) return Decision("uncertain", "native_input_not_returned", result);
            if (after == null || !after.ready || after.schema_version != Protocol.InteractionSchemaVersion ||
                after.surface == null || after.surface.completeness != "complete")
                return Decision("pending", "postcondition_pending", result);
            if (after.world_epoch != before.world_epoch || after.scene != before.scene)
                return Decision("uncertain", "world_changed_after_interaction", result);
            if (after.revision <= before.revision) return Decision("pending", "postcondition_pending", result);
            if (control == null || control.effect_contract == "unknown")
                return Decision("uncertain", "effect_contract_unverified", result);
            if (after.cash != before.cash)
            {
                result.cash_delta = SafeDelta(before.cash, after.cash);
                AddIfDeclared(result.observed_effects, control, "cash");
                SetUnverified(result, control);
                return Decision("uncertain", "cash_changed_during_interaction", result);
            }

            if (!TryInventoryDiff(before, after, out var beforeItems, out var afterItems, out var added, out var removed))
                return Decision("pending", "inventory_evidence_incomplete", result);
            result.cash_delta = 0;
            result.added_item_refs = added;
            result.removed_item_refs = removed;
            bool allowedGift = control.effect_contract == "document_gift";
            string inventoryProblem = InventoryProblem(beforeItems, afterItems, added, removed, allowedGift);
            if (inventoryProblem != null)
            {
                if (removed.Count > 0 || added.Count > 0 || InventoryChanged(beforeItems, afterItems))
                    AddIfDeclared(result.observed_effects, control, "inventory_transfer");
                SetUnverified(result, control);
                return Decision("uncertain", inventoryProblem, result);
            }

            bool progressed;
            if (control.effect_contract == "customer_departure")
                progressed = CustomerDeparted(before, after);
            else
                progressed = DialogueProgressed(command, before.surface, after.surface);
            if (!progressed) return Decision("pending", "postcondition_pending", result);

            if (control.effect_contract == "customer_departure") AddIfDeclared(result.observed_effects, control, "customer_departure");
            else
            {
                AddIfDeclared(result.observed_effects, control, "presentation");
                AddIfDeclared(result.observed_effects, control, "dialogue_transition");
                if (control.effect_contract == "document_gift" && added.Count == 1)
                    AddIfDeclared(result.observed_effects, control, "inventory_transfer");
            }
            result.unverified_effects = control.effects.Where(effect => !result.observed_effects.Contains(effect, StringComparer.Ordinal))
                .OrderBy(effect => effect, StringComparer.Ordinal).ToList();
            result.verification = "confirmed";
            return Decision("succeeded", "postcondition_observed", result);
        }

        // The gate calls this independently after the native verifier reports success. It
        // re-derives every DTO-visible delta so a receipt or claimed effect cannot waive it.
        public static bool Postcondition(Command command, Snapshot before, Snapshot after, InteractionResult interactionResult)
        {
            if (interactionResult == null || !InteractionState.IsValid(interactionResult) ||
                interactionResult.input_receipt != "returned" || interactionResult.verification != "confirmed") return false;
            var expected = Verify(command, before, after, nativeReturned: true);
            return expected.outcome == "succeeded" && SameResult(expected.interaction_result, interactionResult);
        }

        public static InteractionResult UnknownResult(Command command, Snapshot before, Snapshot after, bool nativeReturned)
        {
            var control = FindControl(before?.surface, command?.arguments?.control_ref);
            bool sameWorld = before != null && after != null &&
                before.world_epoch == after.world_epoch && before.scene == after.scene;
            var result = new InteractionResult
            {
                input_receipt = nativeReturned ? "returned" : "unknown",
                verification = "unknown",
                effect_contract = control?.effect_contract ?? "unknown",
                observed_effects = new List<string>(),
                unverified_effects = control?.effects == null ? new List<string>() : control.effects.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                cash_delta = sameWorld ? SafeDelta(before.cash, after.cash) : (int?)null,
                added_item_refs = new List<string>(),
                removed_item_refs = new List<string>()
            };
            if (sameWorld && TryInventoryDiff(before, after, out _, out _, out var added, out var removed))
            {
                result.added_item_refs = added;
                result.removed_item_refs = removed;
            }
            return result;
        }

        public static InteractionResult BindTrustedReceipt(InteractionResult result, bool nativeReturned)
        {
            var copy = InteractionState.Clone(result);
            if (copy == null) return null;
            copy.input_receipt = nativeReturned ? "returned" : "unknown";
            if (!nativeReturned) copy.verification = "unknown";
            return copy;
        }

        private static bool BaseSnapshotReady(Snapshot snapshot) => snapshot != null && snapshot.loaded && snapshot.ready &&
            InteractionState.OneOf(snapshot.phase, "OPEN") && snapshot.business != null && snapshot.business.details != null &&
            snapshot.business.details.customer != null && snapshot.business.details.ui != null && snapshot.interaction != null &&
            snapshot.legal_actions != null;

        private static bool ContractMatchesAction(string action, string effectContract) =>
            action == "dismiss_customer" ? effectContract == "customer_departure" :
            effectContract == "presentation" || effectContract == "document_gift";

        private static SurfaceControl FindControl(SurfaceObservation surface, string controlRef) =>
            surface?.controls?.Find(item => item != null && item.control_ref == controlRef);

        private static bool SameSemantics(LegalAction left, LegalAction right)
        {
            if (left == null || right == null || left.action != right.action || !IsGenericAction(left.action)) return false;
            var a = left.arguments; var b = right.arguments;
            return a != null && b != null && a.customer_ref == b.customer_ref && a.dialogue_ref == b.dialogue_ref &&
                a.mode == b.mode && a.option_ref == b.option_ref;
        }

        private static VerificationDecision Decision(string outcome, string reason, InteractionResult result) => new VerificationDecision
        {
            outcome = outcome,
            reason = reason,
            interaction_result = result
        };

        private sealed class ItemFact
        {
            public string item_ref;
            public string container;
            public int units;
            public bool owned;
        }

        private static bool TryInventoryDiff(Snapshot before, Snapshot after, out Dictionary<string, ItemFact> left,
            out Dictionary<string, ItemFact> right, out List<string> added, out List<string> removed)
        {
            left = null; right = null; added = new List<string>(); removed = new List<string>();
            if (!TryInventory(before, out left) || !TryInventory(after, out right)) return false;
            added = right.Keys.Except(left.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            removed = left.Keys.Except(right.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return true;
        }

        private static bool TryInventory(Snapshot snapshot, out Dictionary<string, ItemFact> facts)
        {
            var collected = new Dictionary<string, ItemFact>(StringComparer.Ordinal);
            facts = collected;
            var details = snapshot?.business?.details;
            if (details?.containers == null || details.items == null) return false;
            foreach (var name in Containers)
            {
                var container = details.containers.Find(item => item != null && item.container == name);
                if (container == null || container.completeness != "complete" || container.item_refs == null) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var reference in container.item_refs)
                {
                    if (reference == null || !seen.Add(reference)) return false;
                    var item = details.items.Find(value => value != null && value.item_ref == reference);
                    if (item == null || item.container != name || item.units <= 0 || collected.ContainsKey(reference)) return false;
                    collected.Add(reference, new ItemFact { item_ref = reference, container = name, units = item.units, owned = item.owned });
                }
            }
            // A complete three-container view must account for every reported item.
            return details.items.All(item => item != null && collected.ContainsKey(item.item_ref)) &&
                details.items.Count == collected.Count;
        }

        private static string InventoryProblem(Dictionary<string, ItemFact> before, Dictionary<string, ItemFact> after,
            List<string> added, List<string> removed, bool allowOneFrontGift)
        {
            if (removed.Count > 0) return "unexpected_item_removed";
            if (added.Count > (allowOneFrontGift ? 1 : 0)) return "unexpected_items_added";
            foreach (var pair in before)
            {
                if (!after.TryGetValue(pair.Key, out var current) || current.container != pair.Value.container ||
                    current.units != pair.Value.units || current.owned != pair.Value.owned)
                    return "unexpected_inventory_mutation";
            }
            if (added.Count == 1)
            {
                if (!allowOneFrontGift || !after.TryGetValue(added[0], out var gift) || gift.container != "front" || !gift.owned)
                    return "unexpected_item_added";
            }
            return null;
        }

        private static bool InventoryChanged(Dictionary<string, ItemFact> before, Dictionary<string, ItemFact> after)
        {
            if (before.Count != after.Count) return true;
            foreach (var pair in before)
                if (!after.TryGetValue(pair.Key, out var item) || item.container != pair.Value.container ||
                    item.units != pair.Value.units || item.owned != pair.Value.owned) return true;
            return false;
        }

        private static bool DialogueProgressed(Command command, SurfaceObservation before, SurfaceObservation after)
        {
            if (before == null || after == null || after.completeness != "complete") return false;
            if (before.dialogue_ref != after.dialogue_ref || after.phase == "completed") return true;
            if (command.action == "advance_dialogue" && command.arguments.mode == "skip" &&
                before.phase == "typing" && after.phase != "typing") return true;
            var oldOptions = before.controls.Where(item => item != null && item.action == "choose_dialogue_option")
                .Select(item => item.option_ref).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var newOptions = after.controls.Where(item => item != null && item.action == "choose_dialogue_option")
                .Select(item => item.option_ref).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return !oldOptions.SequenceEqual(newOptions, StringComparer.Ordinal);
        }

        private static bool CustomerDeparted(Snapshot before, Snapshot after)
        {
            string oldRef = before?.business?.customer_ref;
            if (string.IsNullOrEmpty(oldRef)) return false;
            string newRef = after?.business?.customer_ref;
            var customer = after?.business?.details?.customer;
            return newRef == null && customer?.presence == "absent" || newRef != null && newRef != oldRef && customer?.presence == "present";
        }

        private static int? SafeDelta(int before, int after)
        {
            long delta = (long)after - before;
            return delta < int.MinValue || delta > int.MaxValue ? (int?)null : (int)delta;
        }

        private static void AddIfDeclared(List<string> destination, SurfaceControl control, string effect)
        {
            if (control?.effects?.Contains(effect, StringComparer.Ordinal) == true && !destination.Contains(effect, StringComparer.Ordinal))
                destination.Add(effect);
        }

        private static void SetUnverified(InteractionResult result, SurfaceControl control)
        {
            result.unverified_effects = control?.effects == null ? new List<string>() :
                control.effects.Where(effect => !result.observed_effects.Contains(effect, StringComparer.Ordinal))
                    .OrderBy(effect => effect, StringComparer.Ordinal).ToList();
        }

        private static bool SameResult(InteractionResult expected, InteractionResult actual) => expected != null && actual != null &&
            expected.input_receipt == actual.input_receipt && expected.verification == actual.verification &&
            expected.effect_contract == actual.effect_contract && expected.cash_delta == actual.cash_delta &&
            expected.observed_effects.SequenceEqual(actual.observed_effects, StringComparer.Ordinal) &&
            expected.unverified_effects.SequenceEqual(actual.unverified_effects, StringComparer.Ordinal) &&
            expected.added_item_refs.SequenceEqual(actual.added_item_refs, StringComparer.Ordinal) &&
            expected.removed_item_refs.SequenceEqual(actual.removed_item_refs, StringComparer.Ordinal);
    }
}
