using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PawnPilot
{
    // A proof is only a statement about an immutable snapshot. It never calls a game
    // receiver method, invokes delegates, or holds Unity/GameItem objects.
    public enum CapacityProofStatus
    {
        Unverified = 0,
        Insufficient = 1,
        Sufficient = 2
    }

    public enum CapacityProofReason
    {
        None = 0,
        MissingIdentity,
        UnknownBuild,
        WrongTarget,
        UnknownTargetRules,
        UnknownSourceRules,
        UnknownClientCallback,
        IncompleteSnapshot,
        LockedTarget,
        MalformedGrid,
        UnsupportedGrid,
        UnsupportedItem,
        UnsupportedCallbacks,
        UnsupportedFeatures,
        UnsupportedNestedContent,
        UnsupportedUnitCount,
        UnsupportedShape,
        UnsupportedOrientation,
        InvalidSourceMembership,
        PurchaseListMismatch,
        DuplicateReference,
        DuplicateOccupiedCell,
        CapacityInsufficient
    }

    public enum CapacityFact
    {
        Unknown = 0,
        No = 1,
        Yes = 2
    }

    public enum CapacityBuildProfile
    {
        Unknown = 0,
        Vanilla26337366NoMods = 1
    }

    public enum CapacityTargetProfile
    {
        Unknown = 0,
        EmporiumMainInventoryInHours = 1
    }

    public enum CapacityInventoryCallbackProfile
    {
        Unknown = 0,
        VanillaEmporiumMainInventory = 1,
        VanillaEmporiumBackInventory = 2
    }

    public enum CapacityItemCallbackProfile
    {
        Unknown = 0,
        VanillaCommonMedicalOrToiletPaper = 1
    }

    public enum CapacityFeatureProfile
    {
        Unknown = 0,
        Empty = 1,
        Present = 2
    }

    public enum CapacityNestedProfile
    {
        Unknown = 0,
        EmptyByNativeFactory = 1,
        Present = 2
    }

    public enum CapacitySelectionProfile
    {
        Unknown = 0,
        ExplicitBackInventoryItems = 1,
        AllNonOwnedBackInventoryItems = 2
    }

    public sealed class CapacityShapeSnapshot
    {
        private readonly ReadOnlyCollection<bool> occupiedCells;

        public bool Complete { get; }
        public int Width { get; }
        public int Height { get; }
        public int PositionX { get; }
        public int PositionY { get; }
        public bool Flipped { get; }
        public int Orientation { get; }
        public IReadOnlyList<bool> OccupiedCells => occupiedCells;

        public CapacityShapeSnapshot(bool complete, int width, int height, int positionX, int positionY,
            bool flipped, int orientation, IEnumerable<bool> occupiedCells)
        {
            Complete = complete;
            Width = width;
            Height = height;
            PositionX = positionX;
            PositionY = positionY;
            Flipped = flipped;
            Orientation = orientation;
            this.occupiedCells = Copy(occupiedCells);
        }

        internal bool IsExactOneCell()
        {
            return Complete && Width == 1 && Height == 1 && occupiedCells.Count == 1 && occupiedCells[0];
        }

        internal void AppendFingerprint(StringBuilder builder)
        {
            CapacitySnapshot.Append(builder, Complete);
            CapacitySnapshot.Append(builder, Width);
            CapacitySnapshot.Append(builder, Height);
            CapacitySnapshot.Append(builder, PositionX);
            CapacitySnapshot.Append(builder, PositionY);
            CapacitySnapshot.Append(builder, Flipped);
            CapacitySnapshot.Append(builder, Orientation);
            CapacitySnapshot.Append(builder, occupiedCells.Count);
            foreach (bool occupied in occupiedCells) CapacitySnapshot.Append(builder, occupied);
        }

        private static ReadOnlyCollection<bool> Copy(IEnumerable<bool> values)
        {
            return Array.AsReadOnly(values == null ? Array.Empty<bool>() : new List<bool>(values).ToArray());
        }
    }

    public sealed class CapacityItemSnapshot
    {
        public string ItemRef { get; }
        public string Identifier { get; }
        public string ParentInventoryRef { get; }
        public bool IsDirectInventoryChild { get; }
        public int UnitCount { get; }
        public bool IsOwned { get; }
        public CapacityItemCallbackProfile CallbackProfile { get; }
        public CapacityFeatureProfile FeatureProfile { get; }
        public CapacityNestedProfile NestedProfile { get; }
        public CapacityFact StackCallbackAbsent { get; }
        public CapacityShapeSnapshot Shape { get; }

        public CapacityItemSnapshot(string itemRef, string identifier, string parentInventoryRef,
            bool isDirectInventoryChild, int unitCount, bool isOwned,
            CapacityItemCallbackProfile callbackProfile, CapacityFeatureProfile featureProfile,
            CapacityNestedProfile nestedProfile, CapacityFact stackCallbackAbsent, CapacityShapeSnapshot shape)
        {
            ItemRef = itemRef;
            Identifier = identifier;
            ParentInventoryRef = parentInventoryRef;
            IsDirectInventoryChild = isDirectInventoryChild;
            UnitCount = unitCount;
            IsOwned = isOwned;
            CallbackProfile = callbackProfile;
            FeatureProfile = featureProfile;
            NestedProfile = nestedProfile;
            StackCallbackAbsent = stackCallbackAbsent;
            Shape = shape;
        }

        internal void AppendFingerprint(StringBuilder builder)
        {
            CapacitySnapshot.Append(builder, ItemRef);
            CapacitySnapshot.Append(builder, Identifier);
            CapacitySnapshot.Append(builder, ParentInventoryRef);
            CapacitySnapshot.Append(builder, IsDirectInventoryChild);
            CapacitySnapshot.Append(builder, UnitCount);
            CapacitySnapshot.Append(builder, IsOwned);
            CapacitySnapshot.Append(builder, (int)CallbackProfile);
            CapacitySnapshot.Append(builder, (int)FeatureProfile);
            CapacitySnapshot.Append(builder, (int)NestedProfile);
            CapacitySnapshot.Append(builder, (int)StackCallbackAbsent);
            if (Shape == null) CapacitySnapshot.Append(builder, (string)null);
            else Shape.AppendFingerprint(builder);
        }
    }

    public sealed class CapacitySnapshot
    {
        private readonly ReadOnlyCollection<bool> allowedCells;
        private readonly ReadOnlyCollection<CapacityItemSnapshot> targetDirectItems;
        private readonly ReadOnlyCollection<CapacityItemSnapshot> sourceDirectItems;
        private readonly ReadOnlyCollection<string> purchaseListOrderRefs;

        public string WorldEpoch { get; }
        public int WorldRevision { get; }
        public string TargetRef { get; }
        public string SourceRef { get; }
        public CapacityBuildProfile BuildProfile { get; }
        public CapacityTargetProfile TargetProfile { get; }
        public CapacityInventoryCallbackProfile TargetCallbackProfile { get; }
        public CapacityInventoryCallbackProfile SourceCallbackProfile { get; }
        public CapacityFact TargetInsertLocked { get; }
        public CapacityFact ClientOnItemSoldCallbackPresent { get; }
        public bool TargetSnapshotComplete { get; }
        public bool SourceSnapshotComplete { get; }
        public int GridWidth { get; }
        public int GridHeight { get; }
        public IReadOnlyList<bool> AllowedCells => allowedCells;
        public IReadOnlyList<CapacityItemSnapshot> TargetDirectItems => targetDirectItems;
        public IReadOnlyList<CapacityItemSnapshot> SourceDirectItems => sourceDirectItems;
        public IReadOnlyList<string> PurchaseListOrderRefs => purchaseListOrderRefs;
        public CapacitySelectionProfile SelectionProfile { get; }
        public string Fingerprint { get; }

        public CapacitySnapshot(string worldEpoch, int worldRevision, string targetRef, string sourceRef,
            CapacityBuildProfile buildProfile, CapacityTargetProfile targetProfile,
            CapacityInventoryCallbackProfile targetCallbackProfile,
            CapacityInventoryCallbackProfile sourceCallbackProfile,
            CapacityFact targetInsertLocked, CapacityFact clientOnItemSoldCallbackPresent,
            bool targetSnapshotComplete, bool sourceSnapshotComplete,
            int gridWidth, int gridHeight, IEnumerable<bool> allowedCells,
            IEnumerable<CapacityItemSnapshot> targetDirectItems,
            IEnumerable<CapacityItemSnapshot> sourceDirectItems,
            IEnumerable<string> purchaseListOrderRefs, CapacitySelectionProfile selectionProfile)
        {
            WorldEpoch = worldEpoch;
            WorldRevision = worldRevision;
            TargetRef = targetRef;
            SourceRef = sourceRef;
            BuildProfile = buildProfile;
            TargetProfile = targetProfile;
            TargetCallbackProfile = targetCallbackProfile;
            SourceCallbackProfile = sourceCallbackProfile;
            TargetInsertLocked = targetInsertLocked;
            ClientOnItemSoldCallbackPresent = clientOnItemSoldCallbackPresent;
            TargetSnapshotComplete = targetSnapshotComplete;
            SourceSnapshotComplete = sourceSnapshotComplete;
            GridWidth = gridWidth;
            GridHeight = gridHeight;
            this.allowedCells = Copy(allowedCells);
            this.targetDirectItems = Copy(targetDirectItems);
            this.sourceDirectItems = Copy(sourceDirectItems);
            this.purchaseListOrderRefs = Copy(purchaseListOrderRefs);
            SelectionProfile = selectionProfile;
            Fingerprint = MakeFingerprint();
        }

        internal static void Append(StringBuilder builder, string value)
        {
            string text = value ?? "";
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('|');
        }

        internal static void Append(StringBuilder builder, int value)
        {
            builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append('|');
        }

        internal static void Append(StringBuilder builder, bool value)
        {
            builder.Append(value ? '1' : '0').Append('|');
        }

        private string MakeFingerprint()
        {
            var builder = new StringBuilder(1024);
            Append(builder, WorldEpoch);
            Append(builder, WorldRevision);
            Append(builder, TargetRef);
            Append(builder, SourceRef);
            Append(builder, (int)BuildProfile);
            Append(builder, (int)TargetProfile);
            Append(builder, (int)TargetCallbackProfile);
            Append(builder, (int)SourceCallbackProfile);
            Append(builder, (int)TargetInsertLocked);
            Append(builder, (int)ClientOnItemSoldCallbackPresent);
            Append(builder, TargetSnapshotComplete);
            Append(builder, SourceSnapshotComplete);
            Append(builder, GridWidth);
            Append(builder, GridHeight);
            Append(builder, (int)SelectionProfile);
            Append(builder, allowedCells.Count);
            foreach (bool allowed in allowedCells) Append(builder, allowed);
            AppendItems(builder, targetDirectItems);
            AppendItems(builder, sourceDirectItems);
            Append(builder, purchaseListOrderRefs.Count);
            foreach (string itemRef in purchaseListOrderRefs) Append(builder, itemRef);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        private static void AppendItems(StringBuilder builder, ReadOnlyCollection<CapacityItemSnapshot> items)
        {
            Append(builder, items.Count);
            foreach (CapacityItemSnapshot item in items)
            {
                if (item == null) Append(builder, (string)null);
                else item.AppendFingerprint(builder);
            }
        }

        private static ReadOnlyCollection<T> Copy<T>(IEnumerable<T> values)
        {
            return Array.AsReadOnly(values == null ? Array.Empty<T>() : new List<T>(values).ToArray());
        }
    }

    public sealed class CapacityProofResult
    {
        private readonly ReadOnlyCollection<string> purchaseListOrderRefs;
        private readonly ReadOnlyCollection<string> acceptanceOrderRefs;

        public CapacityProofStatus Status { get; }
        public CapacityProofReason Reason { get; }
        public string WorldEpoch { get; }
        public int WorldRevision { get; }
        public string TargetRef { get; }
        public string SnapshotFingerprint { get; }
        public int FreeCellCount { get; }
        public int RequiredCellCount { get; }
        public IReadOnlyList<string> PurchaseListOrderRefs => purchaseListOrderRefs;
        public IReadOnlyList<string> AcceptanceOrderRefs => acceptanceOrderRefs;

        internal CapacityProofResult(CapacityProofStatus status, CapacityProofReason reason,
            CapacitySnapshot snapshot, int freeCellCount, int requiredCellCount)
        {
            Status = status;
            Reason = reason;
            WorldEpoch = snapshot?.WorldEpoch;
            WorldRevision = snapshot?.WorldRevision ?? -1;
            TargetRef = snapshot?.TargetRef;
            SnapshotFingerprint = snapshot?.Fingerprint;
            FreeCellCount = freeCellCount;
            RequiredCellCount = requiredCellCount;
            purchaseListOrderRefs = Copy(snapshot?.PurchaseListOrderRefs);
            var order = new List<string>(purchaseListOrderRefs);
            order.Reverse();
            acceptanceOrderRefs = Array.AsReadOnly(order.ToArray());
        }

        // This binds the proof to the final receiver and the exact batch ordering.
        // It intentionally does not turn Unverified/Insufficient into a pass.
        public bool IsBoundTo(string targetRef, string worldEpoch, int worldRevision,
            IReadOnlyList<string> purchaseListOrderRefs)
        {
            if (!string.Equals(TargetRef, targetRef, StringComparison.Ordinal) ||
                !string.Equals(WorldEpoch, worldEpoch, StringComparison.Ordinal) ||
                WorldRevision != worldRevision || purchaseListOrderRefs == null ||
                purchaseListOrderRefs.Count != this.purchaseListOrderRefs.Count) return false;
            for (int i = 0; i < purchaseListOrderRefs.Count; i++)
            {
                if (!string.Equals(this.purchaseListOrderRefs[i], purchaseListOrderRefs[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        public bool IsBoundToSnapshot(CapacitySnapshot snapshot)
        {
            return snapshot != null && IsBoundTo(snapshot.TargetRef, snapshot.WorldEpoch,
                snapshot.WorldRevision, snapshot.PurchaseListOrderRefs) &&
                string.Equals(SnapshotFingerprint, snapshot.Fingerprint, StringComparison.Ordinal);
        }

        private static ReadOnlyCollection<string> Copy(IReadOnlyList<string> values)
        {
            if (values == null) return Array.AsReadOnly(Array.Empty<string>());
            var copy = new string[values.Count];
            for (int i = 0; i < values.Count; i++) copy[i] = values[i];
            return Array.AsReadOnly(copy);
        }
    }

    public static class CapacityProof
    {
        private const int NativeMainGridWidth = 24;
        private const int NativeMainGridHeight = 10;

        public static CapacityProofResult Evaluate(CapacitySnapshot snapshot)
        {
            if (snapshot == null) return Fail(CapacityProofStatus.Unverified, CapacityProofReason.MissingIdentity, null);
            if (string.IsNullOrWhiteSpace(snapshot.WorldEpoch) || snapshot.WorldRevision < 0 ||
                string.IsNullOrWhiteSpace(snapshot.TargetRef) || string.IsNullOrWhiteSpace(snapshot.SourceRef))
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.MissingIdentity, snapshot);
            if (snapshot.BuildProfile != CapacityBuildProfile.Vanilla26337366NoMods)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.UnknownBuild, snapshot);
            if (snapshot.TargetProfile != CapacityTargetProfile.EmporiumMainInventoryInHours)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.WrongTarget, snapshot);
            if (snapshot.TargetCallbackProfile != CapacityInventoryCallbackProfile.VanillaEmporiumMainInventory)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.UnknownTargetRules, snapshot);
            if (snapshot.SourceCallbackProfile != CapacityInventoryCallbackProfile.VanillaEmporiumBackInventory)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.UnknownSourceRules, snapshot);
            if (snapshot.ClientOnItemSoldCallbackPresent != CapacityFact.No)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.UnknownClientCallback, snapshot);
            if (!snapshot.TargetSnapshotComplete || !snapshot.SourceSnapshotComplete)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.IncompleteSnapshot, snapshot);
            if (snapshot.TargetInsertLocked == CapacityFact.Yes)
                return Fail(CapacityProofStatus.Insufficient, CapacityProofReason.LockedTarget, snapshot);
            if (snapshot.TargetInsertLocked != CapacityFact.No)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.UnknownTargetRules, snapshot);
            if (snapshot.GridWidth != NativeMainGridWidth || snapshot.GridHeight != NativeMainGridHeight)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.UnsupportedGrid, snapshot);
            if (snapshot.GridWidth <= 0 || snapshot.GridHeight <= 0 ||
                snapshot.AllowedCells.Count != snapshot.GridWidth * snapshot.GridHeight ||
                snapshot.AllowedCells.Count == 0)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.MalformedGrid, snapshot);
            foreach (bool allowed in snapshot.AllowedCells)
            {
                if (!allowed) return Fail(CapacityProofStatus.Unverified, CapacityProofReason.UnsupportedGrid, snapshot);
            }
            if (snapshot.PurchaseListOrderRefs.Count == 0)
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.PurchaseListMismatch, snapshot);

            var targetItemsByRef = new Dictionary<string, CapacityItemSnapshot>(StringComparer.Ordinal);
            var sourceItemsByRef = new Dictionary<string, CapacityItemSnapshot>(StringComparer.Ordinal);
            if (!TryIndex(snapshot.TargetDirectItems, snapshot.TargetRef, targetItemsByRef) ||
                !TryIndex(snapshot.SourceDirectItems, snapshot.SourceRef, sourceItemsByRef))
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.DuplicateReference, snapshot);

            var occupied = new HashSet<int>();
            foreach (CapacityItemSnapshot item in snapshot.TargetDirectItems)
            {
                CapacityProofReason reason = ValidateSimpleItem(item, snapshot.TargetRef, requireUnowned: false);
                if (reason != CapacityProofReason.None) return Fail(CapacityProofStatus.Unverified, reason, snapshot);
                if (item.Shape.PositionX >= snapshot.GridWidth || item.Shape.PositionY >= snapshot.GridHeight)
                    return Fail(CapacityProofStatus.Unverified, CapacityProofReason.UnsupportedShape, snapshot);
                int cell = item.Shape.PositionY * snapshot.GridWidth + item.Shape.PositionX;
                if (!occupied.Add(cell)) return Fail(CapacityProofStatus.Unverified, CapacityProofReason.DuplicateOccupiedCell, snapshot);
            }

            foreach (string sourceItemRef in sourceItemsByRef.Keys)
            {
                if (targetItemsByRef.ContainsKey(sourceItemRef))
                    return Fail(CapacityProofStatus.Unverified, CapacityProofReason.DuplicateReference, snapshot);
            }

            var requested = new HashSet<string>(StringComparer.Ordinal);
            foreach (string itemRef in snapshot.PurchaseListOrderRefs)
            {
                if (string.IsNullOrWhiteSpace(itemRef) || !requested.Add(itemRef))
                    return Fail(CapacityProofStatus.Unverified, CapacityProofReason.DuplicateReference, snapshot);
                CapacityItemSnapshot item;
                if (!sourceItemsByRef.TryGetValue(itemRef, out item))
                    return Fail(CapacityProofStatus.Unverified, CapacityProofReason.InvalidSourceMembership, snapshot);
                CapacityProofReason reason = ValidateSimpleItem(item, snapshot.SourceRef, requireUnowned: true);
                if (reason != CapacityProofReason.None) return Fail(CapacityProofStatus.Unverified, reason, snapshot);
            }

            if (snapshot.SelectionProfile == CapacitySelectionProfile.AllNonOwnedBackInventoryItems)
            {
                var expected = new List<string>();
                foreach (CapacityItemSnapshot item in snapshot.SourceDirectItems)
                {
                    if (!item.IsOwned) expected.Add(item.ItemRef);
                }
                if (!SameOrder(expected, snapshot.PurchaseListOrderRefs))
                    return Fail(CapacityProofStatus.Unverified, CapacityProofReason.PurchaseListMismatch, snapshot);
            }
            else if (snapshot.SelectionProfile != CapacitySelectionProfile.ExplicitBackInventoryItems)
            {
                return Fail(CapacityProofStatus.Unverified, CapacityProofReason.PurchaseListMismatch, snapshot);
            }

            int freeCells = snapshot.AllowedCells.Count - occupied.Count;
            int requiredCells = snapshot.PurchaseListOrderRefs.Count;
            if (freeCells < requiredCells)
                return Fail(CapacityProofStatus.Insufficient, CapacityProofReason.CapacityInsufficient, snapshot, freeCells, requiredCells);
            return Fail(CapacityProofStatus.Sufficient, CapacityProofReason.None, snapshot, freeCells, requiredCells);
        }

        private static CapacityProofReason ValidateSimpleItem(CapacityItemSnapshot item, string expectedParentRef, bool requireUnowned)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ItemRef) || string.IsNullOrWhiteSpace(item.Identifier))
                return CapacityProofReason.MissingIdentity;
            if (!item.IsDirectInventoryChild || !string.Equals(item.ParentInventoryRef, expectedParentRef, StringComparison.Ordinal))
                return CapacityProofReason.InvalidSourceMembership;
            if (requireUnowned && item.IsOwned) return CapacityProofReason.PurchaseListMismatch;
            if (item.Identifier != "common_medical" && item.Identifier != "toilet_paper")
                return CapacityProofReason.UnsupportedItem;
            if (item.CallbackProfile != CapacityItemCallbackProfile.VanillaCommonMedicalOrToiletPaper ||
                item.StackCallbackAbsent != CapacityFact.Yes)
                return CapacityProofReason.UnsupportedCallbacks;
            if (item.FeatureProfile != CapacityFeatureProfile.Empty)
                return CapacityProofReason.UnsupportedFeatures;
            if (item.NestedProfile != CapacityNestedProfile.EmptyByNativeFactory)
                return CapacityProofReason.UnsupportedNestedContent;
            if (item.UnitCount != 1) return CapacityProofReason.UnsupportedUnitCount;
            if (item.Shape == null || !item.Shape.Complete || item.Shape.Width <= 0 || item.Shape.Height <= 0 ||
                item.Shape.OccupiedCells.Count != item.Shape.Width * item.Shape.Height)
                return CapacityProofReason.UnsupportedShape;
            if (!item.Shape.IsExactOneCell()) return CapacityProofReason.UnsupportedShape;
            if (item.Shape.Flipped || item.Shape.Orientation != 0) return CapacityProofReason.UnsupportedOrientation;
            if (item.Shape.PositionX < 0 || item.Shape.PositionY < 0)
                return CapacityProofReason.UnsupportedShape;
            return CapacityProofReason.None;
        }

        private static bool TryIndex(IReadOnlyList<CapacityItemSnapshot> items, string expectedParentRef,
            Dictionary<string, CapacityItemSnapshot> index)
        {
            foreach (CapacityItemSnapshot item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.ItemRef) ||
                    !item.IsDirectInventoryChild ||
                    !string.Equals(item.ParentInventoryRef, expectedParentRef, StringComparison.Ordinal) ||
                    index.ContainsKey(item.ItemRef)) return false;
                index.Add(item.ItemRef, item);
            }
            return true;
        }

        private static bool SameOrder(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
        {
            if (expected.Count != actual.Count) return false;
            for (int i = 0; i < expected.Count; i++)
            {
                if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static CapacityProofResult Fail(CapacityProofStatus status, CapacityProofReason reason,
            CapacitySnapshot snapshot, int freeCells = 0, int requiredCells = 0)
        {
            return new CapacityProofResult(status, reason, snapshot, freeCells, requiredCells);
        }
    }
}
