using System;
using System.Linq;

namespace PawnPilot
{
    public static class M3ObservationTests
    {
        public static void Run(Action<string, bool> check)
        {
            var item = new BusinessItemInput { Identity = new object(), Name = "Food ration", Units = 1,
                Owned = true, ShapeStatus = "known", Width = 1, Height = 1 };
            var input = new BusinessInput
            {
                Customer = new BusinessCustomerInput { Identity = new object(), Presence = "present", Arrived = true,
                    TradeType = "buy", Phase = "trading", RequestText = "Looking for food", DialogueStage = "absent" },
                Containers = new[] {
                    new BusinessContainerInput { Container = "front", Completeness = "complete" },
                    new BusinessContainerInput { Container = "back", Completeness = "complete", Items = new[] { item } },
                    new BusinessContainerInput { Container = "player", Completeness = "complete" } },
                Quote = new BusinessQuoteInput { Status = "complete", Mode = "normal", DisplayedTotal = 12,
                    Reason = null, BindingVerified = true, PurchaseIdentities = new[] { item.Identity } }
            };
            var p = new BusinessProjection();
            var first = p.Capture("sale-world", true, () => input);
            check("m3_buy_customer_has_visible_request_and_owned_offer", BusinessWire.Validate(first) &&
                first.details.customer.trade_type == "buy" && first.details.customer.request_text == "Looking for food" &&
                first.details.quote.status == "complete" && first.item_refs.Count == 1 && first.offer_ref != null &&
                !first.details.eligibility.purchase_enabled);
            check("m3_sale_offer_resolves_exact_native_identity", p.TryResolvePurchase("sale-world", first.customer_ref,
                first.offer_ref, first.collection_fingerprint, out var identities, out _, out var amount) &&
                identities.Single() == item.Identity && amount == 12);
            input.Quote.DisplayedTotal = 13;
            var repriced = p.Capture("sale-world", true, () => input);
            check("m3_reprice_invalidates_sale_offer", repriced.offer_ref != first.offer_ref &&
                !p.TryResolvePurchase("sale-world", first.customer_ref, first.offer_ref, first.collection_fingerprint,
                    out _, out _, out _));
            input.Containers[1].Items = Array.Empty<BusinessItemInput>();
            input.Containers[2].Items = new[] { item };
            var removed = p.Capture("sale-world", true, () => input);
            check("m3_moved_item_cannot_reuse_old_sale_quote", removed.offer_ref == null && removed.item_refs.Count == 0 &&
                removed.details.items.Single().container == "player");
            input.Containers[1].Items = new[] { item };
            input.Containers[2].Items = Array.Empty<BusinessItemInput>();
            item.Owned = false;
            var notOwned = p.Capture("sale-world", true, () => input);
            check("m3_npc_buy_never_offers_nonowned_goods", notOwned.offer_ref == null && notOwned.item_refs.Count == 0);
            input.Customer.TradeType = "sell";
            input.Customer.MultiBuyDisabled = false;
            var purchase = p.Capture("sale-world", true, () => input);
            check("m3_npc_sell_preserves_nonowned_purchase_route", purchase.offer_ref != null &&
                purchase.details.customer.trade_type == "sell" && BusinessWire.Validate(purchase));
            input.Customer.Identity = new object();
            input.Customer.RequestText = null;
            var next = p.Capture("sale-world", true, () => input);
            check("m3_customer_change_revokes_offer_and_request", next.customer_ref != purchase.customer_ref &&
                next.offer_ref != purchase.offer_ref && next.details.customer.request_text == null);
        }
    }
}
