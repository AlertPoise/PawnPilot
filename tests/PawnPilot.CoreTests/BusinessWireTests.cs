using System;
using System.Collections.Generic;
using PawnPilot;

public static class BusinessWireTests
{
    public static int Run(Action<string, bool> check)
    {
        int count = 0;
        void C(string name, bool value) { check(name, value); count++; }

        var partial = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails() };
        C("business_wire_accepts_explicit_unavailable_partial", BusinessWire.Validate(partial));
        var staleRefs = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails(), collection_fingerprint = "old" };
        C("business_wire_rejects_collection_without_current_customer", !BusinessWire.Validate(staleRefs));
        staleRefs.collection_fingerprint = null; staleRefs.details.customer.dialogue_ref = "old-dialogue";
        C("business_wire_rejects_dialogue_without_current_customer", !BusinessWire.Validate(staleRefs));
        var aggregateOversized = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails() };
        for (int n = 0; n < 64; n++) aggregateOversized.details.ui.blockers.Add(new string('x', 256));
        C("business_wire_enforces_aggregate_text_budget", !BusinessWire.Validate(aggregateOversized));
        var absentInconsistent = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails() };
        absentInconsistent.details.customer = new CustomerState { presence = "absent", phase = "dialogue", dialogue_stage = "choices", trade_type = "sell" };
        C("business_wire_rejects_inconsistent_absent_customer", !BusinessWire.Validate(absentInconsistent));
        var departed = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails() };
        departed.details.customer = new CustomerState { presence = "absent", phase = "leaving", dialogue_stage = "unknown", trade_type = "unknown" };
        C("business_wire_keeps_customer_leaving_without_live_refs", BusinessWire.Validate(departed));
        var unresolvedRow = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails() };
        unresolvedRow.details.quote.status = "unverified";
        unresolvedRow.details.quote.rows.Add(new QuoteRowState { item_ref = null, price_text = "?" });
        C("business_wire_allows_unbound_unverified_quote_rows", BusinessWire.Validate(unresolvedRow));
        C("business_wire_requires_details", !BusinessWire.Validate(new BusinessObservation { support = "unsupported" }));
        C("business_wire_rejects_invalid_support", !BusinessWire.Validate(new BusinessObservation { support = "Available", details = new BusinessDetails(), item_refs = new List<string>() }));

        var complete = CompleteQuote();
        C("business_wire_accepts_bounded_complete_quote", BusinessWire.Validate(complete));
        var blockedQuote = CompleteQuote(); blockedQuote.details.ui.status = "blocked"; blockedQuote.details.ui.blockers.Add("dialog");
        C("business_wire_rejects_complete_quote_when_ui_blocked", !BusinessWire.Validate(blockedQuote));
        var unboundQuote = CompleteQuote(); unboundQuote.offer_ref = null;
        C("business_wire_rejects_unbound_complete_quote", !BusinessWire.Validate(unboundQuote));
        var falselyBoundUnverified = new BusinessObservation { support = "partial", customer_ref = "customer-1", offer_ref = "offer-1", item_refs = new List<string>(), details = new BusinessDetails() };
        falselyBoundUnverified.details.quote.status = "unverified";
        C("business_wire_rejects_offer_ref_for_unverified_quote", !BusinessWire.Validate(falselyBoundUnverified));
        falselyBoundUnverified.details.quote.status = "unavailable";
        C("business_wire_rejects_offer_ref_for_unavailable_quote", !BusinessWire.Validate(falselyBoundUnverified));
        var badSum = CompleteQuote(); badSum.details.quote.displayed_total = 4;
        C("business_wire_rejects_complete_quote_total_mismatch", !BusinessWire.Validate(badSum));
        var ownedBack = CompleteQuote(); ownedBack.details.items[0].owned = true;
        C("business_wire_rejects_complete_quote_for_owned_back_item", !BusinessWire.Validate(ownedBack));
        var notCompleteBack = CompleteQuote(); notCompleteBack.details.containers[0].completeness = "incomplete"; notCompleteBack.details.containers[0].reason = "partial";
        C("business_wire_rejects_complete_quote_for_incomplete_back", !BusinessWire.Validate(notCompleteBack));
        var enabled = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails() };
        enabled.details.eligibility.purchase_enabled = true;
        C("business_wire_never_accepts_purchase_enabled", !BusinessWire.Validate(enabled));

        var duplicateContainers = CompleteQuote();
        duplicateContainers.details.containers.Add(new ContainerState { container = "back", completeness = "complete", item_refs = new List<string> { "item-1" } });
        C("business_wire_rejects_duplicate_containers", !BusinessWire.Validate(duplicateContainers));
        var tooManyContainers = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails() };
        tooManyContainers.details.containers.AddRange(new[] {
            new ContainerState { container = "front", completeness = "unavailable", reason = "unknown", item_refs = new List<string>() },
            new ContainerState { container = "back", completeness = "unavailable", reason = "unknown", item_refs = new List<string>() },
            new ContainerState { container = "player", completeness = "unavailable", reason = "unknown", item_refs = new List<string>() },
            new ContainerState { container = "front", completeness = "unavailable", reason = "unknown", item_refs = new List<string>() }
        });
        C("business_wire_bounds_containers", !BusinessWire.Validate(tooManyContainers));
        var mismatched = CompleteQuote(); mismatched.details.containers[0].item_refs.Clear();
        C("business_wire_rejects_container_item_ref_mismatch", !BusinessWire.Validate(mismatched));
        var extra = CompleteQuote(); extra.item_refs.Add("item-2");
        C("business_wire_rejects_unemitted_top_level_item_ref", !BusinessWire.Validate(extra));
        var oversized = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails() };
        oversized.details.items.Add(new ItemState());
        for (int i = 0; i < 65; i++) oversized.details.items.Add(new ItemState { item_ref = "i" + i, container = "front", units = 1 });
        C("business_wire_bounds_items", !BusinessWire.Validate(oversized));

        var source = CompleteQuote();
        var copy = BusinessWire.Clone(source.details);
        copy.customer.option_refs.Add("option-2");
        copy.containers[0].item_refs[0] = "changed";
        copy.items[0].name = "changed";
        copy.quote.rows[0].amount = 999;
        copy.ui.blockers.Add("changed");
        copy.eligibility.missing.Clear();
        copy.candidates.Add(new BusinessCandidate { action = "close_menu", status = "candidate", reason = "copy" });
        C("business_wire_clone_copies_nested_lists_and_rows", source.details.customer.option_refs.Count == 0 &&
            source.details.containers[0].item_refs[0] == "item-1" && source.details.items[0].name == "Cup" &&
            source.details.quote.rows[0].amount == 5 && source.details.ui.blockers.Count == 0 &&
            source.details.eligibility.missing.Count == 4 && source.details.candidates.Count == 0);
        C("business_wire_clone_null_safe", BusinessWire.Clone(null) == null);
        return count;
    }

    private static BusinessObservation CompleteQuote()
    {
        return new BusinessObservation
        {
            support = "available", customer_ref = "customer-1", offer_ref = "offer-1", collection_fingerprint = "collection-1",
            item_refs = new List<string> { "item-1" },
            details = new BusinessDetails
            {
                status = "complete",
                customer = new CustomerState { presence = "present", arrived = true, trade_type = "sell", phase = "trading" },
                containers = new List<ContainerState> { new ContainerState { container = "back", completeness = "complete", item_refs = new List<string> { "item-1" } } },
                items = new List<ItemState> { new ItemState { item_ref = "item-1", container = "back", name = "Cup", units = 1, owned = false, shape_status = "known", width = 1, height = 1 } },
                quote = new QuoteState { status = "complete", mode = "normal", displayed_total = 5, reason = null,
                    rows = new List<QuoteRowState> { new QuoteRowState { item_ref = "item-1", name = "Cup", price_text = "$5", amount = 5 } } },
                ui = new BusinessUiState { status = "consistent", coverage = "verified", blockers = new List<string>() },
                capacity = new CapacityState { status = "unverified", reason = "native_rules_unverified", scope = "none" },
                eligibility = new PurchaseEligibility { purchase_enabled = false, missing = new List<string> { "quote_binding", "capacity", "settlement", "ui_coverage" } }
            }
        };
    }
}
