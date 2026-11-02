using System;
using System.Collections.Generic;
using System.Linq;

namespace PawnPilot
{
    // Production publication boundary, separate from historical wire validation. Native
    // adapters must still prove their own scope; DTO admission cannot grant native authority.
    public static class CandidateAdmission
    {
        public static bool DayBlocksOrdinary(Snapshot snapshot)
        {
            var day = snapshot?.business?.details?.day;
            return day != null && (day.transition_pending || day.report_open || day.stage == "settlement");
        }

        public static string Validate(Snapshot snapshot, LegalAction candidate)
        {
            if (candidate == null || candidate.arguments == null) return "invalid_candidate";
            if (candidate.action == "close_menu")
                return snapshot?.ready == true && snapshot.loaded && snapshot.menu_open && snapshot.can_close_menu &&
                    !BusinessProjection.IsInspectionWaitingIn(snapshot) && !DayBlocksOrdinary(snapshot) &&
                    BusinessActionPolicy.SameArguments(candidate.arguments, new ActionArguments())
                    ? null : "close_menu_candidate_blocked";
            var command = new Command { schema_version = Protocol.BusinessSchemaVersion,
                action = candidate.action, arguments = candidate.arguments };
            string reason = BusinessActionPolicy.ValidateCandidate(command, snapshot);
            if (reason != null) return reason;
            var d = snapshot.business.details;
            var c = d.customer;
            // Match the broker's schema-2 publication checks without rewriting old records.
            if (d.schema_version != 2) return "candidate_business_schema_unsupported";
            if (candidate.action == "receive_next_customer" && c.phase != "absent") return "customer_still_leaving";
            if (candidate.action == "advance_dialogue" && (candidate.arguments.mode == "skip"
                ? c.is_typing != true : c.is_typing == true || c.waiting_for_advance != true)) return "dialogue_state_changed";
            if (candidate.action == "prepare_purchase_items" &&
                !candidate.arguments.item_refs.SequenceEqual(d.containers.Find(x => x.container == "front").item_refs))
                return "front_collection_order_changed";
            if (candidate.action == "prepare_sale_items" &&
                (d.containers.Find(x => x.container == "back").item_refs.Count != 0 ||
                 d.quote.status != "unverified" || d.quote.mode != "unknown" || d.quote.displayed_total.HasValue))
                return "sale_preparation_state_changed";
            if ((candidate.action == "sort_inventory_default" || candidate.action == "sell_visible_offer") &&
                (d.ui.status == "blocked" || d.ui.status == "updating")) return "ui_scope_unverified";
            if (candidate.action == "sort_inventory_default" &&
                (c.presence != "absent" || c.phase != "absent" || c.is_typing == true || c.waiting_for_advance == true ||
                 (c.dialogue_stage != "absent" && c.dialogue_stage != "unknown") ||
                 (snapshot.phase != "MORNING" && snapshot.phase != "OPEN"))) return "inventory_sort_state_changed";
            if (candidate.action == "confirm_end_of_day" && candidate.arguments.max_spend != Math.Max(0, candidate.arguments.amount.Value))
                return "settlement_candidate_ceiling_invalid";
            return null;
        }

        public static bool TryAdd(Snapshot snapshot, List<LegalAction> destination, LegalAction candidate)
        {
            string reason = Validate(snapshot, candidate);
            if (reason == null) { destination.Add(candidate); return true; }
            // Preserve why a native-capable action was not published. Unexpected malformed
            // candidates are programming errors, not normal transient admission failures.
            if (candidate == null || !BusinessActionPolicy.ArgumentsValid(candidate.action, candidate.arguments))
                throw new InvalidOperationException("invalid_native_candidate");
            var descriptions = snapshot.business.details.candidates;
            var description = descriptions.Find(x => x.action == candidate.action);
            if (description == null)
            {
                if (descriptions.Count >= 64) throw new InvalidOperationException("candidate_diagnostic_limit");
                description = new BusinessCandidate { action = candidate.action };
                descriptions.Add(description);
            }
            description.status = "blocked";
            description.reason = reason;
            return false;
        }

        // Only called after the native purchase preflight proved quote/capacity. These are
        // publication facts, not a substitute for that preflight or an execution permission.
        public static bool TryAddVerifiedPurchase(Snapshot snapshot, LegalAction candidate)
        {
            if (candidate?.action != "purchase_visible_offer") throw new InvalidOperationException("invalid_purchase_candidate");
            var d = snapshot.business.details;
            d.capacity.status = "sufficient";
            d.capacity.reason = "native_grid_placement_verified";
            d.capacity.scope = "current_normal_purchase";
            d.eligibility.purchase_enabled = true;
            d.eligibility.missing.Clear();
            if (TryAdd(snapshot, snapshot.legal_actions, candidate)) return true;
            string reason = Validate(snapshot, candidate);
            d.eligibility.purchase_enabled = false;
            d.eligibility.missing.Add(reason ?? "purchase_candidate_blocked");
            return false;
        }

        // Call after purchase facts, inspection gating and final revision have settled. Never
        // repair/filter an inconsistent assembled snapshot: the existing driver fails closed.
        public static void EnsureValid(Snapshot snapshot)
        {
            if (!ActionGate.IsSnapshotValid(snapshot)) throw new InvalidOperationException("invalid_publication_snapshot");
            foreach (var candidate in snapshot.legal_actions)
                if (Validate(snapshot, candidate) != null) throw new InvalidOperationException("invalid_publication_candidate");
            if (snapshot.business.details.eligibility.purchase_enabled !=
                snapshot.legal_actions.Any(x => x.action == "purchase_visible_offer"))
                throw new InvalidOperationException("purchase_publication_mismatch");
        }
    }
}
