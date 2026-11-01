using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PawnPilot
{
    // Local execution evidence. These types are deliberately distinct from wire DTOs in
    // Runtime/BusinessState.cs. They contain copied scalar facts only; no Unity objects or
    // mutable game collections cross the verifier boundary.
    public sealed class PurchaseRequest
    {
        private readonly ReadOnlyCollection<string> itemRefs;

        public string CommandId { get; }
        public string WorldEpoch { get; }
        public string ExpectedVersion { get; }
        public int ExpectedRevision { get; }
        public string CustomerRef { get; }
        public string OfferRef { get; }
        public string CollectionFingerprint { get; }
        public string Mode { get; }
        public int Amount { get; }
        public int MaxSpend { get; }
        public IReadOnlyList<string> ItemRefs => itemRefs;

        public PurchaseRequest(string commandId, string worldEpoch, string expectedVersion, int expectedRevision,
            string customerRef, string offerRef, IEnumerable<string> itemRefs, string collectionFingerprint,
            string mode, int amount, int maxSpend)
        {
            CommandId = commandId;
            WorldEpoch = worldEpoch;
            ExpectedVersion = expectedVersion;
            ExpectedRevision = expectedRevision;
            CustomerRef = customerRef;
            OfferRef = offerRef;
            this.itemRefs = Array.AsReadOnly(Copy(itemRefs));
            CollectionFingerprint = collectionFingerprint;
            Mode = mode;
            Amount = amount;
            MaxSpend = maxSpend;
        }

        private static string[] Copy(IEnumerable<string> source)
        {
            if (source == null) return Array.Empty<string>();
            var values = new List<string>();
            foreach (string value in source) values.Add(value);
            return values.ToArray();
        }
    }

    public sealed class PurchaseItemEvidence
    {
        public string ItemRef { get; }
        public string Container { get; }
        public int Units { get; }
        public bool Owned { get; }
        public CapacityItemSnapshot CapacityFacts { get; }
        // Per-command opaque identity token. Unlike ItemRef, this remains stable across an
        // ownership/container projection change, but it is never serialized to the wire.
        public string IdentityKey { get; }
        // A local fingerprint over other directly-observed mutable item facts. It must be
        // stable for non-target items and is intentionally not used to price an item.
        public string StateFingerprint { get; }

        public PurchaseItemEvidence(string itemRef, string container, int units, bool owned,
            string stateFingerprint = null, CapacityItemSnapshot capacityFacts = null, string identityKey = null)
        {
            ItemRef = itemRef;
            Container = container;
            Units = units;
            Owned = owned;
            CapacityFacts = capacityFacts;
            IdentityKey = identityKey;
            StateFingerprint = stateFingerprint ?? StableFingerprint(capacityFacts);
        }

        // Stable item state used by the capacity proof and settlement verifier. It includes
        // identity, callback/feature/nesting profiles and shape, but excludes ownership, parent
        // inventory, grid coordinates, and transient quote/pricing bookkeeping expected to change
        // during a successful move. Quote rows and the exact cash delta are checked separately.
        public static string StableFingerprint(CapacityItemSnapshot item)
        {
            if (item == null) return null;
            var builder = new StringBuilder(256);
            Append(builder, item.ItemRef);
            Append(builder, item.Identifier);
            Append(builder, item.IsDirectInventoryChild);
            Append(builder, item.UnitCount);
            Append(builder, (int)item.CallbackProfile);
            Append(builder, (int)item.FeatureProfile);
            Append(builder, (int)item.NestedProfile);
            Append(builder, (int)item.StackCallbackAbsent);
            Append(builder, (int)item.SlotSearchPassthroughAbsent);
            var shape = item.Shape;
            if (shape == null) Append(builder, (string)null);
            else
            {
                Append(builder, shape.Complete);
                Append(builder, shape.Width);
                Append(builder, shape.Height);
                Append(builder, shape.Flipped);
                Append(builder, shape.Orientation);
                Append(builder, shape.OccupiedCells.Count);
                foreach (bool occupied in shape.OccupiedCells) Append(builder, occupied);
            }
            Append(builder, item.OccupiedGridCellsComplete);
            Append(builder, item.OccupiedGridCellIndexes.Count);
            foreach (int cell in item.OccupiedGridCellIndexes) Append(builder, cell);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static void Append(StringBuilder builder, string value)
        {
            string text = value ?? "";
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('|');
        }

        private static void Append(StringBuilder builder, int value) =>
            builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append('|');

        private static void Append(StringBuilder builder, bool value) => builder.Append(value ? '1' : '0').Append('|');
    }

    public sealed class PurchaseQuoteRowEvidence
    {
        public string ItemRef { get; }
        public int? Amount { get; }

        public PurchaseQuoteRowEvidence(string itemRef, int? amount)
        {
            ItemRef = itemRef;
            Amount = amount;
        }
    }

    public sealed class PurchaseEvidence
    {
        private readonly ReadOnlyCollection<string> purchaseListOrderRefs;
        private readonly ReadOnlyCollection<PurchaseItemEvidence> items;
        private readonly ReadOnlyCollection<PurchaseQuoteRowEvidence> quoteRows;
        private readonly ReadOnlyCollection<string> blockers;

        public string CommandId { get; }
        public string Status { get; }
        public string WorldEpoch { get; }
        public string Scene { get; }
        public int Revision { get; }
        public string Version { get; }
        public string StorePhase { get; }
        public int Cash { get; }
        public string CustomerPresence { get; }
        public string CustomerRef { get; }
        public bool? CustomerArrived { get; }
        public string CustomerIntent { get; }
        public string CustomerPhase { get; }
        public string UiStatus { get; }
        public string UiCoverage { get; }
        public string OfferRef { get; }
        // Opaque identity of the receiving main player inventory, distinct from offer/item refs.
        public string TargetInventoryRef { get; }
        public string SourceInventoryRef { get; }
        public string CollectionFingerprint { get; }
        public string QuoteStatus { get; }
        public string QuoteMode { get; }
        public int? DisplayedTotal { get; }
        public bool MultiBuyEnabled { get; }
        public bool ContainersComplete { get; }
        public bool QuoteBindingVerified { get; }
        public bool NativePurchaseReady { get; }
        public CapacitySnapshot CapacitySnapshot { get; }
        public CapacityProofResult CapacityProof { get; }
        public IReadOnlyList<string> PurchaseListOrderRefs => purchaseListOrderRefs;
        public IReadOnlyList<PurchaseItemEvidence> Items => items;
        public IReadOnlyList<PurchaseQuoteRowEvidence> QuoteRows => quoteRows;
        public IReadOnlyList<string> Blockers => blockers;

        // This is derived from the immutable capacity proof and its exact binding. A raw
        // caller-supplied bool cannot claim that arbitrary items fit.
        public bool PassedCapacity => CapacityProof != null &&
            CapacityProof.Status == CapacityProofStatus.Sufficient &&
            CapacitySnapshot != null && CapacityProof.IsBoundToSnapshot(CapacitySnapshot) &&
            CapacitySnapshot.TargetRef == TargetInventoryRef && CapacitySnapshot.SourceRef == SourceInventoryRef &&
            CapacitySnapshot.WorldEpoch == WorldEpoch && CapacitySnapshot.WorldRevision == Revision &&
            SameOrder(CapacitySnapshot.PurchaseListOrderRefs, purchaseListOrderRefs) &&
            CapacityFactsMatchEvidence();

        public PurchaseEvidence(string commandId, string status, string worldEpoch, string scene,
            int revision, string version, string storePhase, int cash, string customerPresence,
            string customerRef, bool? customerArrived, string customerIntent, string customerPhase,
            string uiStatus, string uiCoverage, IEnumerable<string> blockers, string offerRef,
            string targetInventoryRef, string sourceInventoryRef,
            string collectionFingerprint, IEnumerable<string> purchaseListOrderRefs,
            string quoteStatus, string quoteMode, int? displayedTotal,
            IEnumerable<PurchaseQuoteRowEvidence> quoteRows, IEnumerable<PurchaseItemEvidence> items,
            bool containersComplete, bool multiBuyEnabled, CapacitySnapshot capacitySnapshot = null,
            CapacityProofResult capacityProof = null, bool quoteBindingVerified = false,
            bool nativePurchaseReady = false)
        {
            CommandId = commandId;
            Status = status;
            WorldEpoch = worldEpoch;
            Scene = scene;
            Revision = revision;
            Version = version;
            StorePhase = storePhase;
            Cash = cash;
            CustomerPresence = customerPresence;
            CustomerRef = customerRef;
            CustomerArrived = customerArrived;
            CustomerIntent = customerIntent;
            CustomerPhase = customerPhase;
            UiStatus = uiStatus;
            UiCoverage = uiCoverage;
            OfferRef = offerRef;
            TargetInventoryRef = targetInventoryRef;
            SourceInventoryRef = sourceInventoryRef;
            CollectionFingerprint = collectionFingerprint;
            QuoteStatus = quoteStatus;
            QuoteMode = quoteMode;
            DisplayedTotal = displayedTotal;
            MultiBuyEnabled = multiBuyEnabled;
            ContainersComplete = containersComplete;
            CapacitySnapshot = capacitySnapshot;
            CapacityProof = capacityProof;
            QuoteBindingVerified = quoteBindingVerified;
            NativePurchaseReady = nativePurchaseReady;
            this.purchaseListOrderRefs = Array.AsReadOnly(Copy(purchaseListOrderRefs));
            this.items = Array.AsReadOnly(Copy(items));
            this.quoteRows = Array.AsReadOnly(Copy(quoteRows));
            this.blockers = Array.AsReadOnly(Copy(blockers));
        }

        private static T[] Copy<T>(IEnumerable<T> source)
        {
            if (source == null) return Array.Empty<T>();
            var values = new List<T>();
            foreach (T value in source) values.Add(value);
            return values.ToArray();
        }

        private static bool SameOrder(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private bool CapacityFactsMatchEvidence()
        {
            var sourceItems = new Dictionary<string, CapacityItemSnapshot>(StringComparer.Ordinal);
            var targetItems = new Dictionary<string, CapacityItemSnapshot>(StringComparer.Ordinal);
            foreach (var item in CapacitySnapshot.SourceDirectItems)
                if (item == null || string.IsNullOrEmpty(item.ItemRef) || sourceItems.ContainsKey(item.ItemRef)) return false;
                else sourceItems.Add(item.ItemRef, item);
            foreach (var item in CapacitySnapshot.TargetDirectItems)
                if (item == null || string.IsNullOrEmpty(item.ItemRef) || targetItems.ContainsKey(item.ItemRef)) return false;
                else targetItems.Add(item.ItemRef, item);

            var observedSource = new List<string>();
            var observedTarget = new List<string>();
            foreach (var item in items)
            {
                if (item == null) return false;
                if (item.Container == "back")
                {
                    if (!sourceItems.TryGetValue(item.ItemRef, out var sourceFact) ||
                        !SameCapacityFacts(item.CapacityFacts, sourceFact) || !sourceFact.IsDirectInventoryChild ||
                        sourceFact.ParentInventoryRef != SourceInventoryRef || sourceFact.UnitCount != item.Units ||
                        sourceFact.IsOwned != item.Owned ||
                        item.StateFingerprint != PurchaseItemEvidence.StableFingerprint(sourceFact)) return false;
                    observedSource.Add(item.ItemRef);
                }
                else if (item.Container == "player")
                {
                    if (!targetItems.TryGetValue(item.ItemRef, out var targetFact) ||
                        !SameCapacityFacts(item.CapacityFacts, targetFact) || !targetFact.IsDirectInventoryChild ||
                        targetFact.ParentInventoryRef != TargetInventoryRef || targetFact.UnitCount != item.Units ||
                        targetFact.IsOwned != item.Owned ||
                        item.StateFingerprint != PurchaseItemEvidence.StableFingerprint(targetFact)) return false;
                    observedTarget.Add(item.ItemRef);
                }
                else if (item.CapacityFacts != null) return false;
            }
            if (observedSource.Count != sourceItems.Count || observedTarget.Count != targetItems.Count) return false;

            var expectedSourceOrder = new List<string>(CapacitySnapshot.SourceDirectItems.Count);
            foreach (var item in CapacitySnapshot.SourceDirectItems) expectedSourceOrder.Add(item.ItemRef);
            var expectedTargetOrder = new List<string>(CapacitySnapshot.TargetDirectItems.Count);
            foreach (var item in CapacitySnapshot.TargetDirectItems) expectedTargetOrder.Add(item.ItemRef);
            if (!SameOrder(observedSource, expectedSourceOrder) || !SameOrder(observedTarget, expectedTargetOrder)) return false;

            var nonOwnedSourceOrder = new List<string>();
            foreach (var itemRef in observedSource)
                if (!sourceItems[itemRef].IsOwned) nonOwnedSourceOrder.Add(itemRef);
            if (MultiBuyEnabled) return SameOrder(nonOwnedSourceOrder, purchaseListOrderRefs);
            if (purchaseListOrderRefs.Count != 1) return false;
            return nonOwnedSourceOrder.Contains(purchaseListOrderRefs[0]);
        }

        private static bool SameCapacityFacts(CapacityItemSnapshot left, CapacityItemSnapshot right)
        {
            if (left == null || right == null || left.ItemRef != right.ItemRef || left.Identifier != right.Identifier ||
                left.ParentInventoryRef != right.ParentInventoryRef || left.IsDirectInventoryChild != right.IsDirectInventoryChild ||
                left.UnitCount != right.UnitCount || left.IsOwned != right.IsOwned ||
                left.CallbackProfile != right.CallbackProfile || left.FeatureProfile != right.FeatureProfile ||
                left.NestedProfile != right.NestedProfile || left.StackCallbackAbsent != right.StackCallbackAbsent ||
                left.SlotSearchPassthroughAbsent != right.SlotSearchPassthroughAbsent ||
                left.OccupiedGridCellsComplete != right.OccupiedGridCellsComplete ||
                left.OccupiedGridCellIndexes.Count != right.OccupiedGridCellIndexes.Count ||
                left.Shape == null || right.Shape == null) return false;
            var a = left.Shape; var b = right.Shape;
            if (a.Complete != b.Complete || a.Width != b.Width || a.Height != b.Height ||
                a.PositionX != b.PositionX || a.PositionY != b.PositionY || a.Flipped != b.Flipped ||
                a.Orientation != b.Orientation || a.OccupiedCells.Count != b.OccupiedCells.Count) return false;
            for (int i = 0; i < a.OccupiedCells.Count; i++)
                if (a.OccupiedCells[i] != b.OccupiedCells[i]) return false;
            for (int i = 0; i < left.OccupiedGridCellIndexes.Count; i++)
                if (left.OccupiedGridCellIndexes[i] != right.OccupiedGridCellIndexes[i]) return false;
            return true;
        }
    }

    public sealed class PurchasePreflightDecision
    {
        private int invocationClaimed;
        public bool Accepted { get; }
        public string Reason { get; }
        public string CommandId { get; }
        internal PurchaseRequest Request { get; }
        internal PurchaseEvidence Evidence { get; }

        private PurchasePreflightDecision(bool accepted, string reason, PurchaseRequest request,
            PurchaseEvidence evidence)
        {
            Accepted = accepted;
            Reason = reason;
            CommandId = request?.CommandId;
            Request = request;
            Evidence = evidence;
        }

        internal bool Binds(PurchaseRequest request, PurchaseEvidence evidence) => Accepted &&
            ReferenceEquals(Request, request) && ReferenceEquals(Evidence, evidence);

        internal bool TryClaimInvocation(PurchaseRequest request) => Accepted &&
            ReferenceEquals(Request, request) && Interlocked.CompareExchange(ref invocationClaimed, 1, 0) == 0;

        internal static PurchasePreflightDecision Allow(PurchaseRequest request, PurchaseEvidence evidence) =>
            new PurchasePreflightDecision(true, "preflight_passed", request, evidence);
        internal static PurchasePreflightDecision Reject(string reason, PurchaseRequest request) =>
            new PurchasePreflightDecision(false, reason ?? "preflight_rejected", request, null);
    }

    public static class PurchaseVerifier
    {
        public const int MaxItems = 64;
        public const int MaxTextLength = 256;

        public static PurchasePreflightDecision ValidatePreflight(PurchaseRequest request, PurchaseEvidence before)
        {
            string reason = ValidateRequest(request);
            if (reason != null) return PurchasePreflightDecision.Reject(reason, request);
            reason = ValidateCompleteEvidence(before, requirePreflightState: true);
            if (reason != null) return PurchasePreflightDecision.Reject(reason, request);
            if (before.CommandId != request.CommandId) return Reject("command_id_mismatch", request);
            if (before.WorldEpoch != request.WorldEpoch || before.Version != request.ExpectedVersion ||
                before.Revision != request.ExpectedRevision) return Reject("state_ref_mismatch", request);
            if (before.CustomerPresence != "present" || before.CustomerRef != request.CustomerRef ||
                before.CustomerArrived != true || before.CustomerIntent != "sell")
                return Reject("customer_ref_mismatch", request);
            if (before.StorePhase != "OPEN" || before.CustomerPhase != "trading")
                return Reject("business_not_trading", request);
            if (!before.NativePurchaseReady) return Reject("native_purchase_state_unverified", request);
            if (before.OfferRef != request.OfferRef || before.CollectionFingerprint != request.CollectionFingerprint)
                return Reject("offer_or_collection_ref_mismatch", request);
            if (before.QuoteStatus != "complete" || before.QuoteMode != "normal" || !before.DisplayedTotal.HasValue)
                return Reject("normal_quote_unverified", request);
            if (before.DisplayedTotal.Value != request.Amount || request.Amount > request.MaxSpend ||
                request.MaxSpend > before.Cash)
                return Reject("spend_limit_or_amount_mismatch", request);
            if (before.Cash < request.Amount) return Reject("insufficient_cash", request);
            if (before.PurchaseListOrderRefs.Count > 1 && !before.MultiBuyEnabled)
                return Reject("multibuy_disabled", request);
            if (!SequenceEqual(request.ItemRefs, before.PurchaseListOrderRefs))
                return Reject("purchase_collection_mismatch", request);
            if (!QuoteBindsCollection(before)) return Reject("quote_binding_unverified", request);
            if (!before.PassedCapacity) return Reject("capacity_unproven", request);
            return PurchasePreflightDecision.Allow(request, before);
        }

        public static VerificationDecision Verify(PurchaseRequest request, PurchaseEvidence before,
            PurchaseEvidence after, PurchaseInvocationLatch invocation)
        {
            if (invocation == null || !invocation.Binds(request, before))
                return VerificationDecision.Uncertain("invocation_evidence_missing");
            if (!invocation.InvocationStarted || invocation.InvocationCount != 1 ||
                !invocation.InvocationReturned || invocation.IsUncertain)
                return VerificationDecision.Uncertain("invocation_not_cleanly_returned");
            var preflight = ValidatePreflight(request, before);
            if (!preflight.Accepted) return VerificationDecision.Uncertain("preflight_evidence_invalid:" + preflight.Reason);
            if (after == null || after.Status != "complete" || !after.ContainersComplete)
                return VerificationDecision.Uncertain("post_evidence_incomplete");
            if (after.CommandId != request.CommandId) return VerificationDecision.Uncertain("command_id_mismatch");
            if (after.WorldEpoch != before.WorldEpoch || after.Scene != before.Scene)
                return VerificationDecision.Uncertain("world_changed_after_invocation");
            if (after.TargetInventoryRef != before.TargetInventoryRef || after.SourceInventoryRef != before.SourceInventoryRef)
                return VerificationDecision.Uncertain("inventory_target_changed_after_invocation");
            if (after.Revision <= before.Revision) return VerificationDecision.Uncertain("post_revision_not_advanced");

            long expectedCash = (long)before.Cash - request.Amount;
            if (expectedCash < int.MinValue || expectedCash > int.MaxValue || after.Cash != expectedCash)
                return VerificationDecision.Uncertain("cash_delta_mismatch");

            string inventoryReason = ValidateInventoryTransition(request, before, after);
            if (inventoryReason != null) return VerificationDecision.Uncertain(inventoryReason);

            return new VerificationDecision
            {
                outcome = "succeeded",
                reason = "purchase_settled"
            };
        }

        private static string ValidateRequest(PurchaseRequest request)
        {
            if (request == null) return "request_missing";
            if (!ValidRef(request.CommandId) || !ValidRef(request.WorldEpoch) ||
                string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 256 ||
                request.ExpectedRevision < 0 || !ValidRef(request.CustomerRef) || !ValidRef(request.OfferRef) ||
                !ValidRef(request.CollectionFingerprint) ||
                request.Mode != "normal" || request.Amount < 0 || request.MaxSpend < 0 ||
                request.Amount > request.MaxSpend || request.ItemRefs.Count < 1 || request.ItemRefs.Count > MaxItems)
                return "invalid_request";
            if (request.ExpectedVersion != request.WorldEpoch + ":" + request.ExpectedRevision.ToString(CultureInfo.InvariantCulture))
                return "invalid_request_version";
            return ValidateRefList(request.ItemRefs) ? null : "invalid_request_items";
        }

        private static string ValidateCompleteEvidence(PurchaseEvidence evidence, bool requirePreflightState)
        {
            if (evidence == null || evidence.Status != "complete" || !evidence.ContainersComplete)
                return "evidence_incomplete";
            if (!ValidRef(evidence.CommandId) || !ValidRef(evidence.WorldEpoch) || !ValidRef(evidence.Scene) ||
                evidence.Revision < 0 || evidence.Version != evidence.WorldEpoch + ":" + evidence.Revision.ToString(CultureInfo.InvariantCulture) ||
                (requirePreflightState && (evidence.StorePhase == null || evidence.StorePhase.Length > MaxTextLength)) || evidence.Cash < 0 ||
                (requirePreflightState && evidence.PurchaseListOrderRefs.Count < 1) || evidence.PurchaseListOrderRefs.Count > MaxItems ||
                !ValidateRefList(evidence.PurchaseListOrderRefs) ||
                (requirePreflightState && (!ValidRef(evidence.OfferRef) || !ValidRef(evidence.CollectionFingerprint))) ||
                !ValidRef(evidence.TargetInventoryRef) || !ValidRef(evidence.SourceInventoryRef) ||
                (requirePreflightState && !ValidRef(evidence.CollectionFingerprint))) return "evidence_invalid";
            if (evidence.Items.Count > MaxItems || evidence.QuoteRows.Count > MaxItems || evidence.Blockers.Count > 16)
                return "evidence_limit_exceeded";
            if (requirePreflightState && !ValidRef(evidence.CustomerRef)) return "evidence_invalid_customer";
            if (requirePreflightState && evidence.Cash < 0) return "evidence_invalid_cash";
            var items = new Dictionary<string, PurchaseItemEvidence>(StringComparer.Ordinal);
            var nativeIdentities = requirePreflightState ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (var item in evidence.Items)
            {
                if (item == null || !ValidRef(item.ItemRef) || !ValidContainer(item.Container) || item.Units < 1 ||
                    (requirePreflightState && (string.IsNullOrWhiteSpace(item.StateFingerprint) || item.StateFingerprint.Length > MaxTextLength)) ||
                    items.ContainsKey(item.ItemRef))
                    return "inventory_evidence_invalid";
                if (requirePreflightState && (item.Container == "back" || item.Container == "player") && item.CapacityFacts == null)
                    return "inventory_capacity_facts_missing";
                if (item.CapacityFacts != null &&
                    (item.CapacityFacts.ItemRef != item.ItemRef || item.CapacityFacts.UnitCount != item.Units ||
                     item.CapacityFacts.IsOwned != item.Owned ||
                     item.StateFingerprint != PurchaseItemEvidence.StableFingerprint(item.CapacityFacts) ||
                     item.CapacityFacts.ParentInventoryRef != (item.Container == "back" ? evidence.SourceInventoryRef :
                         item.Container == "player" ? evidence.TargetInventoryRef : null)))
                    return "inventory_capacity_fact_mismatch";
                if (requirePreflightState && (item.Container == "back" || item.Container == "player") &&
                    (!ValidRef(item.IdentityKey) || !nativeIdentities.Add(item.IdentityKey)))
                    return "inventory_identity_invalid";
                items.Add(item.ItemRef, item);
            }
            foreach (string itemRef in evidence.PurchaseListOrderRefs)
            {
                if (!items.TryGetValue(itemRef, out var item) || item.Container != "back" || item.Owned)
                    return "purchase_item_source_mismatch";
                if (requirePreflightState && !ValidRef(item.IdentityKey)) return "purchase_item_identity_missing";
            }
            var observedNonOwnedBackOrder = new List<string>();
            foreach (var item in evidence.Items)
                if (item.Container == "back" && !item.Owned) observedNonOwnedBackOrder.Add(item.ItemRef);
            if (requirePreflightState)
            {
                if (evidence.MultiBuyEnabled)
                {
                    if (!SequenceEqual(observedNonOwnedBackOrder, evidence.PurchaseListOrderRefs))
                        return "purchase_collection_not_complete";
                }
                else
                {
                    // With multi-buy disabled, the native handler consumes exactly its selected
                    // row (falling back to the first non-owned source item). The quote reader has
                    // already bound that one-item list to the visible selection.
                    if (evidence.PurchaseListOrderRefs.Count != 1 ||
                        !observedNonOwnedBackOrder.Contains(evidence.PurchaseListOrderRefs[0]))
                        return "purchase_collection_not_complete";
                }
            }
            foreach (var row in evidence.QuoteRows)
                if (row == null || !ValidRef(row.ItemRef) || !row.Amount.HasValue || row.Amount.Value < 0)
                    return "quote_evidence_invalid";
            return null;
        }

        private static bool QuoteBindsCollection(PurchaseEvidence evidence)
        {
            return evidence.QuoteBindingVerified && evidence.DisplayedTotal.HasValue &&
                evidence.DisplayedTotal.Value >= 0 && evidence.QuoteRows.Count <= MaxItems;
        }

        private static string ValidateInventoryTransition(PurchaseRequest request,
            PurchaseEvidence before, PurchaseEvidence after)
        {
            string afterShapeReason = ValidateCompleteEvidence(after, requirePreflightState: false);
            if (afterShapeReason != null) return "post_" + afterShapeReason;
            var beforeByRef = new Dictionary<string, PurchaseItemEvidence>(StringComparer.Ordinal);
            var beforeInventoryIdentities = new HashSet<string>(StringComparer.Ordinal);
            var afterByRef = new Dictionary<string, PurchaseItemEvidence>(StringComparer.Ordinal);
            var afterByIdentity = new Dictionary<string, PurchaseItemEvidence>(StringComparer.Ordinal);
            foreach (var item in before.Items)
            {
                beforeByRef.Add(item.ItemRef, item);
                if ((item.Container == "back" || item.Container == "player") && ValidRef(item.IdentityKey))
                    beforeInventoryIdentities.Add(item.IdentityKey);
            }
            foreach (var item in after.Items)
            {
                afterByRef.Add(item.ItemRef, item);
                if (ValidRef(item.IdentityKey) && afterByIdentity.ContainsKey(item.IdentityKey))
                    return "target_identity_duplicated";
                if (ValidRef(item.IdentityKey)) afterByIdentity.Add(item.IdentityKey, item);
                if (item.Container == "back" || item.Container == "player")
                {
                    if (ValidRef(item.IdentityKey))
                    {
                        if (!beforeInventoryIdentities.Contains(item.IdentityKey))
                            return "unexpected_inventory_item_identity";
                    }
                    else if (!beforeByRef.TryGetValue(item.ItemRef, out var previous) ||
                        (previous.Container != "back" && previous.Container != "player") ||
                        ValidRef(previous.IdentityKey))
                        return "inventory_item_identity_missing";
                }
            }
            foreach (string target in request.ItemRefs)
            {
                if (!beforeByRef.TryGetValue(target, out var beforeItem) || beforeItem.Container != "back" ||
                    beforeItem.Owned || !ValidRef(beforeItem.IdentityKey)) return "target_before_state_mismatch";
                if (!afterByIdentity.TryGetValue(beforeItem.IdentityKey, out var afterItem)) return "target_identity_missing";
                if (afterItem.Container != "player" || !afterItem.Owned || afterItem.Units != beforeItem.Units)
                    return "target_transfer_incomplete_or_partial";
            }

            var requestedRefs = new HashSet<string>(request.ItemRefs, StringComparer.Ordinal);
            foreach (var beforeItem in before.Items)
            {
                if ((beforeItem.Container != "back" && beforeItem.Container != "player") ||
                    requestedRefs.Contains(beforeItem.ItemRef)) continue;

                PurchaseItemEvidence afterItem;
                bool found = ValidRef(beforeItem.IdentityKey)
                    ? afterByIdentity.TryGetValue(beforeItem.IdentityKey, out afterItem)
                    : afterByRef.TryGetValue(beforeItem.ItemRef, out afterItem);
                if (!found) return "existing_inventory_item_missing";
                if (afterItem.Container != beforeItem.Container || afterItem.Owned != beforeItem.Owned ||
                    afterItem.Units != beforeItem.Units)
                    return "existing_inventory_item_changed";
            }
            return null;
        }

        private static PurchasePreflightDecision Reject(string reason, PurchaseRequest request) =>
            PurchasePreflightDecision.Reject(reason, request);

        private static bool ValidateRefList(IReadOnlyList<string> values)
        {
            if (values == null) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in values) if (!ValidRef(value) || !seen.Add(value)) return false;
            return true;
        }

        private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++) if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool ValidContainer(string container) => container == "front" || container == "back" || container == "player";
        private static bool ValidRef(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (char c in value)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') &&
                    c != '_' && c != '.' && c != ':' && c != '-') return false;
            return true;
        }
    }

    // One invocation latch per accepted command. The caller marks the boundary before calling
    // the native UI handler; a throw remains consumed and must resolve as unknown, never replay.
    public sealed class PurchaseInvocationLatch
    {
        private readonly PurchaseRequest request;
        private readonly PurchasePreflightDecision preflight;
        private int invocationStarted;
        private bool invocationReturned;
        private bool uncertain;

        public bool InvocationStarted => Volatile.Read(ref invocationStarted) != 0;
        public bool InvocationReturned => invocationReturned;
        public bool IsUncertain => uncertain;
        public int InvocationCount => InvocationStarted ? 1 : 0;

        internal PurchaseInvocationLatch(PurchaseRequest request, PurchasePreflightDecision preflight)
        {
            this.request = request;
            this.preflight = preflight;
        }

        internal bool Binds(PurchaseRequest requested, PurchaseEvidence evidence) =>
            ReferenceEquals(request, requested) && preflight != null && preflight.Binds(requested, evidence);

        public bool InvokeOnce(string requestedCommandId, Action nativeEntry)
        {
            if (request == null || preflight == null || !preflight.Binds(request, preflight.Evidence) ||
                requestedCommandId != request.CommandId || nativeEntry == null)
                return false;
            if (!preflight.TryClaimInvocation(request)) return false;
            if (Interlocked.CompareExchange(ref invocationStarted, 1, 0) != 0) return false;
            try
            {
                nativeEntry();
                invocationReturned = true;
                return true;
            }
            catch
            {
                uncertain = true;
                throw;
            }
        }

        public void MarkOutcomeUnknown()
        {
            if (InvocationStarted) uncertain = true;
        }
    }
}
