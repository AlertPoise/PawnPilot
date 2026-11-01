using System;
using PawnPilot;

public static class SaleVerifierTests
{
    public static void Run(Action<string, bool> check)
    {
        var fixture = new Fixture();
        check("sale_single_item_preflight_passes_for_bound_buy_offer",
            SaleVerifier.ValidatePreflight(fixture.Request, fixture.Before) == null);

        var latch = new SaleInvocationLatch("cmd-sale");
        int invocations = 0;
        bool first = latch.InvokeOnce("cmd-sale", () => invocations++);
        bool replay = latch.InvokeOnce("cmd-sale", () => invocations++);
        check("sale_invocation_is_once_only", first && !replay && invocations == 1 && latch.InvocationReturned);
        check("sale_exact_cash_sold_container_and_ownership_settlement_succeeds",
            SaleVerifier.Verify(fixture.Request, fixture.Before, fixture.After(124, "sold", owned: false), latch).outcome == "succeeded");

        var noEffectLatch = new SaleInvocationLatch("cmd-sale");
        noEffectLatch.InvokeOnce("cmd-sale", () => { });
        check("sale_no_visible_effect_remains_pending_without_retry",
            SaleVerifier.Verify(fixture.Request, fixture.Before, fixture.After(100, "back", owned: true), noEffectLatch).outcome == "pending");

        var partialLatch = new SaleInvocationLatch("cmd-sale");
        partialLatch.InvokeOnce("cmd-sale", () => { });
        check("sale_cash_delta_without_sold_item_is_uncertain",
            SaleVerifier.Verify(fixture.Request, fixture.Before, fixture.After(124, "back", owned: true), partialLatch).outcome == "uncertain");

        var wrongCashLatch = new SaleInvocationLatch("cmd-sale");
        wrongCashLatch.InvokeOnce("cmd-sale", () => { });
        check("sale_wrong_cash_delta_is_uncertain",
            SaleVerifier.Verify(fixture.Request, fixture.Before, fixture.After(123, "sold", owned: false), wrongCashLatch).outcome == "uncertain");

        var changedQuantityLatch = new SaleInvocationLatch("cmd-sale");
        changedQuantityLatch.InvokeOnce("cmd-sale", () => { });
        check("sale_quantity_change_is_uncertain",
            SaleVerifier.Verify(fixture.Request, fixture.Before, fixture.After(124, "sold", owned: false, units: 2), changedQuantityLatch).outcome == "uncertain");

        check("sale_preflight_rejects_wrong_customer_intent",
            SaleVerifier.ValidatePreflight(fixture.Request, fixture.BeforeWith(intent: "sell")) == "sale_customer_mismatch");
        check("sale_preflight_rejects_multiple_back_items",
            SaleVerifier.ValidatePreflight(fixture.Request, fixture.BeforeWith(extraBack: true)) == "sale_back_must_contain_one_item");
        check("sale_preflight_rejects_quote_amount_mismatch",
            SaleVerifier.ValidatePreflight(fixture.Request, fixture.BeforeWith(amount: 25)) == "sale_quote_unverified");
        check("sale_preflight_rejects_unowned_target",
            SaleVerifier.ValidatePreflight(fixture.Request, fixture.BeforeWith(owned: false)) == "sale_target_before_state_mismatch");

        var exceptionLatch = new SaleInvocationLatch("cmd-sale");
        try { exceptionLatch.InvokeOnce("cmd-sale", () => throw new InvalidOperationException("fixture")); }
        catch (InvalidOperationException) { }
        check("sale_native_exception_is_uncertain_and_nonreplayable",
            SaleVerifier.Verify(fixture.Request, fixture.Before, fixture.After(124, "sold", owned: false), exceptionLatch).outcome == "uncertain" &&
            !exceptionLatch.InvokeOnce("cmd-sale", () => { }));
    }

    private sealed class Fixture
    {
        public readonly SaleRequest Request = new SaleRequest("cmd-sale", "world-1", "world-1:10", 10,
            "customer-1", "offer-1", new[] { "item-a" }, "collection-1", "normal", 24);
        public readonly SaleEvidence Before;

        public Fixture() { Before = Evidence(10, 100, "buy", "back", true, 1, 24); }

        public SaleEvidence BeforeWith(string intent = "buy", bool extraBack = false, int? amount = 24,
            bool owned = true)
        {
            var items = extraBack
                ? new[] { Item("item-a", "object-a", "back", 1, owned), Item("item-b", "object-b", "back", 1, true) }
                : new[] { Item("item-a", "object-a", "back", 1, owned) };
            return Evidence(10, 100, intent, "back", owned, 1, amount, items);
        }

        public SaleEvidence After(int cash, string container, bool owned, int units = 1) =>
            Evidence(11, cash, "buy", container, owned, units, 24);

        private static SaleEvidence Evidence(int revision, int cash, string intent, string targetContainer,
            bool owned, int units, int? amount, SaleItemEvidence[] items = null) =>
            new SaleEvidence("cmd-sale", "complete", "world-1", "InventoryScene", revision,
                "world-1:" + revision, cash, "present", "customer-1", intent, "trading", "offer-1",
                "collection-1", "complete", "normal", amount, new[] { "item-a" },
                items ?? new[] { Item("item-a", "object-a", targetContainer, units, owned) }, true,
                "back-inventory", "sold-inventory");

        private static SaleItemEvidence Item(string itemRef, string identity, string container, int units, bool owned) =>
            new SaleItemEvidence(itemRef, identity, container, units, owned);
    }
}
