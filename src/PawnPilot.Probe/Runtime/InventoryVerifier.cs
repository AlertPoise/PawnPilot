using System;
using System.Collections.Generic;
using System.Linq;

namespace PawnPilot
{
    // Immutable, process-local arrangement facts captured around one call to the native default
    // sorter. Game objects are opaque identity tokens only; no Unity types are needed here.
    public sealed class InventorySortMemberEvidence
    {
        private readonly byte[] cells;
        public object ItemIdentity { get; }
        public string ItemRef { get; }
        public int Units { get; }
        public bool Owned { get; }
        public int X { get; }
        public int Y { get; }
        public int Orientation { get; }
        public bool Flipped { get; }
        public int Width { get; }
        public int Height { get; }
        public IReadOnlyList<byte> Cells => Array.AsReadOnly((byte[])cells.Clone());

        public InventorySortMemberEvidence(object itemIdentity, string itemRef, int units, bool owned,
            int x, int y, int orientation, bool flipped, int width, int height, IEnumerable<byte> cells)
        {
            ItemIdentity = itemIdentity;
            ItemRef = itemRef;
            Units = units;
            Owned = owned;
            X = x;
            Y = y;
            Orientation = orientation;
            Flipped = flipped;
            Width = width;
            Height = height;
            this.cells = cells == null ? Array.Empty<byte>() : cells.ToArray();
        }

        internal bool SameState(InventorySortMemberEvidence other) => other != null &&
            ReferenceEquals(ItemIdentity, other.ItemIdentity) && Units == other.Units && Owned == other.Owned;

        internal bool SamePlacement(InventorySortMemberEvidence other) => other != null &&
            X == other.X && Y == other.Y && Orientation == other.Orientation && Flipped == other.Flipped &&
            Width == other.Width && Height == other.Height && cells.SequenceEqual(other.cells);
    }

    public sealed class InventorySortEvidence
    {
        private readonly InventorySortMemberEvidence[] members;
        public string WorldEpoch { get; }
        public string Scene { get; }
        public int Revision { get; }
        public int Cash { get; }
        public object InventoryIdentity { get; }
        public IReadOnlyList<InventorySortMemberEvidence> Members =>
            Array.AsReadOnly((InventorySortMemberEvidence[])members.Clone());

        public InventorySortEvidence(string worldEpoch, string scene, int revision, int cash,
            object inventoryIdentity, IEnumerable<InventorySortMemberEvidence> members)
        {
            WorldEpoch = worldEpoch;
            Scene = scene;
            Revision = revision;
            Cash = cash;
            InventoryIdentity = inventoryIdentity;
            this.members = members == null ? Array.Empty<InventorySortMemberEvidence>() : members.ToArray();
        }

        internal InventorySortMemberEvidence[] CopyMembers() => (InventorySortMemberEvidence[])members.Clone();
    }

    public static class InventoryVerifier
    {
        // A clean true result proves native completion even for an already-sorted layout.
        // False means the native gates/packing failed, not that sorting completed.
        public static VerificationDecision Verify(Command command, Snapshot before, Snapshot after,
            InventorySortEvidence nativeBefore, InventorySortEvidence nativeAfter, bool nativeReturned)
        {
            if (command == null || command.action != "sort_inventory_default" ||
                command.arguments?.container != "player")
                return VerificationDecision.Uncertain("inventory_sort_command_invalid");
            if (before == null || after == null || nativeBefore == null || nativeAfter == null ||
                !before.ready || !after.ready || before.schema_version != Protocol.BusinessSchemaVersion ||
                after.schema_version != Protocol.BusinessSchemaVersion)
                return VerificationDecision.Pending();
            if (before.world_epoch != after.world_epoch || before.scene != after.scene ||
                before.world_epoch != nativeBefore.WorldEpoch || before.world_epoch != nativeAfter.WorldEpoch ||
                before.scene != nativeBefore.Scene || before.scene != nativeAfter.Scene ||
                before.revision != nativeBefore.Revision || after.revision != nativeAfter.Revision ||
                !ReferenceEquals(nativeBefore.InventoryIdentity, nativeAfter.InventoryIdentity))
                return VerificationDecision.Uncertain("player_inventory_identity_changed");
            if (before.cash != after.cash || before.cash != nativeBefore.Cash || after.cash != nativeAfter.Cash)
                return VerificationDecision.Uncertain("cash_changed_during_inventory_sort");
            if (!SnapshotMatchesNative(before, nativeBefore) || !SnapshotMatchesNative(after, nativeAfter) ||
                !SameNativeMembers(nativeBefore, nativeAfter))
                return VerificationDecision.Uncertain("player_inventory_membership_or_ownership_changed");

            bool arrangementChanged = ArrangementChanged(nativeBefore, nativeAfter);
            if (nativeReturned) return VerificationDecision.Succeeded();
            if (!arrangementChanged) return VerificationDecision.Uncertain("native_sort_not_completed");
            return VerificationDecision.Uncertain("native_sort_noop_changed_arrangement");
        }

        private static bool SnapshotMatchesNative(Snapshot snapshot, InventorySortEvidence evidence)
        {
            var container = FindPlayerContainer(snapshot);
            if (container?.completeness != "complete" || container.item_refs == null) return false;
            var states = FindPlayerItems(snapshot);
            var members = evidence.CopyMembers();
            if (states == null || container.item_refs.Count != members.Length || members.Length != states.Count)
                return false;
            if (members.Any(m => m == null || string.IsNullOrEmpty(m.ItemRef))) return false;
            var memberRefs = new HashSet<string>(members.Select(m => m.ItemRef), StringComparer.Ordinal);
            if (memberRefs.Count != members.Length || !memberRefs.SetEquals(container.item_refs)) return false;
            foreach (var member in members)
            {
                if (member == null || !states.TryGetValue(member.ItemRef, out var state) ||
                    state.units != member.Units || state.owned != member.Owned || state.shape_status != "known" ||
                    state.width != member.Width || state.height != member.Height) return false;
            }
            return true;
        }

        private static bool SameNativeMembers(InventorySortEvidence left, InventorySortEvidence right)
        {
            var a = left.CopyMembers();
            var b = right.CopyMembers();
            if (a.Length != b.Length) return false;
            var matched = new bool[a.Length];
            foreach (var member in b)
            {
                int found = -1;
                for (int i = 0; i < a.Length; i++)
                    if (!matched[i] && ReferenceEquals(a[i].ItemIdentity, member.ItemIdentity))
                    { found = i; break; }
                if (found < 0 || !a[found].SameState(member)) return false;
                matched[found] = true;
            }
            return true;
        }

        private static bool ArrangementChanged(InventorySortEvidence left, InventorySortEvidence right)
        {
            var a = left.CopyMembers();
            var b = right.CopyMembers();
            if (a.Length != b.Length) return true;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == null || b[i] == null || !ReferenceEquals(a[i].ItemIdentity, b[i].ItemIdentity)) return true;
                if (!a[i].SamePlacement(b[i])) return true;
            }
            return false;
        }

        private static ContainerState FindPlayerContainer(Snapshot snapshot) =>
            snapshot.business?.details?.containers?.Find(c => c != null && c.container == "player");

        private static Dictionary<string, ItemState> FindPlayerItems(Snapshot snapshot)
        {
            var items = snapshot.business?.details?.items;
            if (items == null) return null;
            var result = new Dictionary<string, ItemState>(StringComparer.Ordinal);
            foreach (var item in items)
                if (item == null || item.container != "player" || string.IsNullOrEmpty(item.item_ref) ||
                    result.ContainsKey(item.item_ref)) continue;
                else result.Add(item.item_ref, item);
            return result;
        }
    }
}
