using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace PawnPilot
{
    // Captures only copied scalar facts. Game objects stay inside this runtime adapter and
    // are used solely as keys for the caller's already-issued opaque item references.
    internal sealed class GameCapacityCapture
    {
        private readonly ReadOnlyDictionary<GameItem, CapacityItemSnapshot> itemFactsByItem;

        internal CapacitySnapshot Snapshot { get; }
        internal CapacityProofResult Proof { get; }
        internal CapacityProofStatus Status => Proof?.Status ?? CapacityProofStatus.Unverified;
        internal CapacityProofReason Reason => Proof?.Reason ?? CapacityProofReason.IncompleteSnapshot;
        internal string TargetInventoryRef { get; }
        internal string SourceInventoryRef { get; }
        internal IReadOnlyDictionary<GameItem, CapacityItemSnapshot> ItemFactsByItem => itemFactsByItem;

        internal GameCapacityCapture(CapacitySnapshot snapshot, CapacityProofResult proof,
            string targetInventoryRef, string sourceInventoryRef,
            IDictionary<GameItem, CapacityItemSnapshot> itemFacts)
        {
            Snapshot = snapshot;
            Proof = proof;
            TargetInventoryRef = targetInventoryRef;
            SourceInventoryRef = sourceInventoryRef;
            var copy = new Dictionary<GameItem, CapacityItemSnapshot>(GameCapacityReader.GameItemReferenceComparer.Instance);
            if (itemFacts != null)
                foreach (var pair in itemFacts) copy.Add(pair.Key, pair.Value);
            itemFactsByItem = new ReadOnlyDictionary<GameItem, CapacityItemSnapshot>(copy);
        }
    }

    // Runtime facts for the one current ordinary in-hours purchase receiver. This class never
    // calls CanAccept, TryFindOneValidInventorySlot, TryInventorySlot, MaxNumAdd/Remove,
    // MayStack, CheckIsStateValid, TryAcceptAll, or any delegate stored on an item/inventory.
    internal static class GameCapacityReader
    {
        private const int MaxDirectItems = 64;
        private const int ExpectedGridWidth = 24;
        private const int ExpectedGridHeight = 10;
        private const string SupportedApplicationVersion = "0.46D";
        private const string SupportedContentVersion = "VERSION 100-Rev6";

        private static readonly object InventoryRefLock = new object();
        private static readonly Dictionary<GameInventory, string> InventoryRefs =
            new Dictionary<GameInventory, string>(GameInventoryReferenceComparer.Instance);
        private static string refWorldEpoch;
        private static long nextInventoryRef;

        // The caller passes the copied list it will hand to the native purchase handler.
        // Capacity evidence describes copied grid/item placement facts. The observed client
        // callback fact is recorded accurately but does not imply callbacks are side-effect-free.
        internal static GameCapacityCapture Capture(EmporiumEntry entry,
            IReadOnlyList<GameItem> actualOrderedPurchaseItems,
            Func<GameItem, string> itemRefResolver,
            string worldEpoch, int worldRevision, bool multiBuyDisabled,
            bool nativeSelectionVerified = false,
            CapacityFact clientOnItemSoldCallbackPresent = CapacityFact.Unknown)
        {
            string targetRef = null;
            string sourceRef = null;
            var itemFacts = new Dictionary<GameItem, CapacityItemSnapshot>(GameItemReferenceComparer.Instance);
            try
            {
                if (entry == null || actualOrderedPurchaseItems == null || itemRefResolver == null ||
                    string.IsNullOrWhiteSpace(worldEpoch) || worldRevision < 0)
                    return Failure(CapacityProofReason.MissingIdentity, itemFacts);
                if (UnityEngine.Application.version != SupportedApplicationVersion || PlayerStore.VERSION != SupportedContentVersion)
                    return Failure(CapacityProofReason.UnknownBuild, itemFacts);

                GameGridInventory target = entry.invElement;
                GameGridInventory source = entry.backInvinvElement;
                if (target == null || source == null || ReferenceEquals(target, source) ||
                    target.GetType() != typeof(GameGridInventory) || source.GetType() != typeof(GameGridInventory))
                    return Failure(CapacityProofReason.WrongTarget, itemFacts);
                var map = MapUIManager.Instance;
                if (map == null || map.isOutside)
                    return Failure(CapacityProofReason.WrongTarget, itemFacts);

                targetRef = GetInventoryRef(target, worldEpoch);
                sourceRef = GetInventoryRef(source, worldEpoch);
                if (!ValidOpaqueRef(targetRef) || !ValidOpaqueRef(sourceRef) || targetRef == sourceRef)
                    return Failure(CapacityProofReason.MissingIdentity, itemFacts, targetRef, sourceRef);

                var targetGrid = target.inventoryShape;
                if (targetGrid == null || targetGrid.GetType() != typeof(GridShapeBuilder) ||
                    targetGrid.width != ExpectedGridWidth || targetGrid.height != ExpectedGridHeight)
                    return Failure(CapacityProofReason.UnsupportedGrid, itemFacts, targetRef, sourceRef);

                bool rulesKnown = TargetRulesAreKnown(entry, target) && SourceRulesAreKnown(source);
                var allowedCells = new bool[ExpectedGridWidth * ExpectedGridHeight];
                bool gridComplete = true;
                for (int y = 0; y < ExpectedGridHeight; y++)
                {
                    for (int x = 0; x < ExpectedGridWidth; x++)
                    {
                        byte cell;
                        if (!TryGridCell(targetGrid, x, y, out cell) || cell != 0)
                        {
                            gridComplete = false;
                            continue;
                        }
                        // The native full rectangle created by EmporiumEntry.Start is zero
                        // inside; CheckShapePlacement treats only those zero cells as usable.
                        allowedCells[y * ExpectedGridWidth + x] = true;
                    }
                }

                List<GameItem> targetMembers;
                List<GameItem> sourceMembers;
                bool targetComplete = TryCopyChildren(target, out targetMembers);
                bool sourceComplete = TryCopyChildren(source, out sourceMembers);
                if (!targetComplete) targetMembers = targetMembers ?? new List<GameItem>();
                if (!sourceComplete) sourceMembers = sourceMembers ?? new List<GameItem>();

                var targetFacts = new List<CapacityItemSnapshot>(targetMembers.Count);
                var sourceFacts = new List<CapacityItemSnapshot>(sourceMembers.Count);
                var allSeen = new HashSet<GameItem>(GameItemReferenceComparer.Instance);
                foreach (GameItem item in targetMembers)
                {
                    if (item == null || !allSeen.Add(item) ||
                        !TryCaptureItem(item, target, targetRef, itemRefResolver, true,
                            ExpectedGridWidth, ExpectedGridHeight, out var fact))
                    {
                        targetComplete = false;
                        continue;
                    }
                    targetFacts.Add(fact);
                    itemFacts.Add(item, fact);
                }
                foreach (GameItem item in sourceMembers)
                {
                    if (item == null || !allSeen.Add(item) ||
                        !TryCaptureItem(item, source, sourceRef, itemRefResolver, false,
                            ExpectedGridWidth, ExpectedGridHeight, out var fact))
                    {
                        sourceComplete = false;
                        continue;
                    }
                    sourceFacts.Add(fact);
                    itemFacts.Add(item, fact);
                }

                var expectedOrder = new List<string>();
                var expectedItems = new List<GameItem>();
                for (int i = 0; i < sourceMembers.Count; i++)
                {
                    GameItem item = sourceMembers[i];
                    if (item == null) { sourceComplete = false; continue; }
                    CapacityItemSnapshot fact;
                    if (!itemFacts.TryGetValue(item, out fact)) { sourceComplete = false; continue; }
                    if (fact.IsOwned) continue;
                    expectedOrder.Add(fact.ItemRef);
                    expectedItems.Add(item);
                }

                CapacitySelectionProfile selectionProfile = CapacitySelectionProfile.AllNonOwnedBackInventoryItems;
                if (multiBuyDisabled && expectedItems.Count > 1)
                {
                    // The native handler reduces disabled multi-buy to its selectedItem or
                    // index 0 fallback. The caller may certify that exact public-row mapping;
                    // this reader still verifies the chosen object is a direct non-owned child.
                    if (!nativeSelectionVerified || actualOrderedPurchaseItems.Count != 1)
                        return Failure(CapacityProofReason.PurchaseListMismatch, itemFacts, targetRef, sourceRef);
                    int selectedIndex = -1;
                    for (int i = 0; i < expectedItems.Count; i++)
                    {
                        if (ReferenceEquals(actualOrderedPurchaseItems[0], expectedItems[i]))
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                    if (selectedIndex < 0)
                        return Failure(CapacityProofReason.PurchaseListMismatch, itemFacts, targetRef, sourceRef);
                    expectedOrder = new List<string> { expectedOrder[selectedIndex] };
                    expectedItems = new List<GameItem> { expectedItems[selectedIndex] };
                    selectionProfile = CapacitySelectionProfile.ExplicitBackInventoryItems;
                }
                else if (!SameReferenceOrder(actualOrderedPurchaseItems, expectedItems))
                {
                    return Failure(CapacityProofReason.PurchaseListMismatch, itemFacts, targetRef, sourceRef);
                }
                if (expectedOrder.Count == 0)
                    return Failure(CapacityProofReason.PurchaseListMismatch, itemFacts, targetRef, sourceRef);

                var snapshot = new CapacitySnapshot(worldEpoch, worldRevision, targetRef, sourceRef,
                    CapacityBuildProfile.SupportedGame26337366CoreRules,
                    CapacityTargetProfile.EmporiumMainInventoryInHours,
                    rulesKnown ? CapacityInventoryCallbackProfile.VanillaEmporiumMainInventory : CapacityInventoryCallbackProfile.Unknown,
                    rulesKnown ? CapacityInventoryCallbackProfile.VanillaEmporiumBackInventory : CapacityInventoryCallbackProfile.Unknown,
                    target.IsInsertLocked() ? CapacityFact.Yes : CapacityFact.No,
                    clientOnItemSoldCallbackPresent,
                    targetComplete && gridComplete, sourceComplete,
                    ExpectedGridWidth, ExpectedGridHeight, allowedCells,
                    targetFacts, sourceFacts, expectedOrder,
                    selectionProfile);
                CapacityProofResult proof = CapacityProof.Evaluate(snapshot);
                return new GameCapacityCapture(snapshot, proof, targetRef, sourceRef, itemFacts);
            }
            catch
            {
                // Any unexpected getter, callback metadata, or list behavior yields no proof.
                return Failure(CapacityProofReason.IncompleteSnapshot, itemFacts, targetRef, sourceRef);
            }
        }

        private static bool TryCaptureItem(GameItem item, GameInventory expectedParent,
            string expectedParentRef, Func<GameItem, string> itemRefResolver, bool captureOccupancy,
            int gridWidth, int gridHeight, out CapacityItemSnapshot fact)
        {
            fact = null;
            try
            {
                if (item == null || !ReferenceEquals(item.parentInventory, expectedParent)) return false;
                string itemRef = itemRefResolver(item);
                string identifier = item.identifier;
                int unitCount = item.unitCount;
                bool owned = GeneralHelper.IsItemOwned(item);
                if (!ValidOpaqueRef(itemRef) || string.IsNullOrWhiteSpace(identifier) || unitCount <= 0) return false;

                GridShape shapeValue = item.modifiedShape;
                CapacityShapeSnapshot shape = CaptureShape(shapeValue, captureOccupancy,
                    gridWidth, gridHeight, out var occupiedGridIndexes);
                if (shape == null) return false;

                CapacityFeatureProfile featureProfile = item.itemFeatures == null
                    ? CapacityFeatureProfile.Unknown
                    : item.itemFeatures.Count == 0 ? CapacityFeatureProfile.Empty : CapacityFeatureProfile.Present;
                // get_children includes both content and examination windows. A window with
                // no childElement contributes no storage; avoid traversing hidden contents
                // or invoking MachineryContainer.CollectStorage on nonempty/custom graphs.
                CapacityNestedProfile nestedProfile =
                    (item.contentWindow == null || item.contentWindow.childElement == null) &&
                    (item.examineWindow == null || item.examineWindow.childElement == null)
                    ? CapacityNestedProfile.EmptyByNativeFactory : CapacityNestedProfile.Present;
                CapacityFact stackAbsent = item.mayStackItemFunc == null ? CapacityFact.Yes : CapacityFact.No;
                CapacityFact passthroughAbsent = item.slotSearchPassthroughForItemFunc == null
                    ? CapacityFact.Yes : CapacityFact.No;
                bool excludedFromNormalGridReceive = string.Equals(identifier, "tutorial_page", StringComparison.Ordinal) ||
                    item.IsTag(HopperHelper.INTAKE_TAG) || item.IsTag(HopperHelper.OUTLET_TAG);
                bool incomingCallbacksSimple = !excludedFromNormalGridReceive &&
                    HasSimpleIncomingCallbacks(item, expectedParent);
                CapacityItemCallbackProfile itemCallbackProfile = incomingCallbacksSimple
                    ? CapacityItemCallbackProfile.VanillaSimpleGridReceive : CapacityItemCallbackProfile.Unknown;

                fact = new CapacityItemSnapshot(itemRef, identifier, expectedParentRef,
                    true, unitCount, owned, itemCallbackProfile, featureProfile, nestedProfile,
                    stackAbsent, shape, captureOccupancy ? occupiedGridIndexes : null, passthroughAbsent);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static CapacityShapeSnapshot CaptureShape(GridShape shapeValue, bool captureOccupancy,
            int gridWidth, int gridHeight, out List<int> occupiedGridIndexes)
        {
            occupiedGridIndexes = null;
            if (shapeValue == null || shapeValue.GetType() != typeof(GridShapeBuilder)) return null;
            var shape = (GridShapeBuilder)shapeValue;
            int width = shape.width;
            int height = shape.height;
            int orientation = shape.orientation;
            if (width <= 0 || height <= 0 || orientation < 0 || orientation > 3 ||
                (long)width * height > 4096) return null;

            var localCells = new bool[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte cell = shape.GetLocal(x, y);
                    if (cell > 1) return null;
                    localCells[y * width + x] = cell == 1;
                }
            }

            if (!captureOccupancy)
            {
                return new CapacityShapeSnapshot(true, width, height, shape.minX, shape.minY,
                    shape.flipped, orientation, localCells);
            }

            int minX = shape.minX;
            int minY = shape.minY;
            int globalWidth = shape.globalWidth;
            int globalHeight = shape.globalHeight;
            if (globalWidth <= 0 || globalHeight <= 0 ||
                (long)globalWidth * globalHeight > 4096) return null;
            var indexes = new List<int>();
            for (int localY = 0; localY < globalHeight; localY++)
            {
                for (int localX = 0; localX < globalWidth; localX++)
                {
                    byte cell = shapeValue.Get(minX + localX, minY + localY);
                    if (cell > 1) return null;
                    if (cell == 1) indexes.Add((minY + localY) * gridWidth + minX + localX);
                }
            }
            occupiedGridIndexes = indexes;
            return new CapacityShapeSnapshot(true, width, height, minX, minY,
                shape.flipped, orientation, localCells);
        }

        private static bool HasSimpleIncomingCallbacks(GameItem item, GameInventory source)
        {
            // These are the delegates reached by native source removal, receiver eligibility,
            // stack/pass-through search, shape/state validation, and one-cell amount limits.
            // Read only identities/nullness; never invoke them. GameItem's constructor always
            // installs one known item-local feature-cleanup callback in onSlotAddItemFunc.
            return item.mayInventoryAddItemFunc == null &&
                item.mayInventoryRemoveItemFunc == null &&
                item.mayStackItemFunc == null &&
                item.maxSlotAddItemFunc == null &&
                item.maxSlotRemoveItemFunc == null &&
                item.maxSlotAmountItemFunc == null &&
                item.minSlotAmountItemFunc == null &&
                item.slotSearchPassthroughForItemFunc == null &&
                item.slotPriorityFunc == null &&
                item.parentItemShapeModifierForItemFunc == null &&
                item.parentItemStateModifierForItemFunc == null &&
                item.isItemStateValidFunc == null &&
                HasOnlyNativeDefaultItemAddCallback(item.onSlotAddItemFunc, item) &&
                item.onSlotRemoveItemFunc == null &&
                item.onSlotChangeAmountItemFunc == null &&
                item.onChangeAmountItemFunc == null &&
                source.minSlotAmountItemFunc == null &&
                source.maxSlotRemoveItemFunc == null &&
                source.mayInventoryRemoveItemFunc == null &&
                source.parentItemShapeModifierForItemFunc == null &&
                source.parentItemStateModifierForItemFunc == null &&
                source.isItemStateValidFunc == null;
        }

        private static bool HasOnlyNativeDefaultItemAddCallback(
            Action<GameItem, GameInventory, SlotMarker> callback, GameItem item)
        {
            if (callback == null || item == null) return false;
            Delegate[] invocationList = callback.GetInvocationList();
            if (invocationList.Length != 1) return false;
            var method = invocationList[0].Method;
            return method != null && method.Name == "<.ctor>b__177_0" &&
                method.DeclaringType != null && method.DeclaringType.FullName == "GameItem" &&
                ReferenceEquals(invocationList[0].Target, item);
        }

        private static bool TargetRulesAreKnown(EmporiumEntry entry, GameGridInventory target)
        {
            // Current-build EmporiumEntry.Start installs this ownership gate and the following
            // constant-priority and UI bookkeeping hooks on its main 24x10 inventory.
            var addGate = target.mayInventoryAddItemFunc;
            return IsDelegate(addGate, "<Start>b__184_12", "EmporiumEntry", entry) &&
                IsSingletonDelegate(target.slotPriorityFunc, "<Start>b__184_6") &&
                IsSingletonDelegate(target.onSlotAddItemEndFunc, "<Start>b__184_19") &&
                target.slotSearchPassthroughForItemFunc == null &&
                target.onSlotAddItemFunc == null && target.onSlotAddItemLateFunc == null &&
                target.maxSlotAddItemFunc == null && target.maxSlotRemoveItemFunc == null &&
                target.maxSlotAmountItemFunc == null && target.minSlotAmountItemFunc == null &&
                target.mayInventoryRemoveItemFunc == null &&
                target.parentItemShapeModifierForItemFunc == null &&
                target.parentItemStateModifierForItemFunc == null &&
                target.isItemStateValidFunc == null;
        }

        private static bool SourceRulesAreKnown(GameGridInventory source)
        {
            // Removal callbacks are checked by method identity only; their bodies are fixed in
            // the supported game DLL. No delegate from this source inventory is invoked here.
            return IsSingletonDelegate(source.slotPriorityFunc, "<Start>b__184_7") &&
                IsSingletonDelegate(source.onSlotRemoveItemEndFunc, "<Start>b__184_18") &&
                source.slotSearchPassthroughForItemFunc == null &&
                source.onSlotRemoveItemFunc == null && source.onSlotRemoveItemLateFunc == null &&
                source.minSlotAmountItemFunc == null && source.maxSlotRemoveItemFunc == null &&
                source.mayInventoryRemoveItemFunc == null &&
                source.parentItemShapeModifierForItemFunc == null &&
                source.parentItemStateModifierForItemFunc == null &&
                source.isItemStateValidFunc == null;
        }

        private static bool IsDelegate(Delegate value, string methodName, string declaringTypeName, object target)
        {
            if (value == null || value.GetInvocationList().Length != 1) return false;
            var method = value.Method;
            return method != null && method.Name == methodName && method.DeclaringType != null &&
                method.DeclaringType.FullName == declaringTypeName && ReferenceEquals(value.Target, target);
        }

        private static bool IsSingletonDelegate(Delegate value, string methodName)
        {
            if (value == null || value.GetInvocationList().Length != 1) return false;
            var method = value.Method;
            return method != null && method.Name == methodName && method.DeclaringType != null &&
                method.DeclaringType.FullName == "EmporiumEntry+<>c" && value.Target != null;
        }

        private static bool TryCopyChildren(GameInventory inventory, out List<GameItem> copy)
        {
            copy = null;
            try
            {
                List<GameItem> children = inventory.childItems;
                if (children == null || children.Count > MaxDirectItems) return false;
                copy = new List<GameItem>(children);
                return copy.Count <= MaxDirectItems;
            }
            catch
            {
                return false;
            }
        }

        private static bool SameReferenceOrder(IReadOnlyList<GameItem> actual, IReadOnlyList<GameItem> expected)
        {
            if (actual == null || expected == null || actual.Count != expected.Count) return false;
            for (int i = 0; i < expected.Count; i++)
                if (!ReferenceEquals(actual[i], expected[i])) return false;
            return true;
        }

        private static bool TryGridCell(GridShape grid, int x, int y, out byte cell)
        {
            cell = 0;
            try
            {
                cell = grid.Get(x, y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetInventoryRef(GameInventory inventory, string worldEpoch)
        {
            lock (InventoryRefLock)
            {
                if (!string.Equals(refWorldEpoch, worldEpoch, StringComparison.Ordinal))
                {
                    InventoryRefs.Clear();
                    refWorldEpoch = worldEpoch;
                    nextInventoryRef = 0;
                }
                string value;
                if (InventoryRefs.TryGetValue(inventory, out value)) return value;
                value = "inventory:capacity-" + (++nextInventoryRef).ToString(System.Globalization.CultureInfo.InvariantCulture);
                InventoryRefs.Add(inventory, value);
                return value;
            }
        }

        private static bool ValidOpaqueRef(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (char c in value)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') &&
                    !(c >= '0' && c <= '9') && c != '_' && c != '.' && c != ':' && c != '-') return false;
            return true;
        }

        private static GameCapacityCapture Failure(CapacityProofReason reason,
            IDictionary<GameItem, CapacityItemSnapshot> itemFacts,
            string targetRef = null, string sourceRef = null)
        {
            var proof = new CapacityProofResult(CapacityProofStatus.Unverified, reason, null, 0, 0);
            return new GameCapacityCapture(null, proof, targetRef, sourceRef, itemFacts);
        }

        internal sealed class GameItemReferenceComparer : IEqualityComparer<GameItem>
        {
            internal static readonly GameItemReferenceComparer Instance = new GameItemReferenceComparer();
            public bool Equals(GameItem x, GameItem y) => ReferenceEquals(x, y);
            public int GetHashCode(GameItem value) => RuntimeHelpers.GetHashCode(value);
        }

        private sealed class GameInventoryReferenceComparer : IEqualityComparer<GameInventory>
        {
            internal static readonly GameInventoryReferenceComparer Instance = new GameInventoryReferenceComparer();
            public bool Equals(GameInventory x, GameInventory y) => ReferenceEquals(x, y);
            public int GetHashCode(GameInventory value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
