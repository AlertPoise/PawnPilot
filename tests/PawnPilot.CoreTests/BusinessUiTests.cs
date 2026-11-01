using System;
using System.Linq;
using PawnPilot;

public static class BusinessUiTests
{
    public static void Run(Action<string, bool> check)
    {
        var clearKnownRoots = BusinessUiEvidence.Evaluate(Array.Empty<string>());
        check("business_ui_without_known_blocker_stays_unverified",
            clearKnownRoots.status == "unverified" && clearKnownRoots.coverage == "known_roots_only" &&
            clearKnownRoots.blockers.Contains("global_ui_stack_unobservable") &&
            clearKnownRoots.blockers.Contains("global_raycast_order_unobservable") &&
            clearKnownRoots.blockers.Contains("quote_rebuild_completion_unobservable"));

        var visibleModal = BusinessUiEvidence.Evaluate(new[] { "generic_confirmation" });
        check("business_ui_known_modal_is_blocked",
            visibleModal.status == "blocked" && visibleModal.blockers.Contains("generic_confirmation") &&
            visibleModal.coverage == "known_roots_only");

        var multiple = BusinessUiEvidence.Evaluate(new[] { "dialogue_active", "item_drag_active", "dialogue_active" });
        check("business_ui_collects_deduplicated_known_blockers",
            multiple.status == "blocked" && multiple.blockers.Count(s => s == "dialogue_active") == 1 &&
            multiple.blockers.Contains("item_drag_active"));

        var failed = BusinessUiEvidence.Evaluate(Array.Empty<string>(), readFailed: true);
        check("business_ui_reader_failure_is_unverified_without_private_error_text",
            failed.status == "unverified" && failed.blockers.Contains("ui_reader_failed") &&
            !failed.blockers.Any(s => s.Contains("Exception", StringComparison.OrdinalIgnoreCase)));

        var uncertainRoot = BusinessUiEvidence.Evaluate(Array.Empty<string>(),
            unverifiedFindings: new[] { "tutorial_group_active_unverified", "background_blocker_active_unverified" });
        check("business_ui_ambiguous_roots_remain_unverified",
            uncertainRoot.status == "unverified" &&
            uncertainRoot.blockers.Contains("tutorial_group_active_unverified") &&
            uncertainRoot.blockers.Contains("background_blocker_active_unverified"));

        var invalid = BusinessUiEvidence.Evaluate(new[] { "generic_confirmation", "raw user supplied text" });
        check("business_ui_rejects_non_allowlisted_blocker_ids",
            invalid.status == "blocked" && invalid.blockers.Contains("generic_confirmation") &&
            invalid.blockers.Contains("invalid_known_ui_evidence") && !invalid.blockers.Contains("raw user supplied text"));

        var unknown = BusinessUiEvidence.Evaluate(new[] { "no_blocker_seen" });
        check("business_ui_unknown_root_claim_does_not_become_blocked_or_consistent",
            unknown.status == "unverified" && unknown.coverage == "known_roots_only" &&
            unknown.blockers.Contains("invalid_known_ui_evidence"));

        var tooMany = BusinessUiEvidence.Evaluate(Enumerable.Repeat("options", 40));
        check("business_ui_known_evidence_is_bounded",
            tooMany.status == "blocked" && tooMany.blockers.Contains("ui_evidence_limit_exceeded") &&
            tooMany.blockers.Count < 40);

        var first = BusinessUiEvidence.Evaluate(new[] { "trade_confirmation" });
        var second = BusinessUiEvidence.Evaluate(Array.Empty<string>());
        first.blockers.Clear();
        check("business_ui_evidence_instances_do_not_share_mutable_lists",
            second.blockers.Count == 3 && second.status == "unverified");

        var malformed = BusinessUiEvidence.Evaluate(new[] { null, "UpperCase", new string('a', 65) });
        check("business_ui_malformed_evidence_collapses_to_fixed_reason",
            malformed.status == "unverified" && malformed.blockers.Contains("invalid_known_ui_evidence") &&
            !malformed.blockers.Any(s => s == null || s.Length > 64 || s == "UpperCase"));

        var invalidFinding = BusinessUiEvidence.Evaluate(Array.Empty<string>(),
            unverifiedFindings: new[] { "raw text from the game" });
        check("business_ui_unverified_findings_are_allowlisted",
            invalidFinding.status == "unverified" && invalidFinding.blockers.Contains("invalid_unverified_ui_evidence") &&
            !invalidFinding.blockers.Contains("raw text from the game"));
    }
}
