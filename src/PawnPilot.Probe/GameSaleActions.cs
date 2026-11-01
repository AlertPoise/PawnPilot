using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PawnPilot
{
    // Native adapter for the narrow ordinary BUY path. It moves one player-owned item to an
    // empty back counter through GraphUtils.TryAcceptOnce, then uses the game's primary sell
    // button only when the current native sell set is exactly that one item.
    internal sealed class GameSaleActions
    {
        private sealed class Execution
        {
            public string Action;
            public Command Command;
            public SaleRequest Request;
            public SaleEvidence Before;
            public SaleInvocationLatch Invocation;
            public GameItem Target;
            public PlayerStore Store;
            public EmporiumEntry Entry;
            public StoreClientInstance CustomerInstance;
            public StoreClient Client;
            public GameGridInventory PlayerInventory;
            public GameGridInventory BackInventory;
            public GameGridInventory SoldInventory;
            public PixelWindow BackWindow;
            public NegociationUIManager Negotiation;
            public Dictionary<GameItem, string> Identities;
            public GameItem[] BeforePlayer;
            public GameItem[] BeforeBack;
            public GameItem[] BeforeSold;
            public string[] BeforePlayerRefs;
            public string[] BeforeBackRefs;
            public Dictionary<GameItem, SaleItemEvidence> BeforeItemFacts;
            public int BeforeCash;
            public string BeforeOfferRef;
            public string BeforeCollectionFingerprint;
            public int TransferReturn;
        }

        private sealed class GameItemComparer : IEqualityComparer<GameItem>
        {
            public static readonly GameItemComparer Instance = new GameItemComparer();
            public bool Equals(GameItem left, GameItem right) => ReferenceEquals(left, right);
            public int GetHashCode(GameItem item) => RuntimeHelpers.GetHashCode(item);
        }

        private readonly BusinessProjection projection;
        private static readonly FieldInfo PrimarySaleButtonField = typeof(NegociationUIManager).GetField(
            "primaryButton", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Dictionary<string, Execution> executions = new Dictionary<string, Execution>(StringComparer.Ordinal);
        private readonly HashSet<string> consumedCommands = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<GameItem, string> identities = new Dictionary<GameItem, string>(GameItemComparer.Instance);
        private int nextIdentity;

        internal GameSaleActions(BusinessProjection businessProjection)
        {
            projection = businessProjection ?? throw new ArgumentNullException(nameof(businessProjection));
        }

        internal static bool IsSupportedOrdinaryBuyer(StoreClient client) => client != null &&
            client.clientIntent == StoreClient.ClientIntent.BUY &&
            (client.identifier == "spacerFood" || client.identifier == "spacerFoodStory");

        // Visible/ordinary interest only. This deliberately does not inspect hidden features,
        // price the item, or predict the buyer's appraisal/reinspection result.
        internal static bool CanOfferItem(GameItem item, StoreClient client) => item != null &&
            IsSupportedOrdinaryBuyer(client) && GeneralHelper.IsItemOwned(item) &&
            item.unitCount > 0 && client.IsClientBuyingThisItem(item);

        internal IEnumerable<LegalAction> BuildLegalActions(Snapshot snapshot)
        {
            var actions = new List<LegalAction>();
            if (!BaseReady(snapshot)) return actions;
            var customerRef = snapshot.business.customer_ref;
            if (string.IsNullOrEmpty(customerRef) || snapshot.business.details.customer?.trade_type != "buy" ||
                snapshot.business.details.customer.phase != "trading" ||
                !IsOrdinaryUiReady(snapshot) || !TryGetContainer(snapshot, "player", out var player) ||
                !TryGetContainer(snapshot, "back", out var back))
                return actions;

            if (back.item_refs.Count == 0)
            foreach (var item in snapshot.business.details.items ?? new List<ItemState>())
            {
                if (item == null || item.container != "player" || !item.owned || string.IsNullOrEmpty(item.item_ref)) continue;
                var refs = new List<string> { item.item_ref };
                var fingerprint = SalePreparationFingerprint(snapshot, customerRef, item);
                if (fingerprint == null) continue;
                var args = new ActionArguments { customer_ref = customerRef, item_refs = refs,
                    collection_fingerprint = fingerprint };
                if (CanPrepare(CommandFor(snapshot, "prepare_sale_items", args), snapshot))
                    actions.Add(new LegalAction { action = "prepare_sale_items", arguments = args });
            }

            if (TryGetSaleArguments(snapshot, out var saleArgs) && CanSell(CommandFor(snapshot, "sell_visible_offer", saleArgs), snapshot))
                actions.Add(new LegalAction { action = "sell_visible_offer", arguments = saleArgs });
            return actions;
        }

        internal void Invoke(Command command, Snapshot snapshot)
        {
            if (command?.id == null || consumedCommands.Contains(command.id) || consumedCommands.Count >= Protocol.MaxCommands)
                throw new NativeActionRejectedException("sale_command_already_consumed");
            if (executions.Count != 0) executions.Clear();
            if (command.action == "prepare_sale_items")
            {
                if (!TryBuildPrepare(command, snapshot, out var execution, out string reason))
                    throw new NativeActionRejectedException(reason ?? "sale_prepare_preflight_unverified");
                consumedCommands.Add(command.id);
                executions.Add(command.id, execution);
                execution.Invocation = new SaleInvocationLatch(command.id);
                // The native placement is the invocation boundary. We cannot retry even when
                // the native receiver returns zero/partial; the next snapshot decides the result.
                if (!execution.Invocation.InvokeOnce(command.id, () =>
                    execution.TransferReturn = GraphUtils.TryAcceptOnce(execution.BackWindow, execution.Target, -1)))
                    throw new NativeActionRejectedException("sale_prepare_invocation_already_consumed");
                return;
            }
            if (command.action == "sell_visible_offer")
            {
                if (!TryBuildSale(command, snapshot, out var execution, out string reason))
                    throw new NativeActionRejectedException(reason ?? "sale_preflight_unverified");
                consumedCommands.Add(command.id);
                executions.Add(command.id, execution);
                if (!execution.Invocation.InvokeOnce(command.id, () => execution.Negotiation.OnSellAllClick()))
                    throw new NativeActionRejectedException("sale_invocation_already_consumed");
                return;
            }
            throw new NativeActionRejectedException("unsupported_sale_action");
        }

        internal VerificationDecision Verify(Command command, Snapshot beforeSnapshot, Snapshot afterSnapshot)
        {
            if (command == null || !executions.TryGetValue(command.id ?? string.Empty, out var execution))
                return VerificationDecision.Uncertain("sale_invocation_evidence_missing");
            if (beforeSnapshot == null || beforeSnapshot.world_epoch != execution.Command.expected_world_epoch ||
                beforeSnapshot.version != execution.Command.expected_version || afterSnapshot == null ||
                afterSnapshot.world_epoch != execution.Command.expected_world_epoch || afterSnapshot.scene != beforeSnapshot.scene)
                return Finish(command.id, VerificationDecision.Uncertain("world_changed_after_sale_action"));
            if (!ReferenceEquals(EmporiumEntry.Instance, execution.Entry) ||
                !ReferenceEquals(execution.Entry.invElement, execution.PlayerInventory) ||
                !ReferenceEquals(execution.Entry.backInvinvElement, execution.BackInventory) ||
                !ReferenceEquals(execution.Entry.soldElement, execution.SoldInventory))
                return Finish(command.id, VerificationDecision.Uncertain("sale_inventory_target_changed"));

            if (execution.Action == "prepare_sale_items")
            {
                if (execution.Invocation == null || execution.Invocation.InvocationCount != 1 ||
                    !execution.Invocation.InvocationReturned || execution.Invocation.IsUncertain)
                    return Finish(command.id, VerificationDecision.Uncertain("sale_prepare_invocation_not_cleanly_returned"));
                return VerifyPrepare(command.id, execution, beforeSnapshot, afterSnapshot);
            }

            if (execution.Invocation == null || !execution.Invocation.InvocationReturned)
                return Finish(command.id, VerificationDecision.Uncertain("sale_invocation_not_cleanly_returned"));
            if (!TryCaptureSaleEvidence(execution, afterSnapshot, out var after)) return VerificationDecision.Pending();
            var result = SaleVerifier.Verify(execution.Request, execution.Before, after, execution.Invocation);
            if (result.outcome == "pending")
            {
                // Reinspection may refresh the quote and abort this native click before sale.
                // Once the offer changes without settlement, treat the one invocation as unknown.
                if (after.OfferRef != execution.Before.OfferRef ||
                    after.CollectionFingerprint != execution.Before.CollectionFingerprint)
                    result = VerificationDecision.Uncertain("sale_offer_changed_without_settlement");
            }
            if (result.outcome != "pending") executions.Remove(command.id);
            return result;
        }

        private bool CanPrepare(Command command, Snapshot snapshot) => TryBuildPrepare(command, snapshot, out _, out _);
        private bool CanSell(Command command, Snapshot snapshot) => TryBuildSale(command, snapshot, out _, out _);

        private bool TryBuildPrepare(Command command, Snapshot snapshot, out Execution execution, out string reason)
        {
            execution = null;
            reason = "sale_prepare_preflight_unverified";
            var args = command?.arguments;
            if (command == null || command.action != "prepare_sale_items" || args == null ||
                string.IsNullOrEmpty(args.customer_ref) || args.item_refs == null || args.item_refs.Count != 1 ||
                string.IsNullOrEmpty(args.collection_fingerprint) || args.offer_ref != null || args.mode != null ||
                args.amount.HasValue || args.max_spend.HasValue || args.authorization_ref != null || args.option_ref != null ||
                args.dialogue_ref != null) { reason = "invalid_sale_prepare_command"; return false; }
            if (!BaseReady(snapshot) || snapshot.world_epoch != command.expected_world_epoch || snapshot.version != command.expected_version ||
                snapshot.revision != command.expected_revision || snapshot.business.customer_ref != args.customer_ref ||
                !IsOrdinaryUiReady(snapshot) || snapshot.business.details.customer?.trade_type != "buy" ||
                snapshot.business.details.customer.phase != "trading" || !TryGetContainer(snapshot, "player", out var player) ||
                !TryGetContainer(snapshot, "back", out var back) || back.item_refs.Count != 0)
            { reason = "sale_prepare_state_changed"; return false; }
            var selected = snapshot.business.details.items?.SingleOrDefault(i => i != null && i.item_ref == args.item_refs[0]);
            if (selected == null || selected.container != "player" || !selected.owned || selected.units <= 0 ||
                args.collection_fingerprint != SalePreparationFingerprint(snapshot, args.customer_ref, selected))
            { reason = "sale_prepare_item_ref_mismatch"; return false; }

            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            var ui = StoreUIManager.Instance;
            var entry = EmporiumEntry.Instance;
            var instance = store?.currentClientInstance;
            var client = instance == null ? null : instance.GetClientBlueprint();
            if (store == null || ui == null || entry == null || instance == null || !store.isClientArrived ||
                store.storeState != PlayerStore.StoreState.OPEN || !IsSupportedOrdinaryBuyer(client) ||
                client.isBusinessComplete || store.isClientBeingArrested || store.isHandlingShootout || ui.isShot ||
                !projection.TryResolveCustomer(snapshot.world_epoch, args.customer_ref, out var customerIdentity) ||
                !ReferenceEquals(customerIdentity, instance) || entry.invElement == null || entry.backInvinvElement == null ||
                entry.soldElement == null || entry.invElement.childItems == null || entry.backInvinvElement.childItems == null ||
                entry.soldElement.childItems == null || entry.backInvinvElement.childItems.Count != 0 ||
                !MatchesProjected(snapshot, "player", player, entry.invElement.childItems) ||
                !MatchesProjected(snapshot, "back", back, entry.backInvinvElement.childItems) ||
                entry.backInvWindow == null || !entry.backInvWindow.IsVisible())
            { reason = "sale_prepare_native_state_unverified"; return false; }
            if (!projection.TryResolveItem(snapshot.world_epoch, args.customer_ref, "player", args.item_refs[0], out var identity) ||
                !(identity is GameItem item) || !ReferenceEquals(item.parentInventory, entry.invElement) ||
                !entry.invElement.childItems.Any(candidate => ReferenceEquals(candidate, item)) ||
                !GeneralHelper.IsItemOwned(item) || item.unitCount != selected.units || !item.MayRemove() ||
                !CanOfferItem(item, client))
            { reason = "sale_prepare_native_item_unverified"; return false; }

            // TryFindOneValidInventorySlot sorts destination inventory state in the native build.
            // Let the one-shot normal receiver apply its own capacity decision after admission.
            execution = NewExecution(command, snapshot, store, entry, instance, client, item);
            execution.Action = command.action;
            execution.PlayerInventory = entry.invElement;
            execution.BackInventory = entry.backInvinvElement;
            execution.SoldInventory = entry.soldElement;
            execution.BackWindow = entry.backInvWindow;
            execution.BeforePlayer = entry.invElement.childItems.ToArray();
            execution.BeforeBack = entry.backInvinvElement.childItems.ToArray();
            execution.BeforeSold = entry.soldElement.childItems.ToArray();
            execution.BeforePlayerRefs = player.item_refs.ToArray();
            execution.BeforeBackRefs = back.item_refs.ToArray();
            execution.BeforeItemFacts = CaptureDirectFacts(execution.BeforePlayer, execution.BeforeBack, execution.BeforeSold);
            execution.BeforeCash = store.playerCash;
            execution.BeforeOfferRef = snapshot.business.offer_ref;
            execution.BeforeCollectionFingerprint = snapshot.business.collection_fingerprint;
            return true;
        }

        private bool TryBuildSale(Command command, Snapshot snapshot, out Execution execution, out string reason)
        {
            execution = null;
            reason = "sale_preflight_unverified";
            var args = command?.arguments;
            if (command == null || command.action != "sell_visible_offer" || args == null ||
                string.IsNullOrEmpty(args.customer_ref) || string.IsNullOrEmpty(args.offer_ref) ||
                string.IsNullOrEmpty(args.collection_fingerprint) || args.item_refs == null || args.item_refs.Count != 1 ||
                args.mode != "normal" || !args.amount.HasValue || args.max_spend.HasValue ||
                args.authorization_ref != null || args.option_ref != null || args.dialogue_ref != null)
            { reason = "invalid_sale_command"; return false; }
            if (!BaseReady(snapshot) || snapshot.world_epoch != command.expected_world_epoch || snapshot.version != command.expected_version ||
                snapshot.revision != command.expected_revision || snapshot.business.customer_ref != args.customer_ref ||
                snapshot.business.offer_ref != args.offer_ref || snapshot.business.collection_fingerprint != args.collection_fingerprint ||
                snapshot.business.item_refs == null || !snapshot.business.item_refs.SequenceEqual(args.item_refs, StringComparer.Ordinal) ||
                snapshot.business.details.customer?.trade_type != "buy" || snapshot.business.details.customer.phase != "trading" ||
                snapshot.business.details.quote == null || snapshot.business.details.quote.status != "complete" ||
                snapshot.business.details.quote.mode != "normal" || snapshot.business.details.quote.displayed_total != args.amount)
            { reason = "sale_offer_or_quote_changed"; return false; }

            if (!projection.TryResolvePurchase(snapshot.world_epoch, args.customer_ref, args.offer_ref,
                args.collection_fingerprint, out var objectItems, out var refs, out int total) ||
                objectItems.Length != 1 || refs.Length != 1 || refs[0] != args.item_refs[0] || total != args.amount.Value ||
                !(objectItems[0] is GameItem target))
            { reason = "sale_quote_binding_unverified"; return false; }

            var back = FindContainer(snapshot, "back");
            if (!ContainerComplete(back) || back.item_refs.Count != 1 || back.item_refs[0] != args.item_refs[0])
            { reason = "sale_back_collection_not_single_item"; return false; }
            var selected = snapshot.business.details.items?.SingleOrDefault(i => i != null && i.item_ref == args.item_refs[0]);
            if (selected == null || selected.container != "back" || !selected.owned || selected.units <= 0)
            { reason = "sale_item_state_mismatch"; return false; }

            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            var ui = StoreUIManager.Instance;
            var entry = EmporiumEntry.Instance;
            var negotiation = NegociationUIManager.Instance;
            var instance = store?.currentClientInstance;
            var client = instance == null ? null : instance.GetClientBlueprint();
            if (store == null || ui == null || entry == null || negotiation == null || instance == null ||
                !store.isClientArrived || store.storeState != PlayerStore.StoreState.OPEN ||
                !IsSupportedOrdinaryBuyer(client) || client.isBusinessComplete || store.isClientBeingArrested ||
                store.isHandlingShootout || ui.isShot || negotiation.uiObject == null || !negotiation.uiObject.activeInHierarchy ||
                !IsPrimarySaleButtonReady(negotiation) ||
                client.onItemBought != null || client.onDealAccepted != null ||
                !projection.TryResolveCustomer(snapshot.world_epoch, args.customer_ref, out var customerIdentity) ||
                !ReferenceEquals(customerIdentity, instance) || !IsOrdinaryUiReady(snapshot) ||
                entry.backInvinvElement == null || entry.invElement == null || entry.soldElement == null ||
                entry.backInvinvElement.childItems == null || entry.invElement.childItems == null || entry.soldElement.childItems == null ||
                !ReferenceEquals(target.parentInventory, entry.backInvinvElement) ||
                entry.backInvinvElement.childItems.Count != 1 || !ReferenceEquals(entry.backInvinvElement.childItems[0], target) ||
                !MatchesProjected(snapshot, "back", back, entry.backInvinvElement.childItems) ||
                !GeneralHelper.IsItemOwned(target) || target.unitCount != selected.units || !CanOfferItem(target, client))
            { reason = "sale_native_state_unverified"; return false; }
            // Avoid the native budget confirmation branch. The displayed ordinary total is the
            // amount bound above; do not recompute it through a price getter here.
            if (client.useClientBudget && client.GetBudget() < args.amount.Value)
            { reason = "buyer_budget_below_displayed_total"; return false; }

            execution = NewExecution(command, snapshot, store, entry, instance, client, target);
            execution.Action = command.action;
            execution.PlayerInventory = entry.invElement;
            execution.BackInventory = entry.backInvinvElement;
            execution.SoldInventory = entry.soldElement;
            execution.Negotiation = negotiation;
            execution.BeforePlayer = entry.invElement.childItems.ToArray();
            execution.BeforeBack = entry.backInvinvElement.childItems.ToArray();
            execution.BeforeSold = entry.soldElement.childItems.ToArray();
            execution.BeforeItemFacts = CaptureDirectFacts(execution.BeforePlayer, execution.BeforeBack, execution.BeforeSold);
            execution.BeforeCash = store.playerCash;
            if (!TryCaptureSaleEvidence(execution, snapshot, out var before))
            { reason = "sale_evidence_incomplete"; execution = null; return false; }
            execution.Before = before;
            reason = SaleVerifier.ValidatePreflight(execution.Request, before);
            if (reason != null) { execution = null; return false; }
            return true;
        }

        private Execution NewExecution(Command command, Snapshot snapshot, PlayerStore store, EmporiumEntry entry,
            StoreClientInstance customer, StoreClient client, GameItem item)
        {
            var execution = new Execution { Command = command, Store = store, Entry = entry,
                CustomerInstance = customer, Client = client, Target = item,
                Identities = new Dictionary<GameItem, string>(identities, GameItemComparer.Instance) };
            if (!execution.Identities.ContainsKey(item)) execution.Identities.Add(item, Identity(item));
            if (command.action == "sell_visible_offer")
            {
                var args = command.arguments;
                execution.Request = new SaleRequest(command.id, command.expected_world_epoch, command.expected_version,
                    command.expected_revision, args.customer_ref, args.offer_ref, args.item_refs,
                    args.collection_fingerprint, args.mode, args.amount.Value);
                execution.Invocation = new SaleInvocationLatch(command.id);
            }
            return execution;
        }

        private VerificationDecision VerifyPrepare(string commandId, Execution execution, Snapshot before, Snapshot after)
        {
            if (!BaseReady(after) || after.revision <= before.revision || after.cash != execution.BeforeCash ||
                after.business.customer_ref != execution.Command.arguments.customer_ref)
                return Finish(commandId, VerificationDecision.Uncertain("sale_prepare_post_state_mismatch"));
            var player = FindContainer(after, "player");
            var back = FindContainer(after, "back");
            if (!ContainerComplete(player) || !ContainerComplete(back)) return VerificationDecision.Pending();
            bool targetInPlayer = execution.PlayerInventory.childItems.Any(item => ReferenceEquals(item, execution.Target));
            bool targetInBack = execution.BackInventory.childItems.Any(item => ReferenceEquals(item, execution.Target));
            if (targetInBack && !targetInPlayer && back.item_refs.Count == 1 &&
                projection.TryGetItemReference(after.world_epoch, "back", execution.Target, out var targetRef) &&
                back.item_refs[0] == targetRef &&
                player.item_refs.SequenceEqual(execution.BeforePlayerRefs.Where(itemRef => itemRef != execution.Command.arguments.item_refs[0]), StringComparer.Ordinal) &&
                back.item_refs.SequenceEqual(new[] { targetRef }, StringComparer.Ordinal) &&
                execution.PlayerInventory.childItems.Count == execution.BeforePlayer.Length - 1 &&
                execution.BackInventory.childItems.Count == 1 &&
                SameObjectsAndFacts(execution, execution.BeforeSold, execution.SoldInventory.childItems, "sold") &&
                execution.BeforeBack.Length == 0 &&
                SameRemainingObjectsAndFacts(execution, execution.PlayerInventory.childItems, execution.Target) &&
                execution.BeforeItemFacts.TryGetValue(execution.Target, out var beforeFacts) &&
                GeneralHelper.IsItemOwned(execution.Target) == beforeFacts.Owned && execution.Target.unitCount == beforeFacts.Units &&
                ItemDtoMatches(after, targetRef, "back", beforeFacts.Units, beforeFacts.Owned))
            {
                if (!HasFreshSingleItemQuote(execution, after, targetRef))
                {
                    var quote = after.business?.details?.quote;
                    if (quote == null || quote.status != "complete" || quote.mode != "normal" ||
                        string.IsNullOrEmpty(after.business.offer_ref) ||
                        after.business.offer_ref == execution.BeforeOfferRef ||
                        string.IsNullOrEmpty(after.business.collection_fingerprint) ||
                        after.business.collection_fingerprint == execution.BeforeCollectionFingerprint)
                        return VerificationDecision.Pending();
                    return Finish(commandId, VerificationDecision.Uncertain("sale_prepare_quote_binding_mismatch"));
                }
                return Finish(commandId, VerificationDecision.Succeeded());
            }
            if (!targetInBack && targetInPlayer && execution.BackInventory.childItems.Count == 0 &&
                execution.PlayerInventory.childItems.Count == execution.BeforePlayer.Length) return VerificationDecision.Pending();
            return Finish(commandId, VerificationDecision.Uncertain("sale_prepare_partial_or_unexpected_transfer"));
        }

        private bool TryCaptureSaleEvidence(Execution execution, Snapshot snapshot, out SaleEvidence evidence)
        {
            evidence = null;
            if (!BaseReady(snapshot) || execution.Entry.backInvinvElement.childItems == null ||
                execution.Entry.invElement.childItems == null || execution.Entry.soldElement.childItems == null ||
                snapshot.business?.details?.quote == null) return false;
            var items = new List<SaleItemEvidence>();
            AppendItems(items, execution.Entry.invElement.childItems, "player", snapshot, execution);
            AppendItems(items, execution.Entry.backInvinvElement.childItems, "back", snapshot, execution);
            AppendItems(items, execution.Entry.soldElement.childItems, "sold", snapshot, execution);
            string customerRef = snapshot.business.customer_ref;
            var customer = snapshot.business.details.customer;
            evidence = new SaleEvidence(execution.Command.id, "complete", snapshot.world_epoch, snapshot.scene,
                snapshot.revision, snapshot.version, execution.Store.playerCash, customer?.presence ?? "unverified",
                customerRef, snapshot.business.details.customer?.trade_type, customer?.phase,
                snapshot.business.offer_ref, snapshot.business.collection_fingerprint,
                snapshot.business.details.quote.status, snapshot.business.details.quote.mode,
                snapshot.business.details.quote.displayed_total, execution.Request.ItemRefs, items,
                ContainersComplete(snapshot), InventoryIdentity(execution.BackInventory), InventoryIdentity(execution.SoldInventory));
            return true;
        }

        private bool HasFreshSingleItemQuote(Execution execution, Snapshot snapshot, string itemRef)
        {
            var business = snapshot.business;
            var quote = business?.details?.quote;
            if (business == null || business.customer_ref != execution.Command.arguments.customer_ref ||
                business.details?.customer?.trade_type != "buy" || business.details.customer.phase != "trading" ||
                string.IsNullOrEmpty(business.offer_ref) || business.offer_ref == execution.BeforeOfferRef ||
                string.IsNullOrEmpty(business.collection_fingerprint) ||
                business.collection_fingerprint == execution.BeforeCollectionFingerprint ||
                business.item_refs == null || business.item_refs.Count != 1 || business.item_refs[0] != itemRef ||
                quote == null || quote.status != "complete" || quote.mode != "normal" || !quote.displayed_total.HasValue)
                return false;
            return projection.TryResolvePurchase(snapshot.world_epoch, business.customer_ref, business.offer_ref,
                    business.collection_fingerprint, out var nativeItems, out var refs, out int displayedTotal) &&
                nativeItems.Length == 1 && ReferenceEquals(nativeItems[0], execution.Target) &&
                refs.Length == 1 && refs[0] == itemRef && displayedTotal == quote.displayed_total.Value;
        }

        private static bool IsPrimarySaleButtonReady(NegociationUIManager negotiation)
        {
            var primary = negotiation == null ? null : PrimarySaleButtonField?.GetValue(negotiation) as TabletButton;
            var button = primary?.button;
            return primary != null && primary.Interactable && primary.gameObject != null &&
                primary.gameObject.activeInHierarchy && button != null && button.interactable &&
                button.IsInteractable() && button.gameObject.activeInHierarchy;
        }

        private void AppendItems(List<SaleItemEvidence> output, List<GameItem> nativeItems, string container,
            Snapshot snapshot, Execution execution)
        {
            foreach (var item in nativeItems)
            {
                if (item == null) continue;
                string refValue = ReferenceEquals(item, execution.Target) ? execution.Request.ItemRefs[0] :
                    projection.TryGetItemReference(snapshot.world_epoch, container == "sold" ? "back" : container,
                        item, out var observedRef) ? observedRef : "local:" + Identity(item);
                output.Add(new SaleItemEvidence(refValue, Identity(item), container, item.unitCount,
                    GeneralHelper.IsItemOwned(item)));
            }
        }

        private static bool TryGetSaleArguments(Snapshot snapshot, out ActionArguments args)
        {
            args = null;
            var business = snapshot.business;
            var quote = business?.details?.quote;
            if (business?.details?.customer?.trade_type != "buy" || business.details.customer.phase != "trading" ||
                string.IsNullOrEmpty(business.customer_ref) || string.IsNullOrEmpty(business.offer_ref) ||
                string.IsNullOrEmpty(business.collection_fingerprint) || quote == null || quote.status != "complete" ||
                quote.mode != "normal" || !quote.displayed_total.HasValue || business.item_refs == null || business.item_refs.Count != 1)
                return false;
            args = new ActionArguments { customer_ref = business.customer_ref, offer_ref = business.offer_ref,
                collection_fingerprint = business.collection_fingerprint, item_refs = new List<string>(business.item_refs),
                mode = "normal", amount = quote.displayed_total.Value };
            return true;
        }

        private static string SalePreparationFingerprint(Snapshot snapshot, string customerRef, ItemState item) =>
            BusinessCollection.Fingerprint(snapshot.world_epoch, customerRef, "buy",
                new[] { new BusinessCollectionMember { item_ref = item.item_ref, container = "player",
                    units = item.units, owned = item.owned } }, "unknown", "unverified", null);

        private bool IsOrdinaryUiReady(Snapshot snapshot) => snapshot != null && !snapshot.menu_open &&
            snapshot.business?.details?.ui?.blockers != null &&
            KnownUnobservableBlockersOnly(snapshot.business.details.ui.blockers) &&
            NegociationUIManager.Instance != null && NegociationUIManager.Instance.uiObject != null &&
            NegociationUIManager.Instance.uiObject.activeInHierarchy && !UnsafeVisibleModal();

        private bool BaseReady(Snapshot snapshot) => snapshot != null && snapshot.ready &&
            snapshot.schema_version == Protocol.BusinessSchemaVersion && snapshot.loaded &&
            !string.IsNullOrEmpty(snapshot.world_epoch) &&
            snapshot.business?.details != null && snapshot.business.details.customer != null &&
            snapshot.business.details.ui != null && snapshot.legal_actions != null;

        private static bool TryGetContainer(Snapshot snapshot, string name, out ContainerState result)
        {
            result = FindContainer(snapshot, name);
            return ContainerComplete(result);
        }

        private static ContainerState FindContainer(Snapshot snapshot, string name) =>
            snapshot?.business?.details?.containers?.SingleOrDefault(c => c != null && c.container == name);

        private static bool ContainerComplete(ContainerState state) => state != null && state.completeness == "complete" &&
            state.item_refs != null && state.item_refs.Count <= 64 &&
            state.item_refs.Distinct(StringComparer.Ordinal).Count() == state.item_refs.Count;

        private static bool ContainersComplete(Snapshot snapshot) => TryGetContainer(snapshot, "back", out _) &&
            TryGetContainer(snapshot, "player", out _);

        private bool MatchesProjected(Snapshot snapshot, string name, ContainerState projected, List<GameItem> native)
        {
            if (!ContainerComplete(projected) || native == null || native.Count != projected.item_refs.Count) return false;
            var resolved = new HashSet<GameItem>(GameItemComparer.Instance);
            foreach (string itemRef in projected.item_refs)
                if (!projection.TryResolveItem(snapshot.world_epoch, snapshot.business.customer_ref, name,
                    itemRef, out var value) || !(value is GameItem item) || !native.Any(candidate => ReferenceEquals(candidate, item)) ||
                    !resolved.Add(item)) return false;
            return true;
        }

        private Dictionary<GameItem, SaleItemEvidence> CaptureDirectFacts(GameItem[] player, GameItem[] back, GameItem[] sold)
        {
            var result = new Dictionary<GameItem, SaleItemEvidence>(GameItemComparer.Instance);
            AddFacts(result, player, "player"); AddFacts(result, back, "back"); AddFacts(result, sold, "sold");
            return result;
        }

        private void AddFacts(Dictionary<GameItem, SaleItemEvidence> result, GameItem[] items, string container)
        {
            foreach (var item in items ?? Array.Empty<GameItem>())
                if (item != null && !result.ContainsKey(item))
                    result.Add(item, new SaleItemEvidence("local:" + Identity(item), Identity(item), container,
                        item.unitCount, GeneralHelper.IsItemOwned(item)));
        }

        private bool SameRemainingObjectsAndFacts(Execution execution, List<GameItem> after, GameItem moved)
        {
            var expected = execution.BeforePlayer.Where(item => !ReferenceEquals(item, moved)).ToArray();
            if (expected.Length != after.Count || !expected.All(item => after.Any(candidate => ReferenceEquals(item, candidate)))) return false;
            foreach (var item in expected)
                if (!execution.BeforeItemFacts.TryGetValue(item, out var before) || item.unitCount != before.Units ||
                    GeneralHelper.IsItemOwned(item) != before.Owned) return false;
            return true;
        }

        private bool SameObjectsAndFacts(Execution execution, GameItem[] before, List<GameItem> after, string container)
        {
            if (before == null || after == null || before.Length != after.Count ||
                !before.All(item => after.Any(candidate => ReferenceEquals(item, candidate)))) return false;
            foreach (var item in before)
                if (!execution.BeforeItemFacts.TryGetValue(item, out var old) || old.Container != container ||
                    item.unitCount != old.Units || GeneralHelper.IsItemOwned(item) != old.Owned) return false;
            return true;
        }

        private static bool ItemDtoMatches(Snapshot snapshot, string itemRef, string container, int units, bool owned)
        {
            var item = snapshot?.business?.details?.items?.SingleOrDefault(entry => entry != null && entry.item_ref == itemRef);
            return item != null && item.container == container && item.units == units && item.owned == owned;
        }

        private static bool KnownUnobservableBlockersOnly(List<string> blockers)
        {
            foreach (var blocker in blockers)
                if (blocker != "global_ui_stack_unobservable" && blocker != "global_raycast_order_unobservable" &&
                    blocker != "quote_rebuild_completion_unobservable" && blocker != "tutorial_group_active_unverified" &&
                    blocker != "control_hints_active_unverified") return false;
            return true;
        }

        private static bool UnsafeVisibleModal()
        {
            var ui = StoreUIManager.Instance;
            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            if (ui == null || store == null ||
                (ui.escapeUIManager?.menuPanel != null && ui.escapeUIManager.menuPanel.activeInHierarchy) ||
                (ui.gameOptionUIManager != null && ui.gameOptionUIManager.IsOpen) ||
                (ui.confirmPanelUIManager?.panel != null && ui.confirmPanelUIManager.panel.activeInHierarchy) ||
                (ui.closeShutterConfirmationPanel != null && ui.closeShutterConfirmationPanel.activeInHierarchy) ||
                ui.isShot || store.isHandlingShootout || store.isClientBeingArrested) return true;
            var generic = GenericConfirmationUIManager.Instance;
            if (generic?.panel != null && generic.panel.activeInHierarchy) return true;
            var bargain = BargainUIManager.Instance;
            if (bargain != null && bargain.IsOpen) return true;
            var dialogue = DialogUIManager.Instance;
            return dialogue != null && (dialogue.currentDialog != null || dialogue.isWaitingForManualAdvance);
        }

        private string Identity(GameItem item)
        {
            if (!identities.TryGetValue(item, out var id)) identities[item] = id = "sale-object-" + (++nextIdentity).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return id;
        }

        private static string InventoryIdentity(GameGridInventory inventory) => inventory == null ? null :
            "sale-inventory-" + RuntimeHelpers.GetHashCode(inventory).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private VerificationDecision Finish(string commandId, VerificationDecision decision)
        {
            if (decision.outcome != "pending") executions.Remove(commandId);
            return decision;
        }

        private static Command CommandFor(Snapshot snapshot, string action, ActionArguments args) => new Command
        {
            schema_version = Protocol.BusinessSchemaVersion, id = "sale-candidate", action = action,
            expected_world_epoch = snapshot.world_epoch, expected_version = snapshot.version,
            expected_revision = snapshot.revision, arguments = args
        };
    }
}
