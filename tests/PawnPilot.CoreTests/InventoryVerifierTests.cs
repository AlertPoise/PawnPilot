using System;
using System.Collections.Generic;
using PawnPilot;

public static class InventoryVerifierTests
{
    public static void Run(Action<string, bool> check)
    {
        var command = new Command { action = "sort_inventory_default", arguments = new ActionArguments { container = "player" } };
        var item = new object();
        var grid = new object();
        var before = Snapshot("world:1", 10, 500, "item:1", 0, 0, 1, 1);
        var sameLayout = Snapshot("world:1", 11, 500, "item:1", 0, 0, 1, 1);
        var nativeBefore = Evidence("world:1", 10, 500, grid, item, "item:1", 0, 0, 1);
        var nativeSame = Evidence("world:1", 11, 500, grid, item, "item:1", 0, 0, 1);

        check("inventory_sort_true_accepts_already_sorted_layout",
            InventoryVerifier.Verify(command, before, sameLayout, nativeBefore, nativeSame, true).outcome == "succeeded");
        check("inventory_sort_false_does_not_claim_completion",
            InventoryVerifier.Verify(command, before, sameLayout, nativeBefore, nativeSame, false).reason == "native_sort_not_completed");

        var moved = Snapshot("world:1", 11, 500, "item:1", 1, 0, 1, 1);
        var nativeMoved = Evidence("world:1", 11, 500, grid, item, "item:1", 1, 0, 1);
        check("inventory_sort_false_rejects_mutation",
            InventoryVerifier.Verify(command, before, moved, nativeBefore, nativeMoved, false).outcome == "uncertain");

        var rotatedSnapshot = Snapshot("world:1", 11, 500, "item:2", 0, 0, 1, 1);
        var rotated = Evidence("world:1", 11, 500, grid, item, "item:2", 0, 0, 2);
        check("inventory_sort_identity_survives_shape_ref_refresh",
            InventoryVerifier.Verify(command, before, rotatedSnapshot, nativeBefore, rotated, true).outcome == "succeeded");

        var extraIdentity = Evidence("world:1", 11, 500, grid, new object(), "item:1", 0, 0, 1);
        check("inventory_sort_rejects_membership_change",
            InventoryVerifier.Verify(command, before, sameLayout, nativeBefore, extraIdentity, true).outcome == "uncertain");
        check("inventory_sort_rejects_cash_change",
            InventoryVerifier.Verify(command, before, Snapshot("world:1", 11, 499, "item:1", 0, 0, 1, 1),
                nativeBefore, Evidence("world:1", 11, 499, grid, item, "item:1", 0, 0, 1), true).outcome == "uncertain");
        check("inventory_sort_rejects_world_change",
            InventoryVerifier.Verify(command, before, Snapshot("world:2", 11, 500, "item:1", 0, 0, 1, 1),
                nativeBefore, Evidence("world:2", 11, 500, grid, item, "item:1", 0, 0, 1), true).outcome == "uncertain");
        check("inventory_sort_rejects_wrong_container_args",
            InventoryVerifier.Verify(new Command { action = "sort_inventory_default", arguments = new ActionArguments() },
                before, sameLayout, nativeBefore, nativeSame, true).outcome == "uncertain");
    }

    private static Snapshot Snapshot(string world, int revision, int cash, string itemRef,
        int x, int y, int width, int height)
    {
        return new Snapshot
        {
            schema_version = Protocol.BusinessSchemaVersion,
            ready = true,
            world_epoch = world,
            scene = "InventoryScene",
            revision = revision,
            cash = cash,
            business = new BusinessObservation
            {
                details = new BusinessDetails
                {
                    containers = new List<ContainerState>
                    {
                        new ContainerState { container = "player", completeness = "complete", item_refs = new List<string> { itemRef } }
                    },
                    items = new List<ItemState>
                    {
                        new ItemState { item_ref = itemRef, container = "player", units = 1, owned = true,
                            shape_status = "known", width = width, height = height }
                    }
                }
            }
        };
    }

    private static InventorySortEvidence Evidence(string world, int revision, int cash,
        object inventory, object item, string itemRef, int x, int y, int orientation)
    {
        return new InventorySortEvidence(world, "InventoryScene", revision, cash, inventory,
            new[] { new InventorySortMemberEvidence(item, itemRef, 1, true, x, y, orientation,
                false, 1, 1, new byte[] { 1 }) });
    }
}
