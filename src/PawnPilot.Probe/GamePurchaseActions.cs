using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace PawnPilot
{
    // The purchase adapter is intentionally separate from the UI/business action adapter. It
    // keeps the native GameItem identities process-local and only dispatches the game's one
    // ordinary purchase handler after fresh quote, inventory, cash, and capacity checks.
    internal sealed class GamePurchaseActions
    {
        private sealed class ItemIdentity
        {
            public string Key;
            public string PostRef;
        }

        private sealed class PurchaseExecution
        {
            public PurchaseRequest Request;
            public PurchaseEvidence Before;
            public PurchaseInvocationLatch Invocation;
            public GameItem[] Items;
            public Dictionary<GameItem, ItemIdentity> Identities;
            public PlayerStore Store;
            public EmporiumEntry Entry;
            public GameGridInventory SourceInventory;
            public GameGridInventory TargetInventory;
            public string TargetRef;
            public string SourceRef;
            public NegociationUIManager Negotiation;
        }

        private sealed class GameItemComparer : IEqualityComparer<GameItem>
        {
            public static readonly GameItemComparer Instance = new GameItemComparer();
            public bool Equals(GameItem left, GameItem right) => ReferenceEquals(left, right);
            public int GetHashCode(GameItem item) => RuntimeHelpers.GetHashCode(item);
        }

        private readonly BusinessProjection projection;
        private readonly Dictionary<GameItem, ItemIdentity> identities = new Dictionary<GameItem, ItemIdentity>(GameItemComparer.Instance);
        private readonly Dictionary<string, PurchaseExecution> executions = new Dictionary<string, PurchaseExecution>(StringComparer.Ordinal);
        private readonly HashSet<string> consumedCommands = new HashSet<string>(StringComparer.Ordinal);
        private int nextIdentity;

        internal string LastUnavailableReason { get; private set; }

        internal GamePurchaseActions(BusinessProjection businessProjection)
        {
            projection = businessProjection ?? throw new ArgumentNullException(nameof(businessProjection));
        }

        // Called by the main-thread snapshot capture. A candidate uses the displayed normal
        // amount as its maximum; callers may submit another max_spend only when it remains no
        // lower than amount and no greater than fresh cash.
        internal LegalAction GetLegalAction(Snapshot snapshot)
        {
            LastUnavailableReason = null;
            if (snapshot == null || snapshot.schema_version != Protocol.BusinessSchemaVersion || !snapshot.ready)
                return Unavailable("not_ready");
            if (snapshot.business?.details?.quote == null || snapshot.business.details.quote.status != "complete" ||
                snapshot.business.details.quote.mode != "normal" || !snapshot.business.details.quote.displayed_total.HasValue)
                return Unavailable("normal_quote_unverified");
            if (string.IsNullOrEmpty(snapshot.business.customer_ref) || string.IsNullOrEmpty(snapshot.business.offer_ref) ||
                string.IsNullOrEmpty(snapshot.business.collection_fingerprint) || snapshot.business.item_refs == null)
                return Unavailable("offer_or_collection_ref_mismatch");
            if (snapshot.business.item_refs.Count == 0) return Unavailable("purchase_set_empty");

            int amount = snapshot.business.details.quote.displayed_total.Value;
            var arguments = new ActionArguments
            {
                customer_ref = snapshot.business.customer_ref,
                offer_ref = snapshot.business.offer_ref,
                item_refs = new List<string>(snapshot.business.item_refs),
                collection_fingerprint = snapshot.business.collection_fingerprint,
                mode = "normal",
                amount = amount,
                max_spend = amount
            };
            var command = new Command
            {
                schema_version = Protocol.BusinessSchemaVersion,
                id = "purchase-candidate",
                action = "purchase_visible_offer",
                expected_version = snapshot.version,
                expected_world_epoch = snapshot.world_epoch,
                expected_revision = snapshot.revision,
                arguments = arguments
            };
            var decision = Check(command, snapshot);
            if (!decision.Accepted) return Unavailable(decision.Reason);
            LastUnavailableReason = null;
            return new LegalAction { action = command.action, arguments = arguments };
        }

        private LegalAction Unavailable(string reason)
        {
            LastUnavailableReason = string.IsNullOrEmpty(reason) ? "purchase_preflight_unverified" : reason;
            return null;
        }

        internal PurchasePreflightDecision Check(Command command, Snapshot snapshot)
        {
            try
            {
                if (!TryBuildPreflight(command, snapshot, out var request, out var evidence, out _, out string reason))
                {
                    LastUnavailableReason = reason ?? "purchase_preflight_unverified";
                    return PurchasePreflightDecision.Reject(LastUnavailableReason, null);
                }
                var decision = PurchaseVerifier.ValidatePreflight(request, evidence);
                LastUnavailableReason = decision.Accepted ? null : decision.Reason;
                return decision;
            }
            catch
            {
                LastUnavailableReason = "purchase_preflight_failed";
                return PurchasePreflightDecision.Reject(LastUnavailableReason, null);
            }
        }

        // ActionGate calls this once after admission. Re-capture the entire preflight and throw
        // only before OnBuyAllClick if anything changed. Exceptions from the native method escape
        // unchanged; ActionGate records them as outcome_unknown and never retries.
        internal void Invoke(Command command, Snapshot beforeSnapshot)
        {
            if (command?.id == null || consumedCommands.Contains(command.id) ||
                consumedCommands.Count >= Protocol.MaxCommands)
                throw new NativeActionRejectedException("purchase_command_already_consumed");
            // ActionGate serializes native actions. A retained entry can only belong to a
            // terminal unknown/succeeded predecessor; clear it before admitting the next one.
            executions.Clear();
            identities.Clear();
            nextIdentity = 0;
            PurchaseRequest request;
            PurchaseEvidence evidence;
            PurchaseExecution context;
            PurchasePreflightDecision decision;
            try
            {
                if (!TryBuildPreflight(command, beforeSnapshot, out request, out evidence,
                    out context, out string reason))
                    throw new NativeActionRejectedException(reason ?? "purchase_preflight_unverified");
                decision = PurchaseVerifier.ValidatePreflight(request, evidence);
            }
            catch (NativeActionRejectedException) { throw; }
            catch { throw new NativeActionRejectedException("purchase_preflight_failed"); }
            if (!decision.Accepted) throw new NativeActionRejectedException(decision.Reason);

            context.Request = request;
            context.Before = evidence;
            context.Invocation = new PurchaseInvocationLatch(request, decision);
            consumedCommands.Add(command.id);
            executions.Add(command.id, context);

            if (!context.Invocation.InvokeOnce(command.id, () => context.Negotiation.OnBuyAllClick()))
                throw new NativeActionRejectedException("purchase_invocation_already_consumed");
        }

        // The native handler is synchronous for this supported route. If the next bounded
        // inventory/cash sample still shows no effects, keep verifying until ActionGate's
        // execution deadline. Any partial observable settlement becomes outcome_unknown.
        internal VerificationDecision Verify(Command command, Snapshot beforeSnapshot, Snapshot afterSnapshot)
        {
            if (command == null || !executions.TryGetValue(command.id ?? string.Empty, out var execution))
                return VerificationDecision.Uncertain("purchase_invocation_evidence_missing");
            if (beforeSnapshot == null || beforeSnapshot.world_epoch != execution.Request.WorldEpoch ||
                beforeSnapshot.version != execution.Request.ExpectedVersion || afterSnapshot == null ||
                afterSnapshot.world_epoch != execution.Request.WorldEpoch || afterSnapshot.scene != execution.Before.Scene)
            {
                executions.Remove(command.id);
                identities.Clear();
                return VerificationDecision.Uncertain("world_changed_after_invocation");
            }

            if (!ReferenceEquals(EmporiumEntry.Instance, execution.Entry) ||
                !ReferenceEquals(execution.Entry.invElement, execution.TargetInventory) ||
                !ReferenceEquals(execution.Entry.backInvinvElement, execution.SourceInventory))
            {
                executions.Remove(command.id);
                identities.Clear();
                return VerificationDecision.Uncertain("inventory_target_changed_after_invocation");
            }
            if (!TryBuildPostEvidence(execution, afterSnapshot, out var after))
                return VerificationDecision.Pending();

            if (HasNoObservablePurchaseEffect(execution, after)) return VerificationDecision.Pending();
            var result = PurchaseVerifier.Verify(execution.Request, execution.Before, after, execution.Invocation);
            if (result.outcome != "pending")
            {
                executions.Remove(command.id);
                identities.Clear();
            }
            return result;
        }

        private bool TryBuildPreflight(Command command, Snapshot snapshot, out PurchaseRequest request,
            out PurchaseEvidence evidence, out PurchaseExecution execution, out string reason)
        {
            request = null;
            evidence = null;
            execution = null;
            reason = "purchase_preflight_unverified";
            var args = command?.arguments;
            if (command == null || command.action != "purchase_visible_offer" || args == null ||
                args.customer_ref == null || args.offer_ref == null || args.collection_fingerprint == null ||
                args.item_refs == null || args.mode != "normal" || args.authorization_ref != null ||
                !args.amount.HasValue || !args.max_spend.HasValue ||
                args.option_ref != null || args.dialogue_ref != null)
            {
                reason = "invalid_purchase_command";
                return false;
            }

            request = new PurchaseRequest(command.id, command.expected_world_epoch, command.expected_version,
                command.expected_revision, args.customer_ref, args.offer_ref, args.item_refs,
                args.collection_fingerprint, args.mode, args.amount.Value, args.max_spend.Value);
            if (snapshot == null || !snapshot.ready || snapshot.schema_version != Protocol.BusinessSchemaVersion ||
                snapshot.world_epoch != request.WorldEpoch || snapshot.version != request.ExpectedVersion ||
                snapshot.revision != request.ExpectedRevision || snapshot.scene == null || snapshot.menu_open ||
                snapshot.business?.details?.customer == null)
            {
                reason = "state_ref_mismatch";
                return false;
            }

            var customerState = snapshot.business.details.customer;
            if (snapshot.business.customer_ref != args.customer_ref || customerState.presence != "present" ||
                customerState.arrived != true || customerState.trade_type != "sell" || customerState.phase != "trading")
            {
                reason = "customer_ref_mismatch";
                return false;
            }
            if (snapshot.business.offer_ref != args.offer_ref ||
                snapshot.business.collection_fingerprint != args.collection_fingerprint ||
                snapshot.business.item_refs == null || !SameOrder(snapshot.business.item_refs, args.item_refs))
            {
                reason = "offer_or_collection_ref_mismatch";
                return false;
            }
            var quote = snapshot.business.details.quote;
            if (quote == null || quote.status != "complete" || quote.mode != "normal" ||
                !quote.displayed_total.HasValue || quote.displayed_total.Value != args.amount.Value)
            {
                reason = "normal_quote_unverified";
                return false;
            }

            if (!UiScopeReady(snapshot))
            {
                reason = "purchase_ui_scope_unverified";
                return false;
            }

            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            var ui = StoreUIManager.Instance;
            var entry = EmporiumEntry.Instance;
            var negotiation = NegociationUIManager.Instance;
            var currentInstance = store == null ? null : store.currentClientInstance;
            var client = currentInstance == null ? null : currentInstance.GetClientBlueprint();
            if (store == null || ui == null || entry == null || negotiation == null ||
                currentInstance == null || client == null ||
                !projection.TryResolveCustomer(snapshot.world_epoch, args.customer_ref, out var customerIdentity) ||
                !ReferenceEquals(customerIdentity, currentInstance) || !store.isClientArrived ||
                store.storeState != PlayerStore.StoreState.OPEN ||
                !GameBusinessActions.IsSupportedOrdinarySeller(client) ||
                client.isBusinessComplete || client.isRegularSellDisable || store.isClientBeingArrested ||
                store.isHandlingShootout || ui.isShot ||
                negotiation.uiObject == null || !negotiation.uiObject.activeInHierarchy ||
                store.playerCash != snapshot.cash || args.amount.Value > args.max_spend.Value ||
                args.max_spend.Value > store.playerCash || store.playerCash < args.amount.Value)
            {
                reason = "native_purchase_state_unverified";
                return false;
            }

            if (!projection.TryResolvePurchase(snapshot.world_epoch, args.customer_ref, args.offer_ref,
                args.collection_fingerprint, out var objectItems, out var resolvedRefs, out int resolvedAmount) ||
                objectItems.Length != args.item_refs.Count || !SameOrder(resolvedRefs, args.item_refs) ||
                resolvedAmount != args.amount.Value)
            {
                reason = "quote_binding_unverified";
                return false;
            }
            var requestedItems = new GameItem[objectItems.Length];
            for (int i = 0; i < objectItems.Length; i++)
            {
                requestedItems[i] = objectItems[i] as GameItem;
                if (requestedItems[i] == null)
                {
                    reason = "purchase_item_identity_unverified";
                    return false;
                }
            }

            var back = FindContainer(snapshot, "back");
            var player = FindContainer(snapshot, "player");
            if (!ContainerComplete(back) || !ContainerComplete(player) || entry.backInvinvElement == null ||
                entry.invElement == null || entry.backInvinvElement.childItems == null || entry.invElement.childItems == null)
            {
                reason = "inventory_evidence_incomplete";
                return false;
            }

            var capture = GameCapacityReader.Capture(entry, requestedItems,
                item => ResolveItemRef(snapshot.world_epoch, entry, item), snapshot.world_epoch,
                snapshot.revision, client.isMultiBuyDisabled, nativeSelectionVerified: true,
                clientOnItemSoldCallbackPresent: client.onItemSold == null ? CapacityFact.No : CapacityFact.Yes);
            if (capture == null || capture.Snapshot == null || capture.Proof == null ||
                capture.Status != CapacityProofStatus.Sufficient ||
                !capture.Proof.IsBoundTo(capture.TargetInventoryRef, snapshot.world_epoch,
                    snapshot.revision, resolvedRefs))
            {
                reason = "capacity_" + (capture?.Reason.ToString() ?? "incompletesnapshot").ToLowerInvariant();
                return false;
            }

            var sourceItems = new List<GameItem>(entry.backInvinvElement.childItems);
            var targetItems = new List<GameItem>(entry.invElement.childItems);
            if (!MatchesProjectedContainer(snapshot.world_epoch, "back", back, sourceItems) ||
                !MatchesProjectedContainer(snapshot.world_epoch, "player", player, targetItems))
            {
                reason = "inventory_projection_stale";
                return false;
            }

            var evidenceItems = new List<PurchaseItemEvidence>(sourceItems.Count + targetItems.Count);
            var executionIdentities = new Dictionary<GameItem, ItemIdentity>(GameItemComparer.Instance);
            if (!AppendPreItems(sourceItems, "back", entry.backInvinvElement, capture,
                    executionIdentities, evidenceItems) ||
                !AppendPreItems(targetItems, "player", entry.invElement, capture,
                    executionIdentities, evidenceItems))
            {
                reason = "inventory_capacity_facts_missing";
                return false;
            }

            evidence = new PurchaseEvidence(command.id, "complete", snapshot.world_epoch, snapshot.scene,
                snapshot.revision, snapshot.version, snapshot.phase, store.playerCash, "present", args.customer_ref,
                true, "sell", "trading", snapshot.business.details.ui?.status,
                snapshot.business.details.ui?.coverage, snapshot.business.details.ui?.blockers,
                args.offer_ref, capture.TargetInventoryRef, capture.SourceInventoryRef,
                args.collection_fingerprint, resolvedRefs, "complete", "normal", resolvedAmount,
                Array.Empty<PurchaseQuoteRowEvidence>(), evidenceItems, true,
                multiBuyEnabled: !client.isMultiBuyDisabled, capacitySnapshot: capture.Snapshot,
                capacityProof: capture.Proof, quoteBindingVerified: true, nativePurchaseReady: true);

            execution = new PurchaseExecution
            {
                Items = requestedItems,
                Identities = executionIdentities,
                Store = store,
                Entry = entry,
                SourceInventory = entry.backInvinvElement,
                TargetInventory = entry.invElement,
                TargetRef = capture.TargetInventoryRef,
                SourceRef = capture.SourceInventoryRef,
                Negotiation = negotiation
            };
            foreach (var item in requestedItems)
            {
                if (!executionIdentities.ContainsKey(item))
                {
                    reason = "purchase_item_identity_unverified";
                    execution = null;
                    return false;
                }
            }
            reason = null;
            return true;
        }

        private bool TryBuildPostEvidence(PurchaseExecution execution, Snapshot snapshot, out PurchaseEvidence evidence)
        {
            evidence = null;
            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            var entry = EmporiumEntry.Instance;
            if (!ReferenceEquals(store, execution.Store) || !ReferenceEquals(entry, execution.Entry) ||
                entry?.backInvinvElement == null || entry.invElement == null ||
                entry.backInvinvElement.childItems == null || entry.invElement.childItems == null ||
                snapshot.cash != store.playerCash) return false;

            var source = new List<GameItem>(entry.backInvinvElement.childItems);
            var target = new List<GameItem>(entry.invElement.childItems);
            if (source.Count + target.Count > PurchaseVerifier.MaxItems) return false;
            var seen = new HashSet<GameItem>(GameItemComparer.Instance);
            var items = new List<PurchaseItemEvidence>(source.Count + target.Count);
            if (!AppendPostItems(source, "back", execution, seen, items) ||
                !AppendPostItems(target, "player", execution, seen, items)) return false;

            var customer = snapshot.business?.details?.customer;
            evidence = new PurchaseEvidence(execution.Request.CommandId, "complete", snapshot.world_epoch,
                snapshot.scene, snapshot.revision, snapshot.version, snapshot.phase, store.playerCash,
                customer?.presence ?? "unknown", snapshot.business?.customer_ref,
                customer?.arrived, customer?.trade_type ?? "unknown", customer?.phase ?? "unknown",
                snapshot.business?.details?.ui?.status, snapshot.business?.details?.ui?.coverage,
                snapshot.business?.details?.ui?.blockers, null, execution.TargetRef, execution.SourceRef,
                null, Array.Empty<string>(), "unavailable", "unknown", null,
                Array.Empty<PurchaseQuoteRowEvidence>(), items, true, multiBuyEnabled: false);
            return true;
        }

        private bool AppendPreItems(IReadOnlyList<GameItem> source, string container, GameGridInventory expectedParent,
            GameCapacityCapture capture, Dictionary<GameItem, ItemIdentity> executionIdentities,
            List<PurchaseItemEvidence> evidenceItems)
        {
            foreach (GameItem item in source)
            {
                if (item == null || !capture.ItemFactsByItem.TryGetValue(item, out var facts) || facts == null ||
                    !ReferenceEquals(item.parentInventory, expectedParent))
                    return false;
                ItemIdentity identity = GetIdentity(item);
                executionIdentities[item] = identity;
                evidenceItems.Add(new PurchaseItemEvidence(facts.ItemRef, container, facts.UnitCount,
                    facts.IsOwned, capacityFacts: facts, identityKey: identity.Key));
            }
            return true;
        }

        private bool AppendPostItems(IReadOnlyList<GameItem> source, string container, PurchaseExecution execution,
            HashSet<GameItem> seen, List<PurchaseItemEvidence> evidenceItems)
        {
            foreach (GameItem item in source)
            {
                if (item == null || !seen.Add(item) ||
                    !ReferenceEquals(item.parentInventory, container == "back" ? execution.SourceInventory : execution.TargetInventory))
                    return false;
                ItemIdentity identity = GetIdentity(item);
                identity.PostRef = identity.PostRef ?? "post-item-" + identity.Key.Substring("native-item-".Length);
                int units = item.unitCount;
                if (units < 1) return false;
                evidenceItems.Add(new PurchaseItemEvidence(identity.PostRef, container, units,
                    GeneralHelper.IsItemOwned(item), identityKey: identity.Key));
            }
            return true;
        }

        private ItemIdentity GetIdentity(GameItem item)
        {
            if (!identities.TryGetValue(item, out var value))
            {
                value = new ItemIdentity { Key = "native-item-" + (++nextIdentity).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                identities.Add(item, value);
            }
            return value;
        }

        private string ResolveItemRef(string worldEpoch, EmporiumEntry entry, GameItem item)
        {
            if (item == null) return null;
            if (ReferenceEquals(item.parentInventory, entry.backInvinvElement))
                return projection.TryGetItemReference(worldEpoch, "back", item, out string backRef) ? backRef : null;
            if (ReferenceEquals(item.parentInventory, entry.invElement))
                return projection.TryGetItemReference(worldEpoch, "player", item, out string playerRef) ? playerRef : null;
            return null;
        }

        private bool MatchesProjectedContainer(string worldEpoch, string container, ContainerState state,
            IReadOnlyList<GameItem> nativeItems)
        {
            if (!ContainerComplete(state) || nativeItems == null || state.item_refs.Count != nativeItems.Count) return false;
            for (int i = 0; i < nativeItems.Count; i++)
            {
                if (!projection.TryGetItemReference(worldEpoch, container, nativeItems[i], out string itemRef) ||
                    itemRef != state.item_refs[i]) return false;
            }
            return true;
        }

        private static bool UiScopeReady(Snapshot snapshot)
        {
            if (snapshot.menu_open || snapshot.business?.details?.ui?.blockers == null) return false;
            foreach (string blocker in snapshot.business.details.ui.blockers)
                if (blocker != "global_ui_stack_unobservable" && blocker != "global_raycast_order_unobservable" &&
                    blocker != "quote_rebuild_completion_unobservable" && blocker != "tutorial_group_active_unverified" &&
                    blocker != "control_hints_active_unverified") return false;

            var ui = StoreUIManager.Instance;
            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            if (ui == null || store == null ||
                (ui.escapeUIManager?.menuPanel != null && ui.escapeUIManager.menuPanel.activeInHierarchy) ||
                (ui.gameOptionUIManager != null && ui.gameOptionUIManager.IsOpen) ||
                (ui.confirmPanelUIManager?.panel != null && ui.confirmPanelUIManager.panel.activeInHierarchy) ||
                (ui.closeShutterConfirmationPanel != null && ui.closeShutterConfirmationPanel.activeInHierarchy) ||
                ui.isShot || store.isHandlingShootout || store.isClientBeingArrested) return false;
            var generic = GenericConfirmationUIManager.Instance;
            if (generic?.panel != null && generic.panel.activeInHierarchy) return false;
            var bargain = BargainUIManager.Instance;
            if (bargain != null && bargain.IsOpen) return false;
            return true;
        }

        private static ContainerState FindContainer(Snapshot snapshot, string name) =>
            snapshot?.business?.details?.containers?.Find(c => c != null && c.container == name);

        private static bool ContainerComplete(ContainerState state) => state != null &&
            state.completeness == "complete" && state.item_refs != null &&
            state.item_refs.Count <= PurchaseVerifier.MaxItems &&
            state.item_refs.Distinct(StringComparer.Ordinal).Count() == state.item_refs.Count;

        private static bool SameOrder(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool HasNoObservablePurchaseEffect(PurchaseExecution execution, PurchaseEvidence after)
        {
            if (after.Cash != execution.Before.Cash) return false;
            var afterByIdentity = new Dictionary<string, PurchaseItemEvidence>(StringComparer.Ordinal);
            foreach (var item in after.Items)
                if (item.IdentityKey != null) afterByIdentity[item.IdentityKey] = item;
            foreach (var requested in execution.Request.ItemRefs)
            {
                PurchaseItemEvidence beforeItem = null;
                foreach (var item in execution.Before.Items)
                    if (item.ItemRef == requested) { beforeItem = item; break; }
                if (beforeItem == null || !afterByIdentity.TryGetValue(beforeItem.IdentityKey, out var afterItem) ||
                    afterItem.Container != "back" || afterItem.Owned || afterItem.Units != beforeItem.Units) return false;
            }
            return true;
        }
    }
}
