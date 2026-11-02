using System;
using System.Collections.Generic;
using System.Linq;

namespace PawnPilot
{
    [Serializable]
    public sealed class SurfaceObservation
    {
        public int schema_version = 1;
        public string surface_ref;
        public string kind = "unknown";
        public string phase = "unknown";
        public string status = "unknown";
        public string completeness = "unavailable";
        public string reason = "surface_unavailable";
        public string text;
        public string customer_ref;
        public string dialogue_ref;
        public List<SurfaceControl> controls = new List<SurfaceControl>();
    }

    [Serializable]
    public sealed class SurfaceControl
    {
        public string control_ref;
        public string action;
        public string mode;
        public string option_ref;
        public string text;
        public bool enabled;
        public string effect_contract = "unknown";
        public List<string> effects = new List<string>();
        public string reason = "control_unavailable";
    }

    [Serializable]
    public sealed class InteractionResult
    {
        public string input_receipt;
        public string verification;
        public string effect_contract;
        public List<string> observed_effects = new List<string>();
        public List<string> unverified_effects = new List<string>();
        public int? cash_delta;
        public List<string> added_item_refs = new List<string>();
        public List<string> removed_item_refs = new List<string>();
    }

    public sealed class DialogueFlowSignals
    {
        public bool Ready;
        public bool HasCustomer;
        public bool Arrived;
        public bool HasCurrentDialogue;
        public bool IsTyping;
        public bool WaitingForAdvance;
        public bool AutoAdvancePending;
        public bool EndPending;
        public bool DisplayPending;
        public int ScheduledEndActions;
        public bool HasVisibleChoices;
        public bool ChoicesComplete;
        public bool HasChoiceDefinition;
        public bool VisibleDialogue;
        public bool OtherSurface;
        public bool AboutToStart;
        public bool CompletedStable;
    }

    public sealed class DialogueLifecycleState
    {
        public string phase;
        public string status;
        public string reason;
        public bool evidence_complete;
    }

    public static class InteractionState
    {
        private static readonly HashSet<string> EffectNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "presentation", "dialogue_transition", "story_state", "inventory_transfer", "cash",
            "reputation", "customer_departure", "day_transition", "external_scope"
        };

        public static SurfaceObservation Clone(SurfaceObservation value)
        {
            if (value == null) return null;
            return new SurfaceObservation
            {
                schema_version = value.schema_version,
                surface_ref = value.surface_ref,
                kind = value.kind,
                phase = value.phase,
                status = value.status,
                completeness = value.completeness,
                reason = value.reason,
                text = value.text,
                customer_ref = value.customer_ref,
                dialogue_ref = value.dialogue_ref,
                controls = value.controls == null ? null : value.controls.Select(Clone).ToList()
            };
        }

        public static SurfaceControl Clone(SurfaceControl value)
        {
            if (value == null) return null;
            return new SurfaceControl
            {
                control_ref = value.control_ref,
                action = value.action,
                mode = value.mode,
                option_ref = value.option_ref,
                text = value.text,
                enabled = value.enabled,
                effect_contract = value.effect_contract,
                effects = value.effects == null ? null : new List<string>(value.effects),
                reason = value.reason
            };
        }

        public static InteractionResult Clone(InteractionResult value)
        {
            if (value == null) return null;
            return new InteractionResult
            {
                input_receipt = value.input_receipt,
                verification = value.verification,
                effect_contract = value.effect_contract,
                observed_effects = value.observed_effects == null ? null : new List<string>(value.observed_effects),
                unverified_effects = value.unverified_effects == null ? null : new List<string>(value.unverified_effects),
                cash_delta = value.cash_delta,
                added_item_refs = value.added_item_refs == null ? null : new List<string>(value.added_item_refs),
                removed_item_refs = value.removed_item_refs == null ? null : new List<string>(value.removed_item_refs)
            };
        }

        public static bool IsValid(SurfaceObservation value)
        {
            if (value == null || value.schema_version != 1 || !OneOf(value.kind, "none", "dialogue", "customer_completion", "native_panel", "unknown") ||
                !OneOf(value.phase, "absent", "typing", "waiting_for_advance", "awaiting_choice", "completed", "progressing", "unsupported", "unknown") ||
                !OneOf(value.status, "idle", "progressing", "awaiting_agent", "awaiting_user", "unsupported", "unknown") ||
                !OneOf(value.completeness, "complete", "partial", "unavailable") || !Text(value.reason, 256, nullable: false) ||
                !Text(value.text, 256, nullable: true) || !Ref(value.surface_ref) || !Ref(value.customer_ref) || !Ref(value.dialogue_ref) ||
                value.controls == null || value.controls.Count > 16) return false;
            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var control in value.controls)
                if (!IsValid(control) || !refs.Add(control.control_ref)) return false;
            return true;
        }

        public static bool IsValid(SurfaceControl value)
        {
            if (value == null || !Ref(value.control_ref) || value.control_ref == null ||
                !OneOf(value.action, "advance_dialogue", "choose_dialogue_option", "dismiss_customer") ||
                !OneOf(value.effect_contract, "presentation", "document_gift", "customer_departure", "unknown") ||
                !Text(value.reason, 256, nullable: false) || !Text(value.text, 256, nullable: true) ||
                !Ref(value.option_ref) || (value.mode != null && !OneOf(value.mode, "skip", "next")) ||
                value.effects == null || value.effects.Count > EffectNames.Count) return false;
            if (value.action == "advance_dialogue")
            {
                if (value.mode == null || value.option_ref != null) return false;
            }
            else if (value.action == "choose_dialogue_option")
            {
                if (value.mode != null || value.option_ref == null) return false;
            }
            else if (value.mode != null || value.option_ref != null) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var effect in value.effects)
                if (!EffectNames.Contains(effect) || !seen.Add(effect)) return false;
            return true;
        }

        public static bool IsValid(InteractionResult value)
        {
            if (value == null || !OneOf(value.input_receipt, "not_invoked", "returned", "unknown") ||
                !OneOf(value.verification, "confirmed", "unknown") ||
                !OneOf(value.effect_contract, "presentation", "document_gift", "customer_departure", "unknown") ||
                !UniqueEffects(value.observed_effects) || !UniqueEffects(value.unverified_effects) ||
                value.added_item_refs == null || value.added_item_refs.Count > 64 ||
                value.removed_item_refs == null || value.removed_item_refs.Count > 64 ||
                !UniqueRefs(value.added_item_refs) || !UniqueRefs(value.removed_item_refs)) return false;
            if (value.observed_effects.Intersect(value.unverified_effects, StringComparer.Ordinal).Any()) return false;
            if (value.verification == "confirmed" && value.input_receipt != "returned") return false;
            if (value.input_receipt == "not_invoked" && value.verification == "confirmed") return false;
            return true;
        }

        public static SurfaceObservation Unavailable(string reason = "surface_unavailable") => new SurfaceObservation
        {
            schema_version = 1, kind = "unknown", phase = "unknown", status = "unknown",
            completeness = "unavailable", reason = string.IsNullOrEmpty(reason) ? "surface_unavailable" : reason,
            controls = new List<SurfaceControl>()
        };

        internal static bool Ref(string value)
        {
            if (value == null) return true;
            if (value.Length < 1 || value.Length > 128) return false;
            foreach (char c in value)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') &&
                    c != '_' && c != '.' && c != ':' && c != '-') return false;
            return true;
        }

        internal static bool Text(string value, int max, bool nullable) =>
            value == null ? nullable : value.Length <= max && (nullable || value.Length > 0);

        internal static bool OneOf(string value, params string[] values) =>
            value != null && Array.IndexOf(values, value) >= 0;

        private static bool UniqueEffects(List<string> values)
        {
            if (values == null || values.Count > EffectNames.Count) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
                if (!EffectNames.Contains(value) || !seen.Add(value)) return false;
            return true;
        }

        private static bool UniqueRefs(List<string> values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
                if (value == null || !Ref(value) || !seen.Add(value)) return false;
            return true;
        }
    }

    public static class DialogueLifecycle
    {
        public static DialogueLifecycleState Evaluate(DialogueFlowSignals signals)
        {
            if (signals == null || !signals.Ready || signals.ScheduledEndActions < 0)
                return State("unknown", "unknown", "dialogue_state_unproven", false);

            bool pendingExceptEnd = signals.AutoAdvancePending || signals.DisplayPending ||
                signals.ScheduledEndActions > 0 || signals.AboutToStart;
            bool anyPending = pendingExceptEnd || signals.EndPending;
            if (!signals.HasCustomer)
                return anyPending
                    ? State("progressing", "progressing", "dialogue_transition_pending", false)
                    : State("absent", "idle", "no_current_customer", true);
            if (signals.OtherSurface)
                return State("unsupported", "unsupported", "other_interaction_surface", false);
            if (!signals.Arrived || signals.AboutToStart)
                return State("progressing", "progressing", "customer_arriving", false);

            // A visible typing line remains progressing even if the same active line has a
            // display coroutine. That coroutine count alone is not evidence of a new event.
            if (signals.HasCurrentDialogue && signals.IsTyping && signals.VisibleDialogue)
                return State("typing", "progressing", "dialogue_typing", true);

            // Some native manual-advance handlers set isEndPending while the advance control is
            // still actionable; do not let that flag mask a verified wait state.
            if (signals.HasCurrentDialogue && signals.WaitingForAdvance && signals.VisibleDialogue &&
                !signals.AutoAdvancePending && !signals.DisplayPending && signals.ScheduledEndActions == 0)
                return State("waiting_for_advance", "awaiting_agent", "manual_advance_available", true);

            if (signals.HasCurrentDialogue && signals.HasVisibleChoices && signals.ChoicesComplete &&
                signals.HasChoiceDefinition && signals.VisibleDialogue)
            {
                if (pendingExceptEnd || signals.EndPending)
                    return State("progressing", "progressing", "choice_surface_conflicts_with_pending_transition", false);
                return State("awaiting_choice", "awaiting_agent", "complete_visible_choices", true);
            }
            if (signals.HasVisibleChoices && (!signals.ChoicesComplete || !signals.HasChoiceDefinition))
            {
                if (anyPending) return State("progressing", "progressing", "choice_surface_incomplete_pending", false);
                return signals.HasChoiceDefinition
                    ? State("unknown", "unknown", "choice_surface_incomplete_without_transition", false)
                    : State("unsupported", "unsupported", "choice_definition_unavailable", false);
            }

            if (anyPending)
                return State("progressing", "progressing", "dialogue_transition_pending", false);

            if (!signals.HasCurrentDialogue && signals.CompletedStable && !signals.HasVisibleChoices && !signals.VisibleDialogue)
                return State("completed", "awaiting_agent", "dialogue_completed_stable", true);

            if (signals.HasCurrentDialogue || signals.VisibleDialogue)
                return State("unknown", "unknown", "dialogue_state_unproven", false);
            return State("unknown", "unknown", "dialogue_state_unproven", false);
        }

        private static DialogueLifecycleState State(string phase, string status, string reason, bool complete) =>
            new DialogueLifecycleState { phase = phase, status = status, reason = reason, evidence_complete = complete };
    }
}
