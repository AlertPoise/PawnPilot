using System;

namespace PawnPilot
{
    // DTO-only daily lifecycle. Native adapters still prove the exact visible control and
    // current game prerequisites immediately before invoking the ordinary game handler.
    public static class DayActionPolicy
    {
        public static bool Handles(string action) => action == "close_business" || action == "open_end_of_day" ||
            action == "confirm_end_of_day" || action == "close_day_report";

        public static bool ArgumentsValid(string action, ActionArguments a)
        {
            if (!Handles(action) || a == null || a.customer_ref != null || a.item_refs != null || a.offer_ref != null ||
                a.collection_fingerprint != null || a.container != null || a.option_ref != null || a.dialogue_ref != null ||
                a.mode != null || a.authorization_ref != null || a.handoff_ref != null) return false;
            if (action != "confirm_end_of_day") return a.settlement_ref == null && !a.amount.HasValue && !a.max_spend.HasValue;
            return Ref(a.settlement_ref) && a.amount.HasValue && a.max_spend.HasValue &&
                a.max_spend.Value >= Math.Max(0, a.amount.Value);
        }

        // Candidate validation deliberately does not consult legal_actions. The shared business
        // policy checks that binding at admission, allowing a caller-approved spending ceiling.
        public static string Validate(Command command, Snapshot snapshot)
        {
            if (command == null || !ArgumentsValid(command.action, command.arguments)) return "invalid_day_arguments";
            if (snapshot == null || !snapshot.loaded || !snapshot.ready || snapshot.schema_version != Protocol.BusinessSchemaVersion)
                return "not_ready";
            if (BusinessProjection.IsInspectionWaitingIn(snapshot)) return "inspection_handoff";
            if (snapshot.menu_open) return "ui_scope_unverified";
            var day = snapshot.business?.details?.day;
            if (day == null || !BusinessWire.ValidateDay(day) || !day.day_number.HasValue) return "day_state_unavailable";
            if (day.transition_pending) return "day_transition_pending";
            switch (command.action)
            {
                case "close_business":
                    return snapshot.phase == "OPEN" && day.stage == "open" && day.remaining_customers == 0 && day.can_close &&
                        snapshot.business.details.customer?.presence == "absent" && !day.report_open && day.settlement == null
                        ? null : "close_business_precondition";
                case "open_end_of_day":
                    return snapshot.phase == "CLOSED" && day.stage == "closed" && day.can_sleep &&
                        !day.report_open && day.settlement == null ? null : "open_end_of_day_precondition";
                case "confirm_end_of_day":
                    if (snapshot.phase != "CLOSED" || day.stage != "settlement" || !day.can_confirm || day.report_open ||
                        day.settlement == null || day.settlement.settlement_ref != command.arguments.settlement_ref ||
                        day.settlement.total != command.arguments.amount) return "settlement_changed";
                    return command.arguments.amount > 0 && command.arguments.max_spend > snapshot.cash
                        ? "settlement_spend_limit" : null;
                case "close_day_report":
                    return snapshot.phase == "MORNING" && day.stage == "next_morning" && day.report_open && day.settlement == null
                        ? null : "day_report_precondition";
                default:
                    return "unknown_action";
            }
        }

        internal static bool MatchesLegal(Command command, LegalAction legal) => command?.action == "confirm_end_of_day" &&
            legal?.action == command.action && ArgumentsValid(command.action, command.arguments) && ArgumentsValid(legal.action, legal.arguments) &&
            command.arguments.settlement_ref == legal.arguments.settlement_ref && command.arguments.amount == legal.arguments.amount;

        public static bool Postcondition(Command command, Snapshot before, Snapshot after)
        {
            if (command == null || !Handles(command.action) || Validate(command, before) != null || after == null || !after.ready ||
                after.schema_version != Protocol.BusinessSchemaVersion || before.world_epoch != after.world_epoch || before.scene != after.scene ||
                after.revision <= before.revision) return false;
            var old = before.business.details.day;
            var current = after.business?.details?.day;
            if (current == null || !BusinessWire.ValidateDay(current) || !current.day_number.HasValue || current.transition_pending) return false;
            if (command.action == "confirm_end_of_day")
                return (long)current.day_number.Value == (long)old.day_number.Value + 1 && after.phase == "MORNING" &&
                    current.stage == "next_morning" && current.report_open && current.settlement == null &&
                    (long)before.cash - after.cash == command.arguments.amount.Value;
            if (current.day_number != old.day_number || before.cash != after.cash) return false;
            switch (command.action)
            {
                case "close_business":
                    return after.phase == "CLOSED" && current.stage == "closed" && !current.report_open && current.settlement == null;
                case "open_end_of_day":
                    return after.phase == "CLOSED" && current.stage == "settlement" && current.settlement != null && !current.report_open;
                case "close_day_report":
                    return after.phase == "MORNING" && !current.report_open && current.settlement == null &&
                        (current.stage == "morning" || current.stage == "next_morning");
                default:
                    return false;
            }
        }

        public static VerificationDecision Verify(Command command, Snapshot before, Snapshot after)
        {
            if (command == null || !Handles(command.action)) return VerificationDecision.Uncertain("unsupported_day_action");
            if (before == null || after == null) return VerificationDecision.Pending();
            if (before.world_epoch != after.world_epoch || before.scene != after.scene) return VerificationDecision.Uncertain("world_changed_after_day_action");
            if (Validate(command, before) != null) return VerificationDecision.Uncertain("day_preflight_evidence_invalid");
            if (Postcondition(command, before, after)) return VerificationDecision.Succeeded();
            var day = after.business?.details?.day;
            if (!after.ready || day == null || !day.day_number.HasValue || day.transition_pending) return VerificationDecision.Pending();
            if (command.action != "confirm_end_of_day" && (after.cash != before.cash || day.day_number != before.business.details.day.day_number))
                return VerificationDecision.Uncertain("unexpected_day_or_cash_change");
            if (command.action == "confirm_end_of_day" && day.day_number != before.business.details.day.day_number)
                return VerificationDecision.Uncertain("day_settlement_mismatch");
            return VerificationDecision.Pending();
        }

        private static bool Ref(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (char c in value)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_' && c != '.' && c != ':' && c != '-') return false;
            return true;
        }
    }
}
