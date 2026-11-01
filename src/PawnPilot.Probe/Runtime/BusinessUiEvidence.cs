using System;
using System.Collections.Generic;

namespace PawnPilot
{
    // Conservative, pure projection of known modal/input observations. This never
    // declares consistency because public roots do not cover the complete UI stack.
    public static class BusinessUiEvidence
    {
        private static readonly string[] UnobservableEvidence =
        {
            "global_ui_stack_unobservable",
            "global_raycast_order_unobservable",
            "quote_rebuild_completion_unobservable"
        };
        private static readonly HashSet<string> KnownRootBlockers = new HashSet<string>(StringComparer.Ordinal)
        {
            "escape_menu", "options", "confirmation", "shutter_confirmation",
            "screen_transition", "store_power_off", "generic_confirmation",
            "bargain_panel", "trade_confirmation", "dialogue_active", "ui_window_drag_active",
            "item_drag_active", "item_selection_active", "item_group_drag_active", "item_equip_selection_active"
        };
        private static readonly HashSet<string> UnverifiedFindings = new HashSet<string>(StringComparer.Ordinal)
        {
            "tutorial_group_active_unverified", "background_blocker_active_unverified",
            "control_hints_active_unverified", "darkness_root_power_mismatch"
        };

        public static BusinessUiState Evaluate(IEnumerable<string> knownBlockers, bool readFailed = false,
            IEnumerable<string> unverifiedFindings = null)
        {
            var result = new BusinessUiState
            {
                status = "unverified",
                coverage = "known_roots_only",
                blockers = new List<string>()
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var actual = new List<string>();
            var actualSeen = new HashSet<string>(StringComparer.Ordinal);
            bool invalidKnown = false;
            bool knownOverflow = false;
            int inspected = 0;
            if (knownBlockers != null)
            {
                foreach (string blocker in knownBlockers)
                {
                    if (++inspected > 32)
                    {
                        knownOverflow = true;
                        break;
                    }
                    if (!IsSafeId(blocker) || !KnownRootBlockers.Contains(blocker))
                    {
                        invalidKnown = true;
                        continue;
                    }
                    Add(actual, actualSeen, blocker);
                }
            }
            int actualLimit = Math.Min(7, actual.Count);
            for (int i = 0; i < actualLimit; i++) Add(result.blockers, seen, actual[i]);
            if (actual.Count > actualLimit) knownOverflow = true;
            if (invalidKnown) Add(result.blockers, seen, "invalid_known_ui_evidence");
            if (knownOverflow) Add(result.blockers, seen, "ui_evidence_limit_exceeded");

            bool findingOverflow = false;
            bool invalidFinding = false;
            inspected = 0;
            if (unverifiedFindings != null)
            {
                foreach (string finding in unverifiedFindings)
                {
                    if (++inspected > 16)
                    {
                        findingOverflow = true;
                        break;
                    }
                    if (!IsSafeId(finding) || !UnverifiedFindings.Contains(finding))
                    {
                        invalidFinding = true;
                        continue;
                    }
                    if (!seen.Contains(finding) && result.blockers.Count < 12) Add(result.blockers, seen, finding);
                    else if (!seen.Contains(finding)) findingOverflow = true;
                }
            }
            if (invalidFinding && result.blockers.Count < 12) Add(result.blockers, seen, "invalid_unverified_ui_evidence");
            if (findingOverflow && result.blockers.Count < 12) Add(result.blockers, seen, "ui_evidence_limit_exceeded");
            if (readFailed) Add(result.blockers, seen, "ui_reader_failed");
            for (int i = 0; i < UnobservableEvidence.Length; i++)
                Add(result.blockers, seen, UnobservableEvidence[i]);

            foreach (string blocker in result.blockers)
            {
                if (KnownRootBlockers.Contains(blocker))
                {
                    result.status = "blocked";
                    break;
                }
            }
            return result;
        }

        private static bool IsSafeId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_') return false;
            }
            return true;
        }

        private static void Add(List<string> values, HashSet<string> seen, string value)
        {
            if (seen.Add(value)) values.Add(value);
        }
    }
}
