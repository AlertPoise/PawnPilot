using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace PawnPilot
{
    // Process-local sale evidence. Item identity keys never cross the wire.
    public sealed class SaleRequest
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
        public IReadOnlyList<string> ItemRefs => itemRefs;

        public SaleRequest(string commandId, string worldEpoch, string expectedVersion, int expectedRevision,
            string customerRef, string offerRef, IEnumerable<string> itemRefs, string collectionFingerprint,
            string mode, int amount)
        {
            CommandId = commandId;
            WorldEpoch = worldEpoch;
            ExpectedVersion = expectedVersion;
            ExpectedRevision = expectedRevision;
            CustomerRef = customerRef;
            OfferRef = offerRef;
            this.itemRefs = Array.AsReadOnly(itemRefs == null ? Array.Empty<string>() : itemRefs.ToArray());
            CollectionFingerprint = collectionFingerprint;
            Mode = mode;
            Amount = amount;
        }
    }

    public sealed class SaleItemEvidence
    {
        public string ItemRef { get; }
        public string IdentityKey { get; }
        public string Container { get; }
        public int Units { get; }
        public bool Owned { get; }

        public SaleItemEvidence(string itemRef, string identityKey, string container, int units, bool owned)
        {
            ItemRef = itemRef;
            IdentityKey = identityKey;
            Container = container;
            Units = units;
            Owned = owned;
        }
    }

    public sealed class SaleEvidence
    {
        private readonly ReadOnlyCollection<string> itemRefs;
        private readonly ReadOnlyCollection<SaleItemEvidence> items;
        public string CommandId { get; }
        public string Status { get; }
        public string WorldEpoch { get; }
        public string Scene { get; }
        public int Revision { get; }
        public string Version { get; }
        public int Cash { get; }
        public string CustomerPresence { get; }
        public string CustomerRef { get; }
        public string CustomerIntent { get; }
        public string CustomerPhase { get; }
        public string OfferRef { get; }
        public string CollectionFingerprint { get; }
        public string QuoteStatus { get; }
        public string QuoteMode { get; }
        public int? DisplayedTotal { get; }
        public bool ContainersComplete { get; }
        public string BackInventoryRef { get; }
        public string SoldInventoryRef { get; }
        public IReadOnlyList<string> ItemRefs => itemRefs;
        public IReadOnlyList<SaleItemEvidence> Items => items;

        public SaleEvidence(string commandId, string status, string worldEpoch, string scene, int revision,
            string version, int cash, string customerPresence, string customerRef, string customerIntent,
            string customerPhase, string offerRef, string collectionFingerprint, string quoteStatus,
            string quoteMode, int? displayedTotal, IEnumerable<string> itemRefs,
            IEnumerable<SaleItemEvidence> items, bool containersComplete, string backInventoryRef,
            string soldInventoryRef)
        {
            CommandId = commandId;
            Status = status;
            WorldEpoch = worldEpoch;
            Scene = scene;
            Revision = revision;
            Version = version;
            Cash = cash;
            CustomerPresence = customerPresence;
            CustomerRef = customerRef;
            CustomerIntent = customerIntent;
            CustomerPhase = customerPhase;
            OfferRef = offerRef;
            CollectionFingerprint = collectionFingerprint;
            QuoteStatus = quoteStatus;
            QuoteMode = quoteMode;
            DisplayedTotal = displayedTotal;
            this.itemRefs = Array.AsReadOnly(itemRefs == null ? Array.Empty<string>() : itemRefs.ToArray());
            this.items = Array.AsReadOnly(items == null ? Array.Empty<SaleItemEvidence>() : items.ToArray());
            ContainersComplete = containersComplete;
            BackInventoryRef = backInventoryRef;
            SoldInventoryRef = soldInventoryRef;
        }
    }

    public sealed class SaleInvocationLatch
    {
        private readonly string commandId;
        private int started;
        private bool returned;
        private bool uncertain;
        public int InvocationCount => Volatile.Read(ref started);
        public bool InvocationReturned => returned;
        public bool IsUncertain => uncertain;

        public SaleInvocationLatch(string commandId) { this.commandId = commandId; }

        public bool InvokeOnce(string requestedCommandId, Action nativeEntry)
        {
            if (string.IsNullOrEmpty(commandId) || requestedCommandId != commandId || nativeEntry == null ||
                Interlocked.CompareExchange(ref started, 1, 0) != 0) return false;
            try
            {
                nativeEntry();
                returned = true;
                return true;
            }
            catch
            {
                uncertain = true;
                throw;
            }
        }
    }

    public static class SaleVerifier
    {
        private const int MaxItems = 64;

        public static string ValidatePreflight(SaleRequest request, SaleEvidence before)
        {
            if (request == null || !ValidRef(request.CommandId) || !ValidRef(request.WorldEpoch) ||
                !ValidRef(request.CustomerRef) || !ValidRef(request.OfferRef) ||
                !ValidRef(request.CollectionFingerprint) || request.Mode != "normal" || request.Amount < 0 ||
                request.ItemRefs.Count != 1 || !ValidRef(request.ItemRefs[0])) return "sale_request_invalid";
            if (!Complete(before) || before.CommandId != request.CommandId || before.WorldEpoch != request.WorldEpoch ||
                before.Version != request.ExpectedVersion || before.Revision != request.ExpectedRevision)
                return "sale_state_ref_mismatch";
            if (before.CustomerPresence != "present" || before.CustomerRef != request.CustomerRef ||
                before.CustomerIntent != "buy" || before.CustomerPhase != "trading") return "sale_customer_mismatch";
            if (before.OfferRef != request.OfferRef || before.CollectionFingerprint != request.CollectionFingerprint ||
                !SameOrder(before.ItemRefs, request.ItemRefs)) return "sale_offer_or_collection_mismatch";
            if (before.QuoteStatus != "complete" || before.QuoteMode != "normal" ||
                before.DisplayedTotal != request.Amount) return "sale_quote_unverified";
            if (string.IsNullOrEmpty(before.BackInventoryRef) || string.IsNullOrEmpty(before.SoldInventoryRef) ||
                before.BackInventoryRef == before.SoldInventoryRef) return "sale_inventory_identity_unverified";

            SaleItemEvidence target = null;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in before.Items)
            {
                if (item == null || !ValidRef(item.ItemRef) || !ValidRef(item.IdentityKey) ||
                    item.Units <= 0 || !identities.Add(item.IdentityKey)) return "sale_item_evidence_invalid";
                if (item.Container == "back")
                {
                    if (target != null) return "sale_back_must_contain_one_item";
                    target = item;
                }
                else if (item.Container != "player" && item.Container != "sold") return "sale_item_container_invalid";
            }
            if (target == null || !target.Owned || target.ItemRef != request.ItemRefs[0] ||
                target.Container != "back" || before.Items.Any(i => i != null && i.Container == "sold" && i.IdentityKey == target.IdentityKey))
                return "sale_target_before_state_mismatch";
            if (!SameOrder(before.ItemRefs, new[] { target.ItemRef })) return "sale_collection_not_single_item";
            return null;
        }

        public static VerificationDecision Verify(SaleRequest request, SaleEvidence before,
            SaleEvidence after, SaleInvocationLatch invocation)
        {
            if (invocation == null || request == null || invocation.InvocationCount != 1 ||
                !invocation.InvocationReturned || invocation.IsUncertain)
                return VerificationDecision.Uncertain("sale_invocation_not_cleanly_returned");
            string reason = ValidatePreflight(request, before);
            if (reason != null) return VerificationDecision.Uncertain("sale_preflight_invalid:" + reason);
            if (!Complete(after)) return VerificationDecision.Pending();
            if (after.CommandId != request.CommandId || after.WorldEpoch != before.WorldEpoch || after.Scene != before.Scene ||
                after.Revision <= before.Revision || after.BackInventoryRef != before.BackInventoryRef ||
                after.SoldInventoryRef != before.SoldInventoryRef)
                return VerificationDecision.Uncertain("sale_post_state_identity_mismatch");

            var beforeByIdentity = Index(before.Items);
            var afterByIdentity = Index(after.Items);
            if (beforeByIdentity == null || afterByIdentity == null)
                return VerificationDecision.Uncertain("sale_post_item_evidence_invalid");
            var targetBefore = before.Items.Single(i => i.ItemRef == request.ItemRefs[0]);
            afterByIdentity.TryGetValue(targetBefore.IdentityKey, out var targetAfter);
            long expectedCash = (long)before.Cash + request.Amount;
            bool cashMatches = expectedCash >= int.MinValue && expectedCash <= int.MaxValue && after.Cash == expectedCash;
            bool settled = targetAfter != null && targetAfter.Container == "sold" && !targetAfter.Owned &&
                targetAfter.Units == targetBefore.Units && cashMatches;

            foreach (var pair in beforeByIdentity)
            {
                if (pair.Key == targetBefore.IdentityKey) continue;
                if (!afterByIdentity.TryGetValue(pair.Key, out var current) ||
                    current.Container != pair.Value.Container || current.Owned != pair.Value.Owned ||
                    current.Units != pair.Value.Units || current.ItemRef != pair.Value.ItemRef)
                    return VerificationDecision.Uncertain("sale_unrelated_item_changed");
            }
            if (afterByIdentity.Count != beforeByIdentity.Count) return VerificationDecision.Uncertain("sale_unexpected_item_identity");

            if (settled) return VerificationDecision.Succeeded();
            bool noEffect = after.Cash == before.Cash && targetAfter != null && targetAfter.Container == "back" &&
                targetAfter.Owned && targetAfter.Units == targetBefore.Units &&
                after.OfferRef == before.OfferRef && after.CollectionFingerprint == before.CollectionFingerprint;
            if (noEffect) return VerificationDecision.Pending();
            return VerificationDecision.Uncertain("sale_partial_or_unexpected_settlement");
        }

        private static bool Complete(SaleEvidence evidence) => evidence != null && evidence.Status == "complete" &&
            evidence.ContainersComplete && !string.IsNullOrEmpty(evidence.WorldEpoch) &&
            !string.IsNullOrEmpty(evidence.Scene) && !string.IsNullOrEmpty(evidence.Version) &&
            evidence.Items != null && evidence.Items.Count <= MaxItems && evidence.ItemRefs != null &&
            evidence.ItemRefs.Count == 1;

        private static Dictionary<string, SaleItemEvidence> Index(IReadOnlyList<SaleItemEvidence> items)
        {
            var result = new Dictionary<string, SaleItemEvidence>(StringComparer.Ordinal);
            foreach (var item in items)
                if (item == null || !ValidRef(item.IdentityKey) || result.ContainsKey(item.IdentityKey)) return null;
                else result.Add(item.IdentityKey, item);
            return result;
        }

        private static bool SameOrder(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool ValidRef(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (char ch in value)
                if (!((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                    (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == ':' || ch == '-')) return false;
            return true;
        }
    }
}
