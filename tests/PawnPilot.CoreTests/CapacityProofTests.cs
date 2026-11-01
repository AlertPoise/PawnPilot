using PawnPilot;
using System;
using System.Collections.Generic;
using System.Linq;

// Research-only fixtures for the deliberately narrow pure capacity simulator.
// These tests exercise its stated assumptions; they are not evidence that the
// simulator matches a live game build or that any real inventory accepts items.
public static class CapacityProofTests
{
    private const string TargetRef = "target:main";
    private const string SourceRef = "source:back";

    public static void Run(Action<string, bool> check)
    {
        var two = new[]
        {
            Item("item:a", SourceRef, false),
            Item("item:b", SourceRef, false, identifier: "toilet_paper")
        };
        CapacityProofResult wholeBatch = CapacityProof.Evaluate(Snapshot(two, new[] { "item:a", "item:b" }));
        check("capacity_two_simple_items_share_one_target_and_reverse_order", wholeBatch.Status == CapacityProofStatus.Sufficient &&
            wholeBatch.RequiredCellCount == 2 && wholeBatch.FreeCellCount == 240 &&
            wholeBatch.AcceptanceOrderRefs.SequenceEqual(new[] { "item:b", "item:a" }));
        check("capacity_proof_binds_target_epoch_revision_and_original_order", wholeBatch.IsBoundTo(TargetRef, "epoch-1", 7, new[] { "item:a", "item:b" }) &&
            !wholeBatch.IsBoundTo("offer:x", "epoch-1", 7, new[] { "item:a", "item:b" }) &&
            !wholeBatch.IsBoundTo(TargetRef, "epoch-1", 8, new[] { "item:a", "item:b" }) &&
            !wholeBatch.IsBoundTo(TargetRef, "epoch-1", 7, new[] { "item:b", "item:a" }));

        CapacityProofResult emptyTarget = CapacityProof.Evaluate(Snapshot(new[] { Item("item:one", SourceRef, false) }, new[] { "item:one" }));
        check("capacity_empty_target_accepts_one_supported_unit_in_model", emptyTarget.Status == CapacityProofStatus.Sufficient &&
            emptyTarget.FreeCellCount == 240 && emptyTarget.RequiredCellCount == 1);

        CapacityItemSnapshot[] nearlyFull = Occupants(239);
        CapacityProofResult shortage = CapacityProof.Evaluate(Snapshot(two, new[] { "item:a", "item:b" }, nearlyFull));
        check("capacity_shortage_is_distinct_from_unverified", shortage.Status == CapacityProofStatus.Insufficient &&
            shortage.Reason == CapacityProofReason.CapacityInsufficient && shortage.FreeCellCount == 1 && shortage.RequiredCellCount == 2);

        CapacityProofResult presentFeatures = CapacityProof.Evaluate(Snapshot(
            new[] { Item("item:feature", SourceRef, false, feature: CapacityFeatureProfile.Present) },
            new[] { "item:feature" }));
        check("capacity_existing_features_do_not_block_geometric_proof",
            presentFeatures.Status == CapacityProofStatus.Sufficient &&
            presentFeatures.Reason == CapacityProofReason.None && presentFeatures.RequiredCellCount == 1);
        CheckRejected(check, "feature_state_unknown",
            Item("item:unknown_features", SourceRef, false, feature: CapacityFeatureProfile.Unknown),
            CapacityProofReason.UnsupportedFeatures);
        CheckRejected(check, "item_callbacks_unknown", Item("item:callback", SourceRef, false, callback: CapacityItemCallbackProfile.Unknown), CapacityProofReason.UnsupportedCallbacks);
        CheckRejected(check, "stack_callback_unknown", Item("item:stack", SourceRef, false, stack: CapacityFact.Unknown), CapacityProofReason.UnsupportedCallbacks);
        CheckRejected(check, "search_passthrough_unknown", Item("item:passthrough", SourceRef, false,
            passthrough: CapacityFact.Unknown), CapacityProofReason.UnsupportedCallbacks);
        CheckRejected(check, "unit_count_two_rejected", Item("item:count", SourceRef, false, unitCount: 2), CapacityProofReason.UnsupportedUnitCount);
        CapacityProofResult noIdentifierAllowlist = CapacityProof.Evaluate(Snapshot(
            new[] { Item("item:unknown", SourceRef, false, identifier: "plain_rect_goods") }, new[] { "item:unknown" }));
        check("capacity_simple_incoming_items_are_not_identifier_whitelisted",
            noIdentifierAllowlist.Status == CapacityProofStatus.Sufficient);
        CheckRejected(check, "nested_contents_unknown", Item("item:nested", SourceRef, false, nested: CapacityNestedProfile.Unknown), CapacityProofReason.UnsupportedNestedContent);
        CheckRejected(check, "rotated_shape_rejected", Item("item:rotated", SourceRef, false, orientation: 1), CapacityProofReason.UnsupportedOrientation);
        CheckRejected(check, "flipped_shape_rejected", Item("item:flipped", SourceRef, false, flipped: true), CapacityProofReason.UnsupportedOrientation);
        CapacityProofResult multiCell = CapacityProof.Evaluate(Snapshot(
            new[] { Item("item:wide", SourceRef, false, identifier: "plain_rect_goods", shapeWidth: 2) },
            new[] { "item:wide" }));
        check("capacity_multi_cell_rectangle_supported", multiCell.Status == CapacityProofStatus.Sufficient &&
            multiCell.RequiredCellCount == 2 && multiCell.FreeCellCount == 240);
        CapacityProofResult rotatedOnly = CapacityProof.Evaluate(Snapshot(
            new[] { Item("item:rotated_only", SourceRef, false, shapeWidth: 1, shapeHeight: 11) },
            new[] { "item:rotated_only" }));
        check("capacity_rotation_only_fit_is_unverified", rotatedOnly.Status == CapacityProofStatus.Unverified &&
            rotatedOnly.Reason == CapacityProofReason.UnsupportedOrientation);

        var arbitraryExisting = Item("occupant:old-type", TargetRef, true, identifier: "unlisted_storage_item",
            unitCount: 8, callback: CapacityItemCallbackProfile.Unknown, feature: CapacityFeatureProfile.Present,
            nested: CapacityNestedProfile.Present, x: 0, y: 0, shapeWidth: 2);
        var rectangleAfterExisting = Item("item:rectangle", SourceRef, false,
            identifier: "unlisted_plain_goods", shapeWidth: 2);
        CapacityProofResult existingTypeAgnostic = CapacityProof.Evaluate(Snapshot(
            new[] { rectangleAfterExisting }, new[] { "item:rectangle" }, new[] { arbitraryExisting }));
        check("capacity_existing_items_use_real_occupancy_without_type_whitelist",
            existingTypeAgnostic.Status == CapacityProofStatus.Sufficient &&
            existingTypeAgnostic.FreeCellCount == 238);

        var rectangleBatch = new[]
        {
            Item("item:horizontal", SourceRef, false, shapeWidth: 2),
            Item("item:vertical", SourceRef, false, shapeHeight: 2)
        };
        CapacityProofResult nativeOrderRectangles = CapacityProof.Evaluate(Snapshot(
            rectangleBatch, new[] { "item:horizontal", "item:vertical" }));
        check("capacity_multicell_batch_reserves_shared_space_in_native_reverse_order",
            nativeOrderRectangles.Status == CapacityProofStatus.Sufficient &&
            nativeOrderRectangles.AcceptanceOrderRefs.SequenceEqual(new[] { "item:vertical", "item:horizontal" }) &&
            nativeOrderRectangles.RequiredCellCount == 4);

        var lShape = MaskItem("item:l", SourceRef, 2, 2,
            true, true,
            true, false);
        CapacityProofResult exactMaskFit = CapacityProof.Evaluate(Snapshot(
            new[] { lShape }, new[] { "item:l" }, OccupantsExcept(0, 1, 24)));
        check("capacity_nonrectangular_mask_fits_exact_three_cell_cavity",
            exactMaskFit.Status == CapacityProofStatus.Sufficient && exactMaskFit.FreeCellCount == 3 &&
            exactMaskFit.RequiredCellCount == 3);

        var oneCell = Item("item:small", SourceRef, false);
        CapacityProofResult lBeforeSmall = CapacityProof.Evaluate(Snapshot(
            new[] { oneCell, lShape }, new[] { "item:small", "item:l" }, OccupantsExcept(0, 1, 24, 25)));
        CapacityProofResult smallBeforeL = CapacityProof.Evaluate(Snapshot(
            new[] { lShape, oneCell }, new[] { "item:l", "item:small" }, OccupantsExcept(0, 1, 24, 25)));
        check("capacity_nonrectangular_first_fit_shares_mask_cells_in_native_reverse_order",
            lBeforeSmall.Status == CapacityProofStatus.Sufficient && lBeforeSmall.RequiredCellCount == 4 &&
            lBeforeSmall.AcceptanceOrderRefs.SequenceEqual(new[] { "item:l", "item:small" }));
        check("capacity_different_native_order_that_needs_rotation_is_unverified",
            smallBeforeL.Status == CapacityProofStatus.Unverified &&
            smallBeforeL.Reason == CapacityProofReason.UnsupportedOrientation);

        var emptyMask = MaskItem("item:empty-shape", SourceRef, 1, 1, false);
        CheckRejected(check, "empty_shape_mask", emptyMask, CapacityProofReason.UnsupportedShape);

        var duplicateOccupants = new[]
        {
            Item("occupant:a", TargetRef, true, x: 3, y: 4),
            Item("occupant:b", TargetRef, true, x: 3, y: 4)
        };
        CapacityProofResult duplicateCell = CapacityProof.Evaluate(Snapshot(
            new[] { Item("item:one", SourceRef, false) }, new[] { "item:one" }, duplicateOccupants));
        check("capacity_duplicate_occupied_cell_is_unverified", duplicateCell.Status == CapacityProofStatus.Unverified &&
            duplicateCell.Reason == CapacityProofReason.DuplicateOccupiedCell);

        var outsideGrid = new[] { Item("occupant:outside", TargetRef, true, x: 24, y: 0) };
        CapacityProofResult outOfBounds = CapacityProof.Evaluate(Snapshot(
            new[] { Item("item:one", SourceRef, false) }, new[] { "item:one" }, outsideGrid));
        check("capacity_out_of_bounds_occupant_is_unverified", outOfBounds.Status == CapacityProofStatus.Unverified &&
            outOfBounds.Reason == CapacityProofReason.UnsupportedShape);

        var incomplete = Snapshot(new[] { Item("item:one", SourceRef, false) }, new[] { "item:one" });
        var incompleteSnapshot = CopySnapshot(incomplete, sourceComplete: false);
        CapacityProofResult partial = CapacityProof.Evaluate(incompleteSnapshot);
        check("capacity_incomplete_inventory_snapshot_is_unverified", partial.Status == CapacityProofStatus.Unverified &&
            partial.Reason == CapacityProofReason.IncompleteSnapshot);

        var callbackPresent = CopySnapshot(incomplete, clientCallback: CapacityFact.Yes);
        CapacityProofResult callbackPlacement = CapacityProof.Evaluate(callbackPresent);
        check("capacity_client_callbacks_do_not_change_grid_placement_proof",
            callbackPlacement.Status == CapacityProofStatus.Sufficient &&
            callbackPlacement.Reason == CapacityProofReason.None);

        var cells = Enumerable.Repeat(true, 240).ToArray();
        var sourceList = new List<CapacityItemSnapshot> { Item("item:one", SourceRef, false) };
        var order = new List<string> { "item:one" };
        var mutableShapeCells = new[] { true };
        var copiedShape = new CapacityShapeSnapshot(true, 1, 1, 0, 0, false, 0, mutableShapeCells);
        sourceList[0] = Item("item:one", SourceRef, false, shape: copiedShape);
        var immutableInput = MakeSnapshot(cells, Array.Empty<CapacityItemSnapshot>(), sourceList, order);
        string originalFingerprint = immutableInput.Fingerprint;
        cells[0] = false;
        sourceList.Clear();
        order[0] = "item:other";
        mutableShapeCells[0] = false;
        CapacityProofResult immutableProof = CapacityProof.Evaluate(immutableInput);
        check("capacity_snapshot_copies_collections_and_shape_cells", immutableInput.AllowedCells[0] &&
            immutableInput.SourceDirectItems.Count == 1 && immutableInput.PurchaseListOrderRefs[0] == "item:one" &&
            immutableInput.SourceDirectItems[0].Shape.OccupiedCells[0]);
        check("capacity_immutable_snapshot_proof_remains_bound", immutableProof.Status == CapacityProofStatus.Sufficient &&
            immutableProof.IsBoundToSnapshot(immutableInput) && immutableInput.Fingerprint == originalFingerprint);
    }

    private static void CheckRejected(Action<string, bool> check, string name, CapacityItemSnapshot item,
        CapacityProofReason reason)
    {
        CapacityProofResult result = CapacityProof.Evaluate(Snapshot(new[] { item }, new[] { item.ItemRef }));
        check("capacity_" + name + "_is_unverified", result.Status == CapacityProofStatus.Unverified && result.Reason == reason);
    }

    private static CapacitySnapshot Snapshot(IReadOnlyList<CapacityItemSnapshot> sourceItems,
        IReadOnlyList<string> order, IReadOnlyList<CapacityItemSnapshot> targetItems = null)
    {
        return MakeSnapshot(Enumerable.Repeat(true, 240).ToArray(), targetItems ?? Array.Empty<CapacityItemSnapshot>(),
            sourceItems, order);
    }

    private static CapacitySnapshot MakeSnapshot(IEnumerable<bool> cells,
        IEnumerable<CapacityItemSnapshot> targetItems, IEnumerable<CapacityItemSnapshot> sourceItems,
        IEnumerable<string> order)
    {
        return new CapacitySnapshot("epoch-1", 7, TargetRef, SourceRef,
            CapacityBuildProfile.Vanilla26337366NoMods,
            CapacityTargetProfile.EmporiumMainInventoryInHours,
            CapacityInventoryCallbackProfile.VanillaEmporiumMainInventory,
            CapacityInventoryCallbackProfile.VanillaEmporiumBackInventory,
            CapacityFact.No, CapacityFact.No, true, true, 24, 10, cells,
            targetItems, sourceItems, order, CapacitySelectionProfile.ExplicitBackInventoryItems);
    }

    private static CapacitySnapshot CopySnapshot(CapacitySnapshot source,
        bool? sourceComplete = null, CapacityFact? clientCallback = null)
    {
        return new CapacitySnapshot(source.WorldEpoch, source.WorldRevision, source.TargetRef, source.SourceRef,
            source.BuildProfile, source.TargetProfile, source.TargetCallbackProfile, source.SourceCallbackProfile,
            source.TargetInsertLocked, clientCallback ?? source.ClientOnItemSoldCallbackPresent,
            source.TargetSnapshotComplete, sourceComplete ?? source.SourceSnapshotComplete,
            source.GridWidth, source.GridHeight, source.AllowedCells, source.TargetDirectItems,
            source.SourceDirectItems, source.PurchaseListOrderRefs, source.SelectionProfile);
    }

    private static CapacityItemSnapshot[] Occupants(int count)
    {
        var items = new CapacityItemSnapshot[count];
        for (int i = 0; i < count; i++)
            items[i] = Item("occupant:" + i, TargetRef, true, x: i % 24, y: i / 24);
        return items;
    }

    private static CapacityItemSnapshot[] OccupantsExcept(params int[] freeCells)
    {
        var free = new HashSet<int>(freeCells);
        var items = new List<CapacityItemSnapshot>(240 - free.Count);
        for (int cell = 0; cell < 240; cell++)
        {
            if (free.Contains(cell)) continue;
            items.Add(Item("occupant:" + cell, TargetRef, true, x: cell % 24, y: cell / 24));
        }
        return items.ToArray();
    }

    private static CapacityItemSnapshot MaskItem(string itemRef, string parentRef,
        int width, int height, params bool[] occupiedCells)
    {
        return Item(itemRef, parentRef, false, shape: new CapacityShapeSnapshot(true,
            width, height, 0, 0, false, 0, occupiedCells));
    }

    private static CapacityItemSnapshot Item(string itemRef, string parentRef, bool owned,
        string identifier = "common_medical", int unitCount = 1,
        CapacityItemCallbackProfile callback = CapacityItemCallbackProfile.VanillaCommonMedicalOrToiletPaper,
        CapacityFeatureProfile feature = CapacityFeatureProfile.Empty,
        CapacityNestedProfile nested = CapacityNestedProfile.EmptyByNativeFactory,
        CapacityFact stack = CapacityFact.Yes, int x = 0, int y = 0,
        int shapeWidth = 1, int shapeHeight = 1, int orientation = 0, bool flipped = false,
        CapacityShapeSnapshot shape = null, CapacityFact passthrough = CapacityFact.Yes)
    {
        shape ??= new CapacityShapeSnapshot(true, shapeWidth, shapeHeight, x, y, flipped, orientation,
            Enumerable.Repeat(true, shapeWidth * shapeHeight));
        IEnumerable<int> occupiedGridCells = null;
        if (parentRef == TargetRef)
        {
            var cells = new List<int>();
            for (int localY = 0; localY < shapeHeight; localY++)
                for (int localX = 0; localX < shapeWidth; localX++)
                    cells.Add((y + localY) * 24 + x + localX);
            occupiedGridCells = cells;
        }
        return new CapacityItemSnapshot(itemRef, identifier, parentRef, true, unitCount, owned,
            callback, feature, nested, stack, shape, occupiedGridCells, passthrough);
    }
}
