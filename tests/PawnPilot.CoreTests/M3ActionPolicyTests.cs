using System;
using System.Collections.Generic;
using PawnPilot;

public static class M3ActionPolicyTests
{
    public static void Run(Action<string, bool> check)
    {
        check("m3_actions_registered_and_enabled",
            BusinessActionPolicy.IsRegistered("prepare_sale_items") && BusinessActionPolicy.IsEnabled("prepare_sale_items") &&
            BusinessActionPolicy.IsRegistered("sell_visible_offer") && BusinessActionPolicy.IsEnabled("sell_visible_offer") &&
            BusinessActionPolicy.IsRegistered("sort_inventory_default") && BusinessActionPolicy.IsEnabled("sort_inventory_default"));

        var requestBusiness = State(true).business;
        requestBusiness.details.customer.request_text = new string('r', 256);
        check("m3_request_text_accepts_maximum_length", BusinessWire.Validate(requestBusiness));
        requestBusiness.details.customer.request_text = new string('r', 257);
        check("m3_request_text_rejects_overlength", !BusinessWire.Validate(requestBusiness));
        var legacy = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = new BusinessDetails { schema_version = 1 } };
        check("m3_request_text_and_buy_type_preserve_schema1_contract", BusinessWire.Validate(legacy));
        legacy.details.customer.trade_type = "buy";
        check("m3_buy_type_is_schema2_only", !BusinessWire.Validate(legacy));

        var sale = new ActionArguments { customer_ref = "customer:1", item_refs = new List<string> { "item:player" },
            collection_fingerprint = "collection:1" };
        check("m3_prepare_sale_argument_binding_is_one_item",
            BusinessActionPolicy.ArgumentsValid("prepare_sale_items", sale) &&
            !BusinessActionPolicy.ArgumentsValid("prepare_sale_items", new ActionArguments { customer_ref = "customer:1",
                item_refs = new List<string> { "item:player", "item:other" }, collection_fingerprint = "collection:1" }) &&
            !BusinessActionPolicy.ArgumentsValid("purchase_visible_offer", new ActionArguments { customer_ref = "customer:1",
                offer_ref = "offer:1", collection_fingerprint = "collection:1", item_refs = new List<string> { "item:1" },
                mode = "normal", amount = 1, max_spend = 1, container = "player" }));

        var buyState = State(true);
        var playerItem = buyState.business.details.items[0];
        sale.collection_fingerprint = BusinessCollection.Fingerprint("world:1", "customer:1", "buy",
            new[] { new BusinessCollectionMember { item_ref = playerItem.item_ref, container = "player", units = 1, owned = true } },
            "unknown", "unverified", null);
        buyState.legal_actions.Add(new LegalAction { action = "prepare_sale_items", arguments = sale });
        check("m3_prepare_sale_candidate_uses_owned_player_member_fingerprint",
            BusinessActionPolicy.ValidateCandidate(new Command { schema_version = 3, action = "prepare_sale_items", arguments = sale }, buyState) == null);
        var otherSale = new ActionArguments { customer_ref = "customer:1", item_refs = new List<string> { "item:other" },
            collection_fingerprint = "collection:other" };
        buyState.legal_actions.Add(new LegalAction { action = "prepare_sale_items", arguments = otherSale });
        check("m3_legal_actions_allow_distinct_candidates_of_one_action", ActionGate.IsSnapshotValid(buyState));
        buyState.legal_actions.Add(new LegalAction { action = "prepare_sale_items", arguments = otherSale });
        check("m3_legal_actions_reject_duplicate_action_argument_pairs", !ActionGate.IsSnapshotValid(buyState));
        buyState.legal_actions.RemoveAt(buyState.legal_actions.Count - 1);
        sale.collection_fingerprint = "collection:stale";
        check("m3_prepare_sale_rejects_stale_fingerprint",
            BusinessActionPolicy.ValidateCandidate(new Command { schema_version = 3, action = "prepare_sale_items", arguments = sale }, buyState) != null);

        var offer = new ActionArguments { customer_ref = "customer:1", item_refs = new List<string> { "item:back" },
            offer_ref = "offer:1", collection_fingerprint = "collection:offer", mode = "normal", amount = 17 };
        var offerState = State(true);
        offerState.business.details.containers[1].item_refs.Add("item:back");
        offerState.business.details.items.Add(new ItemState { item_ref = "item:back", container = "back", units = 1, owned = true });
        offerState.business.item_refs.Add("item:back");
        offerState.business.offer_ref = "offer:1";
        offerState.business.collection_fingerprint = "collection:offer";
        offerState.business.details.quote = new QuoteState { status = "complete", mode = "normal", displayed_total = 17, rows = new List<QuoteRowState>() };
        offerState.business.details.capacity.status = "unverified";
        offerState.business.details.eligibility.purchase_enabled = false;
        offerState.legal_actions.Add(new LegalAction { action = "sell_visible_offer", arguments = offer });
        check("m3_sell_offer_candidate_does_not_depend_on_purchase_eligibility",
            BusinessActionPolicy.ValidateCandidate(new Command { schema_version = 3, action = "sell_visible_offer", arguments = offer }, offerState) == null);
        offerState.business.details.items[1].owned = false;
        check("m3_sell_offer_rejects_nonowned_back_item",
            BusinessActionPolicy.ValidateCandidate(new Command { schema_version = 3, action = "sell_visible_offer", arguments = offer }, offerState) != null);

        var sortState = State(false);
        var sort = new ActionArguments { container = "player" };
        sortState.legal_actions.Add(new LegalAction { action = "sort_inventory_default", arguments = sort });
        check("m3_sort_candidate_requires_player_container_only",
            BusinessActionPolicy.ValidateCandidate(new Command { schema_version = 3, action = "sort_inventory_default", arguments = sort }, sortState) == null &&
            !BusinessActionPolicy.ArgumentsValid("sort_inventory_default", new ActionArguments { container = "back" }));
        sortState.business.details.ui.status = "unverified";
        sortState.business.details.ui.coverage = "known_roots_only";
        sortState.business.details.ui.blockers.AddRange(new[] { "global_ui_stack_unobservable", "global_raycast_order_unobservable" });
        sortState.interaction.consistency = "unverified";
        check("m3_sort_allows_global_unobservable_ui_evidence",
            BusinessActionPolicy.ValidateCandidate(new Command { schema_version = 3, action = "sort_inventory_default", arguments = sort }, sortState) == null);
    }

    private static Snapshot State(bool buying)
    {
        var customer = new CustomerState { presence = buying ? "present" : "absent", trade_type = buying ? "buy" : "unknown",
            phase = buying ? "trading" : "absent", arrived = buying ? true : (bool?)null, dialogue_stage = "absent" };
        var back = new ContainerState { container = "back", completeness = "complete" };
        var player = new ContainerState { container = "player", completeness = "complete", item_refs = new List<string> { "item:player" } };
        var business = new BusinessObservation { support = "available", customer_ref = buying ? "customer:1" : null,
            item_refs = new List<string>(), details = new BusinessDetails { schema_version = 2, status = "complete", customer = customer,
                containers = new List<ContainerState> { new ContainerState { container = "front", completeness = "complete" }, back, player },
                items = new List<ItemState> { new ItemState { item_ref = "item:player", container = "player", units = 1, owned = true } },
                ui = new BusinessUiState { status = "consistent", coverage = "verified", blockers = new List<string>() } } };
        return new Snapshot { schema_version = 3, loaded = true, ready = true, world_epoch = "world:1", version = "world:1:1", scene = "shop", phase = "OPEN",
            revision = 1, business = business, interaction = new InteractionObservation { phase = "OPEN", consistency = "consistent", operable = true },
            legal_actions = new List<LegalAction>() };
    }
}
