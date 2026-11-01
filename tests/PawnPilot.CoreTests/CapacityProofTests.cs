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

        CheckRejected(check, "feature_state_unknown", Item("item:feature", SourceRef, false, feature: CapacityFeatureProfile.Present), CapacityProofReason.UnsupportedFeatures);
        CheckRejected(check, "item_callbacks_unknown", Item("item:callback", SourceRef, false, callback: CapacityItemCallbackProfile.Unknown), CapacityProofReason.UnsupportedCallbacks);
        CheckRejected(check, "stack_callback_unknown", Item("item:stack", SourceRef, false, stack: CapacityFact.Unknown), CapacityProofReason.UnsupportedCallbacks);
        CheckRejected(check, "unit_count_two_rejected", Item("item:count", SourceRef, false, unitCount: 2), CapacityProofReason.UnsupportedUnitCount);
        CheckRejected(check, "unknown_item_type_rejected", Item("item:unknown", SourceRef, false, identifier: "unknown_item"), CapacityProofReason.UnsupportedItem);
        CheckRejected(check, "nested_contents_unknown", Item("item:nested", SourceRef, false, nested: CapacityNestedProfile.Unknown), CapacityProofReason.UnsupportedNestedContent);
        CheckRejected(check, "rotated_shape_rejected", Item("item:rotated", SourceRef, false, orientation: 1), CapacityProofReason.UnsupportedOrientation);
        CheckRejected(check, "flipped_shape_rejected", Item("item:flipped", SourceRef, false, flipped: true), CapacityProofReason.UnsupportedOrientation);
        CheckRejected(check, "multi_cell_shape_rejected_even_with_free_area", Item("item:wide", SourceRef, false, shapeWidth: 2), CapacityProofReason.UnsupportedShape);

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

        var unknownClient = CopySnapshot(incomplete, clientCallback: CapacityFact.Unknown);
        CapacityProofResult unknownCallback = CapacityProof.Evaluate(unknownClient);
        check("capacity_unknown_client_callback_is_unverified", unknownCallback.Status == CapacityProofStatus.Unverified &&
            unknownCallback.Reason == CapacityProofReason.UnknownClientCallback);

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

    private static CapacityItemSnapshot Item(string itemRef, string parentRef, bool owned,
        string identifier = "common_medical", int unitCount = 1,
        CapacityItemCallbackProfile callback = CapacityItemCallbackProfile.VanillaCommonMedicalOrToiletPaper,
        CapacityFeatureProfile feature = CapacityFeatureProfile.Empty,
        CapacityNestedProfile nested = CapacityNestedProfile.EmptyByNativeFactory,
        CapacityFact stack = CapacityFact.Yes, int x = 0, int y = 0,
        int shapeWidth = 1, int shapeHeight = 1, int orientation = 0, bool flipped = false,
        CapacityShapeSnapshot shape = null)
    {
        shape ??= new CapacityShapeSnapshot(true, shapeWidth, shapeHeight, x, y, flipped, orientation,
            Enumerable.Repeat(true, shapeWidth * shapeHeight));
        return new CapacityItemSnapshot(itemRef, identifier, parentRef, true, unitCount, owned,
            callback, feature, nested, stack, shape);
    }
}
