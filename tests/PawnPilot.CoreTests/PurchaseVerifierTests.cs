using System;
using System.Collections.Generic;
using PawnPilot;

public static class PurchaseVerifierTests
{
    public static void Run(Action<string, bool> check)
    {
        var one = Fixture.Create(new[] { "item-a" }, new[] { 24 }, cash: 100);
        var oneRequest = one.Request();
        var preflight = PurchaseVerifier.ValidatePreflight(oneRequest, one.Before);
        check("purchase_single_preflight_passes_with_bound_capacity", preflight.Accepted && one.Before.PassedCapacity);

        var sameCustomer = one.After(76, customer: "present", movedCount: 1);
        var verified = Verify(one, sameCustomer);
        check("purchase_single_exact_settlement_succeeds", verified.outcome == "succeeded" && verified.reason == "purchase_settled");

        var rotatedRefAfter = one.After(76, customer: "present", movedCount: 1, rotateMovedItemRefs: true);
        check("purchase_settlement_binds_native_item_identity_when_public_ref_rotates",
            Verify(one, rotatedRefAfter).outcome == "succeeded");

        var two = Fixture.Create(new[] { "item-a", "item-b" }, new[] { 24, 31 }, cash: 100);
        var twoPreflight = PurchaseVerifier.ValidatePreflight(two.Request(), two.Before);
        check("purchase_collection_preflight_passes_when_proven", twoPreflight.Accepted && two.Before.PassedCapacity &&
            two.Proof.AcceptanceOrderRefs[0] == "item-b" && two.Proof.AcceptanceOrderRefs[1] == "item-a");
        var twoAfter = two.After(45, customer: "present", movedCount: 2);
        var twoResult = Verify(two, twoAfter);
        check("purchase_collection_settlement_succeeds", twoResult.outcome == "succeeded");

        var selectedOne = one.WithDisabledMultiBuyExtraStock();
        var selectedOneDecision = PurchaseVerifier.ValidatePreflight(
            one.Request(amount: 24, maxSpend: 100, itemRefs: new[] { "item-a" }), selectedOne);
        check("purchase_disabled_multibuy_accepts_bound_single_selection_with_complete_back_evidence",
            selectedOneDecision.Accepted && selectedOne.PassedCapacity && selectedOne.Items.Count == 3);

        var dismissedAfter = one.After(76, customer: "absent", movedCount: 1);
        var dismissed = Verify(one, dismissedAfter);
        check("purchase_allows_customer_clear_after_complete_settlement", dismissed.outcome == "succeeded" &&
            dismissed.reason == "purchase_settled");
        var dismissedShortCash = one.After(77, customer: "absent", movedCount: 1);
        check("purchase_dismissal_without_exact_cash_is_uncertain",
            Verify(one, dismissedShortCash).outcome == "uncertain");

        var replacement = one.After(76, customer: "replacement", movedCount: 1);
        check("purchase_allows_next_customer_after_complete_settlement",
            Verify(one, replacement).outcome == "succeeded");

        var partial = two.After(45, customer: "present", movedCount: 1);
        check("purchase_partial_collection_is_uncertain",
            Verify(two, partial).outcome == "uncertain");
        var partialDismissed = two.After(45, customer: "absent", movedCount: 1);
        check("purchase_customer_clear_does_not_hide_partial_transfer",
            Verify(two, partialDismissed).outcome == "uncertain");

        var extraItemChange = one.After(76, customer: "present", movedCount: 1, changeUnrelatedState: true);
        check("purchase_does_not_reject_unrelated_item_state_changes",
            Verify(one, extraItemChange).outcome == "succeeded");
        check("purchase_rejects_unexpected_player_inventory_item_after_settlement",
            Verify(one, one.AfterWithUnexpectedInventoryItem(76, "player")).reason ==
                "unexpected_inventory_item_identity");
        check("purchase_rejects_unexpected_back_inventory_item_after_settlement",
            Verify(one, one.AfterWithUnexpectedInventoryItem(76, "back")).reason ==
                "unexpected_inventory_item_identity");
        var extraCashChange = one.After(75, customer: "present", movedCount: 1);
        check("purchase_extra_cash_delta_is_uncertain",
            Verify(one, extraCashChange).reason == "cash_delta_mismatch");
        var changedUnits = one.After(76, customer: "present", movedCount: 1, targetUnits: 2);
        check("purchase_target_quantity_must_be_preserved",
            Verify(one, changedUnits).reason == "target_transfer_incomplete_or_partial");
        check("purchase_target_shape_and_feature_changes_after_native_ownership_callback_are_allowed",
            Verify(one, one.AfterWithChangedTargetFacts(76)).outcome == "succeeded");
        var existingInventoryBefore = one.BeforeWithExistingInventory();
        check("purchase_preserves_unpurchased_player_and_back_items_by_identity",
            Verify(one.Request(), existingInventoryBefore, one.AfterWithExistingInventory(76)).outcome == "succeeded");
        check("purchase_missing_unpurchased_player_item_is_uncertain",
            Verify(one.Request(), existingInventoryBefore,
                one.AfterWithExistingInventory(76, omitPlayer: true)).reason == "existing_inventory_item_missing");
        check("purchase_changed_unpurchased_back_quantity_is_uncertain",
            Verify(one.Request(), existingInventoryBefore,
                one.AfterWithExistingInventory(76, changeBackUnits: true)).reason == "existing_inventory_item_changed");
        check("purchase_source_inventory_identity_must_be_preserved",
            Verify(one, one.AfterWithChangedSourceRef(76)).reason == "inventory_target_changed_after_invocation");
        var changedWorld = one.After(76, customer: "present", movedCount: 1, world: "world-2");
        check("purchase_world_change_is_uncertain",
            Verify(one, changedWorld).reason == "world_changed_after_invocation");

        var spendingMismatch = one.Request(amount: 25, maxSpend: 24);
        check("purchase_preflight_rejects_spending_above_requested_limit",
            !PurchaseVerifier.ValidatePreflight(spendingMismatch, one.Before).Accepted);
        var maxSpendBelowQuote = one.Request(amount: 24, maxSpend: 23);
        check("purchase_preflight_rejects_quote_above_max_spend",
            !PurchaseVerifier.ValidatePreflight(maxSpendBelowQuote, one.Before).Accepted);
        var maxSpendAboveCash = one.Request(amount: 24, maxSpend: 101);
        check("purchase_preflight_rejects_max_spend_above_current_cash",
            !PurchaseVerifier.ValidatePreflight(maxSpendAboveCash, one.Before).Accepted);
        var wrongCustomer = one.Request(customer: "other-customer");
        check("purchase_preflight_rejects_customer_ref_mismatch",
            PurchaseVerifier.ValidatePreflight(wrongCustomer, one.Before).Reason == "customer_ref_mismatch");
        var wrongItems = one.Request(itemRefs: new[] { "other-item" });
        check("purchase_preflight_rejects_collection_ref_mismatch",
            PurchaseVerifier.ValidatePreflight(wrongItems, one.Before).Reason == "purchase_collection_mismatch");
        var wrongCommand = new PurchaseRequest("other-command", "world-1", "world-1:10", 10,
            "customer-1", "offer-1", new[] { "item-a" }, "collection-1", "normal", 24, 24);
        check("purchase_preflight_rejects_evidence_for_other_command",
            PurchaseVerifier.ValidatePreflight(wrongCommand, one.Before).Reason == "command_id_mismatch");

        var noProof = one.WithEvidence(removeProof: true);
        check("purchase_preflight_rejects_missing_capacity_proof",
            PurchaseVerifier.ValidatePreflight(one.Request(), noProof).Reason == "capacity_unproven");
        var wrongRevisionSnapshot = one.CapacityAtRevision(one.Before.Revision + 1);
        var staleProof = one.WithEvidence(capacitySnapshot: wrongRevisionSnapshot,
            capacityProof: CapacityProof.Evaluate(wrongRevisionSnapshot));
        check("purchase_preflight_rejects_capacity_proof_from_other_revision",
            PurchaseVerifier.ValidatePreflight(one.Request(), staleProof).Reason == "capacity_unproven");
        var divergentFacts = one.WithAlteredSourceFact();
        check("purchase_capacity_proof_must_match_same_item_facts_as_evidence",
            !divergentFacts.PassedCapacity && PurchaseVerifier.ValidatePreflight(one.Request(), divergentFacts).Reason == "capacity_unproven");
        var forgedFingerprint = one.WithForgedSourceFingerprint();
        check("purchase_source_fingerprint_must_be_derived_from_capacity_facts",
            !forgedFingerprint.PassedCapacity && !PurchaseVerifier.ValidatePreflight(one.Request(), forgedFingerprint).Accepted);
        var hiddenStock = one.WithExtraNonOwnedBackItem();
        check("purchase_preflight_rejects_omitted_nonowned_back_item",
            PurchaseVerifier.ValidatePreflight(one.Request(), hiddenStock).Reason == "purchase_collection_not_complete");
        var incomplete = one.WithEvidence(containersComplete: false);
        check("purchase_preflight_rejects_partial_inventory_evidence",
            PurchaseVerifier.ValidatePreflight(one.Request(), incomplete).Reason == "evidence_incomplete");

        var mutableRefs = new List<string> { "item-a" };
        var immutable = one.EvidenceWithOrder(mutableRefs);
        mutableRefs[0] = "tampered";
        check("purchase_evidence_defensively_copies_refs", immutable.PurchaseListOrderRefs[0] == "item-a");
        var readOnlyItems = immutable.Items as IList<PurchaseItemEvidence>;
        bool readOnly = false;
        try { readOnlyItems[0] = new PurchaseItemEvidence("tampered", "back", 1, false, "state"); }
        catch (NotSupportedException) { readOnly = true; }
        check("purchase_evidence_item_list_is_read_only", readOnly);

        var latch = new PurchaseInvocationLatch(oneRequest, preflight);
        int calls = 0;
        bool firstInvocation = latch.InvokeOnce("cmd-1", () => calls++);
        bool secondInvocation = latch.InvokeOnce("cmd-1", () => calls++);
        check("purchase_native_entry_is_exactly_once", firstInvocation && !secondInvocation && calls == 1 && latch.InvocationReturned);
        latch.MarkOutcomeUnknown();
        check("purchase_unknown_attempt_remains_non_replayable", latch.IsUncertain && !latch.InvokeOnce("cmd-1", () => calls++) && calls == 1);

        var exceptionRequest = one.Request();
        var exceptionPreflight = PurchaseVerifier.ValidatePreflight(exceptionRequest, one.Before);
        var exceptionLatch = new PurchaseInvocationLatch(exceptionRequest, exceptionPreflight);
        bool threw = false;
        try { exceptionLatch.InvokeOnce("cmd-1", () => throw new InvalidOperationException("fixture")); }
        catch (InvalidOperationException) { threw = true; }
        check("purchase_native_exception_marks_uncertain", threw && exceptionLatch.InvocationStarted &&
            exceptionLatch.IsUncertain && !exceptionLatch.InvocationReturned);
        check("purchase_native_exception_cannot_replay", !exceptionLatch.InvokeOnce("cmd-1", () => calls++) && calls == 1);
        check("purchase_latch_rejects_wrong_command_id", !new PurchaseInvocationLatch(oneRequest, preflight)
            .InvokeOnce("different-command", () => calls++));
        var firstUseRequest = one.Request();
        var firstUsePreflight = PurchaseVerifier.ValidatePreflight(firstUseRequest, one.Before);
        var firstUseLatch = new PurchaseInvocationLatch(firstUseRequest, firstUsePreflight);
        var secondUseLatch = new PurchaseInvocationLatch(firstUseRequest, firstUsePreflight);
        check("purchase_latch_prevents_parallel_replay",
            firstUseLatch.InvokeOnce("cmd-1", () => { }) && !secondUseLatch.InvokeOnce("cmd-1", () => { }));
        var noInvocationRequest = one.Request();
        var noInvocationPreflight = PurchaseVerifier.ValidatePreflight(noInvocationRequest, one.Before);
        var noInvocationLatch = new PurchaseInvocationLatch(noInvocationRequest, noInvocationPreflight);
        check("purchase_settlement_requires_invocation_receipt",
            PurchaseVerifier.Verify(noInvocationRequest, one.Before,
                one.After(76, "present", 1), noInvocationLatch).outcome == "uncertain");
        var thrownRequest = one.Request();
        var thrownPreflight = PurchaseVerifier.ValidatePreflight(thrownRequest, one.Before);
        var thrownLatch = new PurchaseInvocationLatch(thrownRequest, thrownPreflight);
        try { thrownLatch.InvokeOnce("cmd-1", () => throw new InvalidOperationException("fixture")); }
        catch (InvalidOperationException) { }
        check("purchase_settlement_with_exception_receipt_is_uncertain",
            PurchaseVerifier.Verify(thrownRequest, one.Before,
                one.After(76, "present", 1), thrownLatch).outcome == "uncertain");
        var boundRequest = one.Request();
        var boundPreflight = PurchaseVerifier.ValidatePreflight(boundRequest, one.Before);
        var boundLatch = new PurchaseInvocationLatch(boundRequest, boundPreflight);
        boundLatch.InvokeOnce("cmd-1", () => { });
        var alternateSameVersion = one.EvidenceWithCash(101);
        var alternatePreflight = PurchaseVerifier.ValidatePreflight(boundRequest, alternateSameVersion);
        var alternateBeforeResult = PurchaseVerifier.Verify(boundRequest, alternateSameVersion,
            one.After(76, "present", 1), boundLatch);
        check("purchase_latch_is_bound_to_exact_preflight_snapshot_not_same_version_substitute",
            one.Before.Version == alternateSameVersion.Version && alternatePreflight.Accepted &&
            alternateBeforeResult.outcome == "uncertain" && alternateBeforeResult.reason == "invocation_evidence_missing");
    }

    private static VerificationDecision Verify(Fixture fixture, PurchaseEvidence after)
    {
        var request = fixture.Request();
        var preflight = PurchaseVerifier.ValidatePreflight(request, fixture.Before);
        var latch = new PurchaseInvocationLatch(request, preflight);
        if (preflight.Accepted) latch.InvokeOnce(request.CommandId, () => { });
        return PurchaseVerifier.Verify(request, fixture.Before, after, latch);
    }

    private static VerificationDecision Verify(PurchaseRequest request, PurchaseEvidence before, PurchaseEvidence after)
    {
        var preflight = PurchaseVerifier.ValidatePreflight(request, before);
        var latch = new PurchaseInvocationLatch(request, preflight);
        if (preflight.Accepted) latch.InvokeOnce(request.CommandId, () => { });
        return PurchaseVerifier.Verify(request, before, after, latch);
    }

    private sealed class Fixture
    {
        private readonly string[] refs;
        private readonly int[] prices;
        private readonly int cash;
        private readonly string customer;
        private readonly string targetInventory;
        private readonly string sourceInventory;
        private readonly string fingerprint;
        public CapacitySnapshot CapacitySnapshot { get; }
        public CapacityProofResult Proof { get; }
        public PurchaseEvidence Before { get; }

        private Fixture(string[] refs, int[] prices, int cash, string customer = "customer-1",
            string targetInventory = "player-inv-1", string sourceInventory = "back-inv-1",
            string fingerprint = "collection-1")
        {
            this.refs = (string[])refs.Clone();
            this.prices = (int[])prices.Clone();
            this.cash = cash;
            this.customer = customer;
            this.targetInventory = targetInventory;
            this.sourceInventory = sourceInventory;
            this.fingerprint = fingerprint;
            CapacitySnapshot = Capacity(this.refs, targetInventory, sourceInventory, revision: 10);
            Proof = CapacityProof.Evaluate(CapacitySnapshot);
            Before = Evidence("cmd-1", "complete", 10, cash, "present", customer,
                this.refs, this.prices, this.refs, this.targetInventory, this.sourceInventory,
                this.fingerprint, allItems: BeforeItems(this.refs, CapacitySnapshot), capacitySnapshot: CapacitySnapshot,
                capacityProof: Proof);
        }

        public static Fixture Create(string[] refs, int[] prices, int cash)
        {
            if (refs == null || prices == null || refs.Length != prices.Length) throw new ArgumentException();
            return new Fixture(refs, prices, cash);
        }

        public PurchaseRequest Request(int? amount = null, int? maxSpend = null, string customer = null,
            string[] itemRefs = null)
        {
            int total = amount ?? Sum(prices);
            return new PurchaseRequest("cmd-1", "world-1", "world-1:10", 10,
                customer ?? this.customer, "offer-1", itemRefs ?? refs, fingerprint, "normal",
                total, maxSpend ?? total);
        }

        public PurchaseEvidence After(int cashAfter, string customer, int movedCount, bool changeUnrelatedState = false,
            int? targetUnits = null, string world = "world-1", bool rotateMovedItemRefs = false)
        {
            var afterItems = new List<PurchaseItemEvidence>();
            for (int i = 0; i < refs.Length; i++)
            {
                string container = i < movedCount ? "player" : "back";
                int units = i == 0 && targetUnits.HasValue ? targetUnits.Value : 1;
                string postRef = rotateMovedItemRefs && container == "player" ? "post-item-" + i : refs[i];
                CapacityItemSnapshot facts = rotateMovedItemRefs && container == "player" ? null :
                    CapacityItem(postRef, container == "player" ? targetInventory : sourceInventory,
                        "common_medical", i, owned: i < movedCount, units: units);
                afterItems.Add(new PurchaseItemEvidence(postRef, container, units,
                    i < movedCount, capacityFacts: facts, identityKey: "purchase-object-" + i));
            }
            afterItems.Add(new PurchaseItemEvidence("unrelated", "front", 1, false,
                changeUnrelatedState ? "changed-state" : "stable-state"));
            var presence = customer == "absent" ? "absent" : "present";
            string customerRef = customer == "absent" ? null : customer == "replacement" ? "customer-2" : this.customer;
            var remaining = new List<string>();
            for (int i = movedCount; i < refs.Length; i++) remaining.Add(refs[i]);
            return Evidence("cmd-1", "complete", 11, cashAfter, presence, customerRef,
                remaining.ToArray(), Array.Empty<int>(), refs,
                targetInventory, sourceInventory, fingerprint, afterItems,
                capacitySnapshot: null, capacityProof: null, worldEpoch: world);
        }

        public PurchaseEvidence AfterWithUnexpectedInventoryItem(int cashAfter, string container)
        {
            var baseline = After(cashAfter, "present", movedCount: refs.Length);
            var allItems = new List<PurchaseItemEvidence>(baseline.Items)
            {
                new PurchaseItemEvidence("callback-added-item", container, 1, container == "player",
                    identityKey: "callback-added-object")
            };
            return Evidence("cmd-1", "complete", 11, cashAfter, "present", customer,
                Array.Empty<string>(), Array.Empty<int>(), refs, targetInventory, sourceInventory, fingerprint,
                allItems);
        }

        public PurchaseEvidence AfterWithChangedTargetFacts(int cashAfter)
        {
            var baseline = After(cashAfter, "present", movedCount: refs.Length);
            var alteredItems = new List<PurchaseItemEvidence>();
            foreach (var item in baseline.Items)
            {
                if (item.ItemRef == refs[0])
                {
                    var altered = CapacityItem(item.ItemRef, targetInventory, "toilet_paper", 0, owned: true);
                    alteredItems.Add(new PurchaseItemEvidence(item.ItemRef, "player", item.Units, true,
                        capacityFacts: altered, identityKey: item.IdentityKey));
                }
                else alteredItems.Add(item);
            }
            return Evidence("cmd-1", "complete", 11, cashAfter, "present", this.customer,
                Array.Empty<string>(), Array.Empty<int>(), refs, targetInventory, sourceInventory, fingerprint,
                alteredItems);
        }

        public PurchaseEvidence AfterWithChangedSourceRef(int cashAfter)
        {
            var baseline = After(cashAfter, "present", movedCount: refs.Length);
            return Evidence("cmd-1", "complete", 11, cashAfter, "present", this.customer,
                Array.Empty<string>(), Array.Empty<int>(), refs, targetInventory, "other-back-inv", fingerprint,
                baseline.Items);
        }

        public PurchaseEvidence BeforeWithExistingInventory()
        {
            var existingPlayer = CapacityItem("existing-player", targetInventory, "common_medical", 5, owned: true);
            var existingBack = CapacityItem("existing-back", sourceInventory, "common_medical", refs.Length + 1, owned: true);
            var sourceFacts = new List<CapacityItemSnapshot>(CapacitySnapshot.SourceDirectItems) { existingBack };
            var capacity = new CapacitySnapshot(CapacitySnapshot.WorldEpoch, CapacitySnapshot.WorldRevision,
                CapacitySnapshot.TargetRef, CapacitySnapshot.SourceRef, CapacitySnapshot.BuildProfile,
                CapacitySnapshot.TargetProfile, CapacitySnapshot.TargetCallbackProfile, CapacitySnapshot.SourceCallbackProfile,
                CapacitySnapshot.TargetInsertLocked, CapacitySnapshot.ClientOnItemSoldCallbackPresent,
                CapacitySnapshot.TargetSnapshotComplete, CapacitySnapshot.SourceSnapshotComplete,
                CapacitySnapshot.GridWidth, CapacitySnapshot.GridHeight, CapacitySnapshot.AllowedCells,
                new[] { existingPlayer }, sourceFacts, refs, CapacitySnapshot.SelectionProfile);
            var proof = CapacityProof.Evaluate(capacity);
            var allItems = new List<PurchaseItemEvidence>(BeforeItems(refs, capacity))
            {
                new PurchaseItemEvidence("existing-back", "back", 1, true,
                    capacityFacts: existingBack, identityKey: "existing-back-object")
            };
            return Evidence("cmd-1", "complete", 10, cash, "present", customer,
                refs, prices, refs, targetInventory, sourceInventory, fingerprint, allItems,
                capacitySnapshot: capacity, capacityProof: proof);
        }

        public PurchaseEvidence AfterWithExistingInventory(int cashAfter, bool omitPlayer = false,
            bool changeBackUnits = false)
        {
            var baseline = After(cashAfter, "present", movedCount: refs.Length);
            var allItems = new List<PurchaseItemEvidence>(baseline.Items);
            var existingBack = CapacityItem("existing-back", sourceInventory, "common_medical", refs.Length + 1,
                owned: true, units: changeBackUnits ? 2 : 1);
            allItems.Add(new PurchaseItemEvidence("existing-back", "back", existingBack.UnitCount, true,
                capacityFacts: existingBack, identityKey: "existing-back-object"));
            if (!omitPlayer)
            {
                var existingPlayer = CapacityItem("existing-player", targetInventory, "common_medical", 5, owned: true);
                allItems.Add(new PurchaseItemEvidence("existing-player", "player", 1, true,
                    capacityFacts: existingPlayer, identityKey: "player-object-" + refs.Length));
            }
            return Evidence("cmd-1", "complete", 11, cashAfter, "present", customer,
                Array.Empty<string>(), Array.Empty<int>(), refs, targetInventory, sourceInventory, fingerprint,
                allItems);
        }

        public PurchaseEvidence WithEvidence(bool? containersComplete = null, int? revision = null,
            CapacitySnapshot capacitySnapshot = null, CapacityProofResult capacityProof = null,
            bool removeProof = false)
        {
            var snap = capacitySnapshot ?? (revision.HasValue ? Capacity(refs, targetInventory, sourceInventory, revision.Value) : CapacitySnapshot);
            var proof = removeProof ? null : capacityProof ?? CapacityProof.Evaluate(snap);
            return Evidence("cmd-1", "complete", revision ?? 10, cash, "present", customer,
                refs, prices, refs, targetInventory, sourceInventory, fingerprint, BeforeItems(refs, snap),
                containersComplete ?? true, snap, proof);
        }

        public PurchaseEvidence WithAlteredSourceFact()
        {
            var altered = CapacityItem("item-a", sourceInventory, "toilet_paper", 0);
            return Evidence("cmd-1", "complete", 10, cash, "present", customer,
                refs, prices, refs, targetInventory, sourceInventory, fingerprint,
                BeforeItems(refs, CapacitySnapshot, altered), capacitySnapshot: CapacitySnapshot, capacityProof: Proof);
        }

        public PurchaseEvidence WithForgedSourceFingerprint()
        {
            var items = new List<PurchaseItemEvidence>(BeforeItems(refs, CapacitySnapshot));
            items[0] = new PurchaseItemEvidence("item-a", "back", 1, false, "forged-fingerprint",
                CapacitySnapshot.SourceDirectItems[0]);
            return Evidence("cmd-1", "complete", 10, cash, "present", customer,
                refs, prices, refs, targetInventory, sourceInventory, fingerprint, items,
                capacitySnapshot: CapacitySnapshot, capacityProof: Proof);
        }

        public PurchaseEvidence WithExtraNonOwnedBackItem()
        {
            var extra = CapacityItem("hidden-item", sourceInventory, "common_medical", refs.Length);
            var sourceItems = new List<CapacityItemSnapshot>(CapacitySnapshot.SourceDirectItems) { extra };
            var snapshot = new CapacitySnapshot(CapacitySnapshot.WorldEpoch, CapacitySnapshot.WorldRevision,
                CapacitySnapshot.TargetRef, CapacitySnapshot.SourceRef, CapacitySnapshot.BuildProfile,
                CapacitySnapshot.TargetProfile, CapacitySnapshot.TargetCallbackProfile, CapacitySnapshot.SourceCallbackProfile,
                CapacitySnapshot.TargetInsertLocked, CapacitySnapshot.ClientOnItemSoldCallbackPresent,
                CapacitySnapshot.TargetSnapshotComplete, CapacitySnapshot.SourceSnapshotComplete,
                CapacitySnapshot.GridWidth, CapacitySnapshot.GridHeight, CapacitySnapshot.AllowedCells,
                CapacitySnapshot.TargetDirectItems, sourceItems, refs, CapacitySnapshot.SelectionProfile);
            var allItems = new List<PurchaseItemEvidence>(BeforeItems(refs, snapshot));
            allItems.Insert(refs.Length, new PurchaseItemEvidence("hidden-item", "back", 1, false,
                capacityFacts: extra, identityKey: "purchase-hidden-object"));
            return Evidence("cmd-1", "complete", 10, cash, "present", customer,
                refs, prices, refs, targetInventory, sourceInventory, fingerprint, allItems,
                capacitySnapshot: snapshot, capacityProof: CapacityProof.Evaluate(snapshot));
        }

        public PurchaseEvidence WithDisabledMultiBuyExtraStock()
        {
            var sourceItems = new List<CapacityItemSnapshot>(CapacitySnapshot.SourceDirectItems)
            {
                CapacityItem("hidden-item", sourceInventory, "common_medical", refs.Length)
            };
            var snapshot = new CapacitySnapshot(CapacitySnapshot.WorldEpoch, CapacitySnapshot.WorldRevision,
                CapacitySnapshot.TargetRef, CapacitySnapshot.SourceRef, CapacitySnapshot.BuildProfile,
                CapacitySnapshot.TargetProfile, CapacitySnapshot.TargetCallbackProfile, CapacitySnapshot.SourceCallbackProfile,
                CapacitySnapshot.TargetInsertLocked, CapacitySnapshot.ClientOnItemSoldCallbackPresent,
                CapacitySnapshot.TargetSnapshotComplete, CapacitySnapshot.SourceSnapshotComplete,
                CapacitySnapshot.GridWidth, CapacitySnapshot.GridHeight, CapacitySnapshot.AllowedCells,
                CapacitySnapshot.TargetDirectItems, sourceItems, new[] { refs[0] },
                CapacitySelectionProfile.ExplicitBackInventoryItems);
            var allRefs = new List<string>(refs) { "hidden-item" };
            return Evidence("cmd-1", "complete", 10, cash, "present", customer,
                new[] { refs[0] }, new[] { prices[0] }, new[] { refs[0] }, targetInventory,
                sourceInventory, fingerprint, BeforeItems(allRefs.ToArray(), snapshot),
                capacitySnapshot: snapshot, capacityProof: CapacityProof.Evaluate(snapshot), multiBuyEnabled: false);
        }

        public PurchaseEvidence EvidenceWithOrder(IEnumerable<string> order)
        {
            return Evidence("cmd-1", "complete", 10, cash, "present", customer,
                order, prices, refs, targetInventory, sourceInventory,
                fingerprint, BeforeItems(refs, CapacitySnapshot), capacitySnapshot: CapacitySnapshot, capacityProof: Proof);
        }

        public PurchaseEvidence EvidenceWithCash(int changedCash) =>
            Evidence("cmd-1", "complete", 10, changedCash, "present", customer,
                refs, prices, refs, targetInventory, sourceInventory, fingerprint,
                BeforeItems(refs, CapacitySnapshot), capacitySnapshot: CapacitySnapshot, capacityProof: Proof);

        public CapacitySnapshot CapacityAtRevision(int revision) =>
            Capacity(refs, targetInventory, sourceInventory, revision);

        private static PurchaseItemEvidence[] BeforeItems(string[] refs, CapacitySnapshot capacity,
            CapacityItemSnapshot alteredItem = null)
        {
            var items = new List<PurchaseItemEvidence>();
            var sourceFacts = new Dictionary<string, CapacityItemSnapshot>(StringComparer.Ordinal);
            foreach (var item in capacity.SourceDirectItems) sourceFacts.Add(item.ItemRef, item);
            foreach (var itemRef in refs)
            {
                var facts = itemRef == "item-a" && alteredItem != null ? alteredItem : sourceFacts[itemRef];
                items.Add(new PurchaseItemEvidence(itemRef, "back", 1, false, capacityFacts: facts,
                    identityKey: "purchase-object-" + Array.IndexOf(refs, itemRef)));
            }
            foreach (var item in capacity.TargetDirectItems)
                items.Add(new PurchaseItemEvidence(item.ItemRef, "player", item.UnitCount, item.IsOwned,
                    capacityFacts: item, identityKey: "player-object-" + items.Count));
            items.Add(new PurchaseItemEvidence("unrelated", "front", 1, false, "stable-state"));
            return items.ToArray();
        }

        private static PurchaseEvidence Evidence(string commandId, string status, int revision, int cash,
            string customerPresence, string customerRef, IEnumerable<string> purchaseRefs, int[] quoteAmounts,
            string[] originalRefs, string target, string source, string fingerprint,
            IEnumerable<PurchaseItemEvidence> allItems, bool containersComplete = true,
            CapacitySnapshot capacitySnapshot = null, CapacityProofResult capacityProof = null,
            string worldEpoch = "world-1", bool multiBuyEnabled = true)
        {
            var quoteRows = new List<PurchaseQuoteRowEvidence>();
            int? total = null;
            for (int i = 0; i < quoteAmounts.Length; i++)
            {
                quoteRows.Add(new PurchaseQuoteRowEvidence(originalRefs[i], quoteAmounts[i]));
                total = (total ?? 0) + quoteAmounts[i];
            }
            return new PurchaseEvidence(commandId, status, worldEpoch, "InventoryScene", revision,
                worldEpoch + ":" + revision, "OPEN", cash, customerPresence, customerRef,
                customerPresence == "present" ? true : (bool?)null,
                customerPresence == "present" ? "sell" : "unknown",
                customerPresence == "present" ? "trading" : "absent",
                "consistent", "verified", Array.Empty<string>(), "offer-1", target, source, fingerprint,
                purchaseRefs, quoteAmounts.Length == 0 ? "unavailable" : "complete",
                quoteAmounts.Length == 0 ? "unknown" : "normal", total, quoteRows, allItems,
                containersComplete, multiBuyEnabled: multiBuyEnabled, capacitySnapshot: capacitySnapshot,
                capacityProof: capacityProof, quoteBindingVerified: true, nativePurchaseReady: true);
        }

        private static CapacitySnapshot Capacity(string[] refs, string target, string source, int revision)
        {
            var sourceItems = new List<CapacityItemSnapshot>();
            for (int i = 0; i < refs.Length; i++)
                sourceItems.Add(CapacityItem(refs[i], source, "common_medical", 0));
            return new CapacitySnapshot("world-1", revision, target, source,
                CapacityBuildProfile.Vanilla26337366NoMods,
                CapacityTargetProfile.EmporiumMainInventoryInHours,
                CapacityInventoryCallbackProfile.VanillaEmporiumMainInventory,
                CapacityInventoryCallbackProfile.VanillaEmporiumBackInventory,
                CapacityFact.No, CapacityFact.No, true, true, 24, 10,
                Filled(240, true), Array.Empty<CapacityItemSnapshot>(), sourceItems, refs,
                CapacitySelectionProfile.AllNonOwnedBackInventoryItems);
        }

        private static CapacityItemSnapshot CapacityItem(string itemRef, string parent, string identifier, int x,
            bool owned = false, int units = 1) =>
            new CapacityItemSnapshot(itemRef, identifier, parent, true, units, owned,
                CapacityItemCallbackProfile.VanillaCommonMedicalOrToiletPaper,
                CapacityFeatureProfile.Empty, CapacityNestedProfile.EmptyByNativeFactory,
                CapacityFact.Yes, new CapacityShapeSnapshot(true, 1, 1, x, 0, false, 0, new[] { true }),
                occupiedGridCellIndexes: new[] { x }, slotSearchPassthroughAbsent: CapacityFact.Yes);

        private static bool[] Filled(int count, bool value)
        {
            var cells = new bool[count];
            for (int i = 0; i < count; i++) cells[i] = value;
            return cells;
        }

        private static int Sum(IEnumerable<int> values)
        {
            int total = 0;
            foreach (int value in values) total = checked(total + value);
            return total;
        }
    }
}
