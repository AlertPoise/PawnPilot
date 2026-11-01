using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PawnPilot
{
    // Local-only observation projection. This class has no Unity dependency and defines no actions.
    // Candidate fields are local research evidence, not certified visible state or action handles.
    // The reader restricts direct containers/geometry; a screenshot must corroborate visibility.
    public sealed class VerificationDiagnostic
    {
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        private readonly Dictionary<object, string> itemReferences = new Dictionary<object, string>(ReferenceComparer.Instance);
        private readonly Dictionary<object, string> customerReferences = new Dictionary<object, string>(ReferenceComparer.Instance);
        private long nextItemReference;
        private long nextCustomerReference;
        private string currentWorld;

        public VerificationDiagnosticSnapshot Capture(string worldEpoch, bool ready, Func<VerificationDiagnosticInput> read)
        {
            if (!ValidWorld(worldEpoch))
            {
                ClearReferences();
                currentWorld = null;
                return Unavailable(null, "invalid_world_epoch");
            }

            if (!string.Equals(currentWorld, worldEpoch, StringComparison.Ordinal))
            {
                ClearReferences();
                currentWorld = worldEpoch;
            }

            if (!ready)
            {
                ClearItemReferences();
                customerReferences.Clear();
                return Unavailable(worldEpoch, "not_ready");
            }

            if (read == null)
            {
                ClearReferences();
                return Unavailable(worldEpoch, "reader_unavailable");
            }

            VerificationDiagnosticInput input;
            try
            {
                input = read();
            }
            catch
            {
                ClearReferences();
                return Unavailable(worldEpoch, "diagnostic_reader_failed");
            }

            string invalidReason = Validate(input);
            if (invalidReason != null)
            {
                ClearReferences();
                return Unavailable(worldEpoch, invalidReason);
            }

            var customerRef = GetCustomerReference(input.Customer);
            string blocker = GetBlocker(input);
            if (blocker != null && blocker.Length > 256)
            {
                ClearReferences();
                return Unavailable(worldEpoch, "invalid_string");
            }
            if (blocker != null)
            {
                // Item presence cannot be tracked while its presentation is obstructed.
                ClearItemReferences();
                return new VerificationDiagnosticSnapshot
                {
                    world_epoch = worldEpoch,
                    customer_ref = customerRef,
                    availability = "blocked",
                    blocker = blocker,
                    ui_consistency = "blocked",
                    quote_binding = "unverified",
                    items = Array.Empty<VerificationDiagnosticItem>(),
                    rows = Array.Empty<VerificationDiagnosticRow>(),
                    footer = Array.Empty<string>()
                };
            }

            var presentItems = new HashSet<object>(ReferenceComparer.Instance);
            var projectedItems = new VerificationDiagnosticItem[input.Items.Length];
            for (int i = 0; i < input.Items.Length; i++)
            {
                VerificationItemInput item = input.Items[i];
                presentItems.Add(item.Identity);
                projectedItems[i] = new VerificationDiagnosticItem
                {
                    item_ref = GetItemReference(item.Identity),
                    container = item.Container,
                    units = item.Units,
                    owned = item.Owned,
                    shape_min_x = item.X,
                    shape_min_y = item.Y
                };
            }
            RemoveAbsentItems(presentItems);

            var projectedRows = new List<VerificationDiagnosticRow>();
            for (int i = 0; i < input.Rows.Length; i++)
            {
                VerificationRowInput row = input.Rows[i];
                if (!input.TabletOpen || !row.FullyVisible) continue;
                projectedRows.Add(new VerificationDiagnosticRow { name = row.Name, price = row.Price });
            }

            return new VerificationDiagnosticSnapshot
            {
                world_epoch = worldEpoch,
                customer_ref = customerRef,
                availability = input.UnknownUi ? "unverified" : "available",
                blocker = null,
                ui_consistency = "unverified",
                quote_binding = "unverified",
                items = projectedItems,
                rows = projectedRows.ToArray(),
                footer = input.TabletOpen ? (string[])input.Footer.Clone() : Array.Empty<string>()
            };
        }

        private static string Validate(VerificationDiagnosticInput input)
        {
            if (input == null) return "invalid_sample";
            if (!ValidString(input.KnownBlocker)) return "invalid_string";
            if (input.Items == null || input.Rows == null || input.Footer == null) return "incomplete_sample";
            if (input.Items.Length > 64 || input.Rows.Length > 64 || input.Footer.Length > 8) return "sample_limit_exceeded";
            if (input.Customer != null && !input.Customer.GetType().IsClass) return "invalid_customer_identity";

            var seenItems = new Dictionary<object, string>(ReferenceComparer.Instance);
            for (int i = 0; i < input.Items.Length; i++)
            {
                VerificationItemInput item = input.Items[i];
                if (item == null || item.Identity == null) return "invalid_item_identity";
                if (!item.Identity.GetType().IsClass) return "invalid_item_identity";
                if (!ValidString(item.Container) || string.IsNullOrWhiteSpace(item.Container)) return "invalid_string";
                if (item.Units <= 0) return "invalid_item_units";
                if (seenItems.TryGetValue(item.Identity, out string previousContainer))
                    return string.Equals(previousContainer, item.Container, StringComparison.Ordinal)
                        ? "duplicate_item_identity"
                        : "item_in_multiple_containers";
                seenItems.Add(item.Identity, item.Container);
            }

            for (int i = 0; i < input.Rows.Length; i++)
            {
                VerificationRowInput row = input.Rows[i];
                if (row == null || row.Name == null || row.Price == null || !ValidString(row.Name) || !ValidString(row.Price)) return "invalid_string";
            }
            for (int i = 0; i < input.Footer.Length; i++)
                if (input.Footer[i] == null || !ValidString(input.Footer[i])) return "invalid_string";

            return null;
        }

        private static bool ValidString(string value) => value == null || value.Length <= 256;
        private static bool ValidWorld(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

        private static string GetBlocker(VerificationDiagnosticInput input)
        {
            var reasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(input.KnownBlocker)) reasons.Add(input.KnownBlocker);
            if (input.ConfirmationOpen) reasons.Add("confirmation_open");
            if (input.DialogueActive) reasons.Add("dialogue_active");
            if (input.WaitingForAdvance) reasons.Add("waiting_for_advance");
            return reasons.Count == 0 ? null : string.Join(";", reasons);
        }

        private string GetItemReference(object identity)
        {
            if (itemReferences.TryGetValue(identity, out string reference)) return reference;
            reference = "item-" + (++nextItemReference).ToString(System.Globalization.CultureInfo.InvariantCulture);
            itemReferences.Add(identity, reference);
            return reference;
        }

        private string GetCustomerReference(object identity)
        {
            if (identity == null)
            {
                customerReferences.Clear();
                return null;
            }
            if (customerReferences.TryGetValue(identity, out string reference)) return reference;
            customerReferences.Clear();
            reference = "customer-" + (++nextCustomerReference).ToString(System.Globalization.CultureInfo.InvariantCulture);
            customerReferences.Add(identity, reference);
            return reference;
        }

        private void RemoveAbsentItems(HashSet<object> presentItems)
        {
            var absent = new List<object>();
            foreach (object identity in itemReferences.Keys)
                if (!presentItems.Contains(identity)) absent.Add(identity);
            for (int i = 0; i < absent.Count; i++) itemReferences.Remove(absent[i]);
        }

        private void ClearItemReferences() => itemReferences.Clear();

        private void ClearReferences()
        {
            itemReferences.Clear();
            customerReferences.Clear();
        }

        private static VerificationDiagnosticSnapshot Unavailable(string worldEpoch, string reason) => new VerificationDiagnosticSnapshot
        {
            world_epoch = worldEpoch,
            customer_ref = null,
            availability = "unavailable",
            blocker = reason,
            ui_consistency = "unverified",
            quote_binding = "unverified",
            items = Array.Empty<VerificationDiagnosticItem>(),
            rows = Array.Empty<VerificationDiagnosticRow>(),
            footer = Array.Empty<string>()
        };
    }

    public sealed class VerificationDiagnosticInput
    {
        public object Customer;
        public string KnownBlocker;
        public bool TabletOpen;
        public bool ConfirmationOpen;
        public bool DialogueActive;
        public bool WaitingForAdvance;
        public bool UnknownUi;
        public VerificationItemInput[] Items = Array.Empty<VerificationItemInput>();
        public VerificationRowInput[] Rows = Array.Empty<VerificationRowInput>();
        public string[] Footer = Array.Empty<string>();
    }

    public sealed class VerificationItemInput
    {
        public object Identity;
        public string Container;
        public int Units;
        public bool Owned;
        public int X;
        public int Y;
    }

    public sealed class VerificationRowInput
    {
        public string Name;
        public string Price;
        public bool FullyVisible;
    }

    [Serializable]
    public sealed class VerificationDiagnosticSnapshot
    {
        public string world_epoch;
        public string customer_ref;
        public string availability;
        public string blocker;
        public string ui_consistency;
        public string quote_binding;
        public string visibility_basis = "geometric_candidates_require_screenshot";
        public VerificationDiagnosticItem[] items;
        public VerificationDiagnosticRow[] rows;
        public string[] footer;
    }

    [Serializable]
    public sealed class VerificationDiagnosticItem
    {
        public string item_ref;
        public string container;
        public int units;
        public bool owned;
        public int shape_min_x;
        public int shape_min_y;
    }

    [Serializable]
    public sealed class VerificationDiagnosticRow
    {
        public string name;
        public string price;
    }
}
