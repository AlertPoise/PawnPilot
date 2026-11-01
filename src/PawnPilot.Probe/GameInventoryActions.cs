using System;
using System.Collections.Generic;

namespace PawnPilot
{
    // Narrow native adapter for the game's fixed player-inventory default sort. It never accepts
    // caller-selected order flags or an inventory identity; the only target is EmporiumEntry's
    // currently visible main player grid.
    internal sealed class GameInventoryActions
    {
        private sealed class Execution
        {
            internal InventorySortEvidence Before;
            internal bool? NativeReturned;
        }

        private readonly BusinessProjection projection;
        private readonly Dictionary<string, Execution> executions = new Dictionary<string, Execution>(StringComparer.Ordinal);
        private readonly HashSet<string> consumedCommands = new HashSet<string>(StringComparer.Ordinal);
        internal long CompletionGeneration { get; private set; }

        internal GameInventoryActions(BusinessProjection businessProjection)
        {
            projection = businessProjection ?? throw new ArgumentNullException(nameof(businessProjection));
        }

        // Called during the main-thread snapshot capture. Incomplete/inactive player grids never
        // become an action candidate; candidate arguments have one exact, fixed shape.
        internal List<LegalAction> BuildLegalActions(Snapshot snapshot)
        {
            var actions = new List<LegalAction>();
            if (snapshot == null || snapshot.schema_version != Protocol.BusinessSchemaVersion || !snapshot.ready ||
                snapshot.business?.details?.customer == null || snapshot.business.details.customer.presence != "absent" ||
                snapshot.menu_open || (snapshot.phase != "MORNING" && snapshot.phase != "OPEN")) return actions;

            if (!TryCapture(snapshot, out _, out string reason))
            {
                snapshot.business.details.candidates.Add(new BusinessCandidate
                {
                    action = "sort_inventory_default", status = "blocked", reason = reason
                });
                return actions;
            }
            actions.Add(new LegalAction
            {
                action = "sort_inventory_default",
                arguments = new ActionArguments { container = "player" }
            });
            return actions;
        }

        // ActionGate calls this only after protocol admission. Every live identity, collection,
        // native sort gate, and callback scope is rechecked before entering the game method.
        internal void Invoke(Command command, Snapshot snapshot)
        {
            if (command?.id == null || command.action != "sort_inventory_default" ||
                !ValidArguments(command.arguments))
                throw new NativeActionRejectedException("inventory_sort_arguments_invalid");
            if (consumedCommands.Contains(command.id) || consumedCommands.Count >= Protocol.MaxCommands)
                throw new NativeActionRejectedException("inventory_sort_command_already_consumed");
            if (!TryCapture(snapshot, out var live, out string reason))
                throw new NativeActionRejectedException(reason ?? "player_inventory_unverified");
            if (!HasMatchingAction(snapshot, command))
                throw new NativeActionRejectedException("inventory_sort_candidate_missing");

            // Keep the execution record before native dispatch. If the call throws, verification
            // will have no clean-return evidence and ActionGate will preserve outcome_unknown.
            consumedCommands.Add(command.id);
            executions.Clear();
            var execution = new Execution { Before = live };
            executions.Add(command.id, execution);
            bool sorted = InventorySortHelper.Sort((GameGridInventory)live.InventoryIdentity, false, false);
            execution.NativeReturned = sorted;
            // Completion is observable even for an already-sorted grid; advance the owning
            // snapshot's revision without changing any game field or weakening stale checks.
            CompletionGeneration++;
        }

        internal VerificationDecision Verify(Command command, Snapshot before, Snapshot after)
        {
            if (command == null || command.action != "sort_inventory_default")
                return VerificationDecision.Uncertain("unsupported_inventory_verifier_action");
            if (!executions.TryGetValue(command.id ?? string.Empty, out var execution) || execution?.Before == null ||
                !execution.NativeReturned.HasValue)
                return VerificationDecision.Uncertain("inventory_sort_invocation_evidence_missing");
            if (!TryCapture(after, out var liveAfter, out string reason))
                return VerificationDecision.Pending();
            return InventoryVerifier.Verify(command, before, after, execution.Before, liveAfter,
                execution.NativeReturned.Value);
        }

        private bool TryCapture(Snapshot snapshot, out InventorySortEvidence evidence, out string reason)
        {
            evidence = null;
            reason = "player_inventory_unverified";
            if (snapshot == null || snapshot.schema_version != Protocol.BusinessSchemaVersion || !snapshot.ready ||
                string.IsNullOrEmpty(snapshot.world_epoch) || string.IsNullOrEmpty(snapshot.scene) ||
                snapshot.business?.details?.customer == null || snapshot.business.details.customer.presence != "absent" ||
                snapshot.menu_open || (snapshot.phase != "MORNING" && snapshot.phase != "OPEN"))
            { reason = "inventory_sort_state_unavailable"; return false; }

            try
            {
                var entry = EmporiumEntry.Instance;
                var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
                if (entry == null || store == null || MapUIManager.Instance == null || MapUIManager.Instance.isOutside ||
                    UnityEngine.Application.version != "0.46D" || PlayerStore.VERSION != "VERSION 100-Rev6")
                { reason = "inventory_sort_scene_unsupported"; return false; }
                var inventory = entry.invElement;
                var window = entry.invWindow;
                if (inventory == null || window == null || inventory.GetType() != typeof(GameGridInventory) ||
                    window.rectTransform == null || !window.IsVisible() || !window.rectTransform.gameObject.activeInHierarchy || inventory.rectTransform == null || !inventory.rectTransform.gameObject.activeInHierarchy ||
                    !ReferenceEquals(window.childElement, inventory) || inventory.inventoryShape == null ||
                    inventory.childItems == null || inventory.childItems.Count > 64)
                { reason = "player_inventory_window_unavailable"; return false; }
                if (HasBlockingModal(StoreUIManager.Instance, store) || HasLiveInputBlocker())
                { reason = "inventory_sort_ui_blocked"; return false; }

                var player = FindContainer(snapshot, "player");
                if (player == null || player.completeness != "complete" || player.item_refs == null ||
                    player.item_refs.Count != inventory.childItems.Count || player.item_refs.Count > 64)
                { reason = "player_inventory_incomplete"; return false; }
                if (!KnownInventoryCallbacks(inventory) || HasActionBlockingEvidence(snapshot.business.details.ui?.blockers))
                { reason = "inventory_sort_callback_scope_unverified"; return false; }

                var members = new List<InventorySortMemberEvidence>(inventory.childItems.Count);
                var seenRefs = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < inventory.childItems.Count; i++)
                {
                    GameItem item = inventory.childItems[i];
                    if (item == null || item.GetType() != typeof(GameItemElement) ||
                        !ReferenceEquals(item.parentInventory, inventory) ||
                        !projection.TryGetItemReference(snapshot.world_epoch, "player", item, out var itemRef) ||
                        !string.Equals(itemRef, player.item_refs[i], StringComparison.Ordinal) || !seenRefs.Add(itemRef) ||
                        !KnownItemCallbacks(item))
                    { reason = "player_inventory_member_unverified"; return false; }

                    var shape = item.modifiedShape;
                    if (shape == null || shape.GetType() != typeof(GridShapeBuilder) ||
                        shape.width <= 0 || shape.height <= 0 || shape.width > 64 || shape.height > 64)
                    { reason = "player_inventory_shape_unverified"; return false; }
                    var builder = (GridShapeBuilder)shape;
                    if (builder.globalWidth <= 0 || builder.globalHeight <= 0 ||
                        builder.globalWidth > 64 || builder.globalHeight > 64)
                    { reason = "player_inventory_shape_unverified"; return false; }
                    var itemState = FindItemState(snapshot, itemRef);
                    if (itemState == null || itemState.units != item.unitCount ||
                        itemState.owned != GeneralHelper.IsItemOwned(item) || itemState.shape_status != "known" ||
                        itemState.width != builder.globalWidth || itemState.height != builder.globalHeight)
                    { reason = "player_inventory_item_state_unverified"; return false; }
                    var cells = new byte[shape.width * shape.height];
                    for (int y = 0; y < shape.height; y++)
                        for (int x = 0; x < shape.width; x++) cells[y * shape.width + x] = shape.Get(x, y);
                    members.Add(new InventorySortMemberEvidence(item, itemRef, item.unitCount,
                        GeneralHelper.IsItemOwned(item), shape.minX, shape.minY,
                        shape.orientation, shape.flipped, builder.globalWidth, builder.globalHeight, cells));
                }

                // The native routine's own gates are safe to evaluate only after the item and
                // inventory callback closures above have been narrowed to this build's defaults.
                if (!InventorySortHelper.MaySort(inventory) || !InventorySortHelper.CanSort(inventory))
                { reason = "native_sort_precondition"; return false; }

                evidence = new InventorySortEvidence(snapshot.world_epoch, snapshot.scene, snapshot.revision,
                    snapshot.cash, inventory, members);
                reason = null;
                return true;
            }
            catch
            {
                evidence = null;
                reason = "player_inventory_capture_failed";
                return false;
            }
        }

        private static bool KnownInventoryCallbacks(GameGridInventory inventory)
        {
            // Match the current main-grid add/remove completion hooks and reject custom delegates
            // only where the native sort preflight or commit invokes them.
            return IsSingletonDelegate(inventory.onSlotAddItemEndFunc, "<Start>b__184_19") &&
                inventory.mayInventoryRemoveItemFunc == null && inventory.minSlotAmountItemFunc == null &&
                inventory.maxSlotRemoveItemFunc == null && inventory.parentItemShapeModifierForItemFunc == null &&
                inventory.parentItemStateModifierForItemFunc == null && inventory.isItemStateValidFunc == null &&
                inventory.onSlotAddItemFunc == null && inventory.onSlotAddItemLateFunc == null &&
                inventory.onSlotRemoveItemFunc == null && inventory.onSlotRemoveItemLateFunc == null &&
                IsSingletonDelegate(inventory.onSlotRemoveItemEndFunc, "<Start>b__184_20");
        }
        private static bool KnownItemCallbacks(GameItem item)
        {
            // Only reject custom hooks evaluated during sort preflight, removal, reacceptance,
            // or shape/state validation. Unrelated add/stack/search hooks are ignored.
            return item.mayInventoryRemoveItemFunc == null && item.minSlotAmountItemFunc == null &&
                item.maxSlotRemoveItemFunc == null && item.parentItemShapeModifierForItemFunc == null &&
                item.parentItemStateModifierForItemFunc == null && item.isItemStateValidFunc == null &&
                HasOnlyNativeDefaultItemAddCallback(item.onSlotAddItemFunc, item) &&
                item.onSlotRemoveItemFunc == null;
        }

        private static bool HasOnlyNativeDefaultItemAddCallback(Action<GameItem, GameInventory, SlotMarker> callback,
            GameItem item)
        {
            if (callback == null) return false;
            var list = callback.GetInvocationList();
            return list.Length == 1 && list[0].Method != null && list[0].Method.Name == "<.ctor>b__177_0" &&
                list[0].Method.DeclaringType?.FullName == "GameItem" && ReferenceEquals(list[0].Target, item);
        }

        private static bool IsDelegate(Delegate value, string methodName, string declaringTypeName, object target)
        {
            if (value == null || value.GetInvocationList().Length != 1) return false;
            return value.Method != null && value.Method.Name == methodName &&
                value.Method.DeclaringType?.FullName == declaringTypeName && ReferenceEquals(value.Target, target);
        }

        private static bool IsSingletonDelegate(Delegate value, string methodName)
        {
            if (value == null || value.GetInvocationList().Length != 1) return false;
            return value.Method != null && value.Method.Name == methodName &&
                value.Method.DeclaringType?.FullName == "EmporiumEntry+<>c" && value.Target != null;
        }

        private static bool ValidArguments(ActionArguments arguments) => arguments != null && arguments.container == "player" &&
            arguments.customer_ref == null && arguments.item_refs == null && arguments.offer_ref == null &&
            arguments.collection_fingerprint == null && arguments.option_ref == null && arguments.dialogue_ref == null &&
            arguments.mode == null && arguments.authorization_ref == null && !arguments.amount.HasValue &&
            !arguments.max_spend.HasValue;

        private static ItemState FindItemState(Snapshot snapshot, string itemRef) =>
            snapshot?.business?.details?.items?.Find(i => i != null && i.container == "player" && i.item_ref == itemRef);

        private static ContainerState FindContainer(Snapshot snapshot, string name) =>
            snapshot?.business?.details?.containers?.Find(c => c != null && c.container == name);

        private static bool HasMatchingAction(Snapshot snapshot, Command command)
        {
            if (snapshot?.legal_actions == null) return false;
            foreach (var action in snapshot.legal_actions)
                if (action?.action == command.action && ValidArguments(action.arguments) &&
                    ValidArguments(command.arguments)) return true;
            return false;
        }

        private static bool HasLiveInputBlocker()
        {
            var drag = ItemMouseDragHandler.current;
            var multi = ItemMultiSelectHandler.current;
            var select = ItemSelectHandler.current;
            return UIDragHandler.isDraggingAnyUI || (drag != null && drag.IsDraggingItem) ||
                (multi != null && (multi.HasSelection || multi.IsGroupDragging)) ||
                (select != null && select.IsEquipped);
        }

        private static bool HasActionBlockingEvidence(List<string> blockers)
        {
            if (blockers == null) return true;
            foreach (string blocker in blockers)
            {
                if (blocker == "global_ui_stack_unobservable" || blocker == "global_raycast_order_unobservable" ||
                    blocker == "quote_rebuild_completion_unobservable" || blocker == "tutorial_group_active_unverified" ||
                    blocker == "background_blocker_active_unverified" || blocker == "control_hints_active_unverified" ||
                    blocker == "darkness_root_power_mismatch") continue;
                return true;
            }
            return false;
        }
        private static bool HasBlockingModal(StoreUIManager ui, PlayerStore store)
        {
            if (ui == null || store == null || ui.isShot || store.isHandlingShootout || store.isClientBeingArrested || Interactable.isPaused)
                return true;
            if (ui.escapeUIManager?.menuPanel != null && ui.escapeUIManager.menuPanel.activeInHierarchy) return true;
            if (ui.gameOptionUIManager != null && ui.gameOptionUIManager.IsOpen) return true;
            if (ui.confirmPanelUIManager?.panel != null && ui.confirmPanelUIManager.panel.activeInHierarchy) return true;
            if (ui.closeShutterConfirmationPanel != null && ui.closeShutterConfirmationPanel.activeInHierarchy) return true;
            var generic = GenericConfirmationUIManager.Instance;
            if (generic != null && generic.panel != null && generic.panel.activeInHierarchy) return true;
            var bargain = BargainUIManager.Instance;
            return bargain != null && bargain.IsOpen;
        }
    }
}
