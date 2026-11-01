using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace PawnPilot
{
    // Internal, process-local values supplied by the Unity reader. Object identities never enter
    // a wire DTO; callers must include only UI-visible data and known direct container members.
    public sealed class BusinessInput
    {
        public BusinessCustomerInput Customer = new BusinessCustomerInput();
        public BusinessContainerInput[] Containers = Array.Empty<BusinessContainerInput>();
        public BusinessQuoteInput Quote = new BusinessQuoteInput();
        public BusinessUiInput Ui = new BusinessUiInput();
    }

    public sealed class BusinessCustomerInput
    {
        public object Identity;
        public string Presence = "unverified";
        public bool? Arrived;
        public string TradeType = "unknown";
        public string Phase = "unknown";
        public object DialogueIdentity;
        public string DialogueStage = "unknown";
        public object[] OptionIdentities = Array.Empty<object>();
    }

    public sealed class BusinessContainerInput
    {
        public string Container;
        public string Completeness = "unavailable";
        public string Reason;
        public BusinessItemInput[] Items = Array.Empty<BusinessItemInput>();
    }

    public sealed class BusinessItemInput
    {
        public object Identity;
        public string Name; // Set only when read from rendered, visible UI text.
        public int Units;
        public bool Owned;
        public string ShapeStatus = "unverified";
        public int? Width, Height;
    }

    public sealed class BusinessQuoteInput
    {
        public string Status = "unavailable";
        public string Mode = "unknown";
        public int? DisplayedTotal;
        public string Reason = "not_observed";
        public bool BindingVerified;
        public BusinessQuoteRowInput[] Rows = Array.Empty<BusinessQuoteRowInput>();
    }

    public sealed class BusinessQuoteRowInput
    {
        public object ItemIdentity;
        public string Name; // Rendered row label only.
        public string PriceText; // Rendered row text only; do not compute price here.
        public int? Amount;
    }

    public sealed class BusinessUiInput
    {
        public string Status = "unverified";
        public string Coverage = "known_roots_only";
        public string[] Blockers = Array.Empty<string>();
        public bool Complete;
    }

    public sealed class BusinessCollectionMember
    {
        public string item_ref;
        public string container;
        public int units;
        public bool owned;
    }

    // Stable opaque fingerprint for one ordered, completely observed collection. It contains
    // no game IDs or names and does not assert that an unverified quote is actionable.
    public static class BusinessCollection
    {
        public static string Fingerprint(string worldEpoch, string customerRef, string tradeType,
            IEnumerable<BusinessCollectionMember> orderedMembers, string quoteMode, string quoteStatus, int? displayedTotal)
        {
            if (string.IsNullOrEmpty(worldEpoch) || worldEpoch.Length > 128 || string.IsNullOrEmpty(customerRef) ||
                customerRef.Length > 128 || orderedMembers == null ||
                !OneOf(tradeType, "sell", "other", "unknown") || !OneOf(quoteMode, "normal", "wholesale", "unknown") ||
                !OneOf(quoteStatus, "complete", "unverified", "unavailable") || (displayedTotal.HasValue && displayedTotal.Value < 0)) return null;
            var members = new List<BusinessCollectionMember>();
            foreach (var member in orderedMembers)
            {
                if (member == null || string.IsNullOrEmpty(member.item_ref) || member.item_ref.Length > 128 ||
                    !OneOf(member.container, "front", "back", "player") || member.units <= 0) return null;
                members.Add(member);
                if (members.Count > 64) return null;
            }
            if (members.Count == 0) return null;
            var builder = new StringBuilder();
            Append(builder, worldEpoch); Append(builder, customerRef); Append(builder, tradeType);
            Append(builder, quoteMode); Append(builder, quoteStatus);
            Append(builder, displayedTotal.HasValue ? displayedTotal.Value.ToString(CultureInfo.InvariantCulture) : null);
            Append(builder, members.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var member in members)
            {
                Append(builder, member.item_ref); Append(builder, member.container);
                Append(builder, member.units.ToString(CultureInfo.InvariantCulture));
                Append(builder, member.owned ? "1" : "0");
            }
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                return "collection-" + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static bool OneOf(string value, params string[] allowed)
        {
            foreach (var candidate in allowed) if (string.Equals(value, candidate, StringComparison.Ordinal)) return true;
            return false;
        }

        private static void Append(StringBuilder builder, string value) =>
            builder.Append(value == null ? -1 : value.Length).Append(':').Append(value).Append('|');
    }

    public static class BusinessQuoteParsing
    {
        public static bool IsTotalLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            return string.Equals(value, "total", StringComparison.OrdinalIgnoreCase) || value == "总计" || value == "总价";
        }

        public static int? ParseVisibleTotal(string rendered)
        {
            if (string.IsNullOrWhiteSpace(rendered)) return null;
            string value = rendered.Trim();
            if (value.Length > 32) return null;
            if (value.Length > 0 && IsCurrencySymbol(value[0])) value = value.Substring(1).TrimStart();
            else if (value.Length > 0 && IsCurrencyGlyph(value[value.Length - 1])) value = value.Substring(0, value.Length - 1).TrimEnd();
            if (value.Length == 0) return null;
            int comma = value.IndexOf(',');
            string digits;
            if (comma < 0)
            {
                digits = value;
                if (!AllDigits(digits)) return null;
            }
            else
            {
                string[] groups = value.Split(',');
                if (groups.Length < 2 || groups[0].Length < 1 || groups[0].Length > 3 || !AllDigits(groups[0])) return null;
                for (int i = 1; i < groups.Length; i++) if (groups[i].Length != 3 || !AllDigits(groups[i])) return null;
                digits = string.Concat(groups);
            }
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int total) && total >= 0)
                return total;
            return null;
        }

        private static bool IsCurrencySymbol(char value) => value == '$' || value == '€' || value == '£' || value == '¥';
        private static bool IsCurrencyGlyph(char value) => value >= '\uE000' && value <= '\uF8FF';
        private static bool AllDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < value.Length; i++) if (value[i] < '0' || value[i] > '9') return false;
            return true;
        }
    }

    // Pure projection, reference lifetime, bounds, and quote-binding validation. No Unity or game
    // references are used. Production business actions remain disabled by this observation path.
    public sealed class BusinessProjection
    {
        private sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new RefComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        private sealed class ItemRecord
        {
            public string reference;
            public string container;
            public string name;
            public int units;
            public bool owned;
            public string shapeStatus;
            public int? width, height;
        }

        private sealed class RefState
        {
            public object identity;
            public string fingerprint;
            public string reference;
        }

        private readonly Dictionary<object, ItemRecord> itemRefs = new Dictionary<object, ItemRecord>(RefComparer.Instance);
        private readonly Dictionary<object, RefState> optionRefs = new Dictionary<object, RefState>(RefComparer.Instance);
        private object customerIdentity, dialogueIdentity;
        private string customerRef, dialogueRef, dialogueFingerprint;
        private object[] dialogueOptions = Array.Empty<object>();
        private string world;
        private long nextReference;

        internal BusinessObservation Invalidate(string worldEpoch, string reason)
        {
            ClearAll(); world = worldEpoch;
            return Unavailable(reason);
        }

        // Resolves only a ref issued by the most recent valid projection state. Native callers
        // must still revalidate that this exact option is visible and interactable immediately
        // before dispatch; this lookup alone does not authorize an action.
        internal bool TryResolveCustomer(string worldEpoch, string expectedCustomerRef, out object identity)
        {
            identity = null;
            if (!string.Equals(world, worldEpoch, StringComparison.Ordinal) || customerIdentity == null ||
                customerRef == null || !string.Equals(customerRef, expectedCustomerRef, StringComparison.Ordinal)) return false;
            identity = customerIdentity;
            return true;
        }

        internal bool TryResolveDialogue(string worldEpoch, string expectedCustomerRef, string expectedDialogueRef,
            out object identity)
        {
            identity = null;
            if (!TryResolveCustomer(worldEpoch, expectedCustomerRef, out _) || dialogueIdentity == null ||
                dialogueRef == null || !string.Equals(dialogueRef, expectedDialogueRef, StringComparison.Ordinal)) return false;
            identity = dialogueIdentity;
            return true;
        }

        internal bool TryResolveOption(string worldEpoch, string expectedCustomerRef, string expectedDialogueRef,
            string optionRef, out object identity)
        {
            identity = null;
            if (!string.Equals(world, worldEpoch, StringComparison.Ordinal) || customerRef == null ||
                dialogueRef == null || !string.Equals(customerRef, expectedCustomerRef, StringComparison.Ordinal) ||
                !string.Equals(dialogueRef, expectedDialogueRef, StringComparison.Ordinal)) return false;
            foreach (var entry in optionRefs)
            {
                if (!string.Equals(entry.Value.reference, optionRef, StringComparison.Ordinal)) continue;
                identity = entry.Key;
                return identity != null;
            }
            return false;
        }

        public BusinessObservation Capture(string worldEpoch, bool ready, Func<BusinessInput> read)
        {
            if (string.IsNullOrEmpty(worldEpoch) || !ValidText(worldEpoch, 128))
            {
                ClearAll();
                world = null;
                return Unavailable("invalid_world_epoch");
            }
            if (!string.Equals(world, worldEpoch, StringComparison.Ordinal))
            {
                ClearAll();
                world = worldEpoch;
            }
            if (!ready)
            {
                ClearAll();
                return Unavailable("not_ready");
            }
            if (read == null)
            {
                ClearAll();
                return Unavailable("reader_unavailable");
            }

            try
            {
                var input = read();
                string invalid = Validate(input);
                if (invalid != null)
                {
                    ClearAll();
                    return Unavailable(invalid);
                }
                var projected = Project(worldEpoch, input);
                if (!BusinessWire.Validate(projected))
                {
                    ClearAll();
                    return Unavailable("projected_evidence_invalid");
                }
                return projected;
            }
            catch (Exception)
            {
                // Do not serialize exception messages or keep stale item/customer capabilities.
                ClearAll();
                return Unavailable("reader_failed");
            }
        }

        private BusinessObservation Project(string worldEpoch, BusinessInput input)
        {
            var details = new BusinessDetails();
            var customer = input.Customer;
            bool hasCustomer = customer.Identity != null && customer.Presence == "present";
            if (!ReferenceEquals(customerIdentity, customer.Identity) || (hasCustomer && customerRef == null))
            {
                customerIdentity = customer.Identity;
                customerRef = hasCustomer ? NewRef("customer") : null;
                ClearItemRefs();
                ClearDialogueRefs();
            }
            if (!hasCustomer)
            {
                customerRef = null;
                ClearDialogueRefs();
            }

            details.customer.presence = customer.Presence;
            details.customer.arrived = hasCustomer ? customer.Arrived : null;
            details.customer.trade_type = hasCustomer ? customer.TradeType : "unknown";
            details.customer.phase = customer.Presence == "absent" ?
                (customer.Phase == "leaving" ? "leaving" : "absent") : hasCustomer ? customer.Phase : "unknown";
            details.customer.dialogue_stage = customer.Presence == "absent" ? "absent" : hasCustomer ? customer.DialogueStage : "unknown";
            if (hasCustomer && customer.DialogueIdentity != null && customer.DialogueStage != "absent")
            {
                string nextDialogueFingerprint = customer.DialogueStage;
                if (!ReferenceEquals(dialogueIdentity, customer.DialogueIdentity) ||
                    !string.Equals(dialogueFingerprint, nextDialogueFingerprint, StringComparison.Ordinal) ||
                    !SameIdentities(dialogueOptions, customer.OptionIdentities))
                {
                    ClearDialogueRefs();
                    dialogueIdentity = customer.DialogueIdentity;
                    dialogueFingerprint = nextDialogueFingerprint;
                    dialogueOptions = (object[])customer.OptionIdentities.Clone();
                    dialogueRef = NewRef("dialogue");
                    for (int i = 0; i < customer.OptionIdentities.Length; i++)
                    {
                        object option = customer.OptionIdentities[i];
                        optionRefs.Add(option, new RefState { identity = option,
                            fingerprint = nextDialogueFingerprint, reference = NewRef("option") });
                    }
                }
                details.customer.dialogue_ref = dialogueRef;
                for (int i = 0; i < customer.OptionIdentities.Length; i++)
                    details.customer.option_refs.Add(optionRefs[customer.OptionIdentities[i]].reference);
            }
            else ClearDialogueRefs();

            var containersByName = new Dictionary<string, BusinessContainerInput>(StringComparer.Ordinal);
            foreach (var container in input.Containers) containersByName.Add(container.Container, container);
            bool uiBlocksItems = input.Ui.Status == "blocked" || Array.IndexOf(input.Ui.Blockers, "ui_reader_failed") >= 0;
            var presentObservedItems = new HashSet<object>(RefComparer.Instance);
            var allItemRefs = new List<string>();
            bool allContainersComplete = true;
            foreach (string name in new[] { "front", "back", "player" })
            {
                if (!containersByName.TryGetValue(name, out var source))
                {
                    allContainersComplete = false;
                    RemoveContainerReferences(name);
                    details.containers.Add(new ContainerState { container = name, completeness = "unavailable", reason = "container_missing" });
                    continue;
                }
                var completeness = uiBlocksItems ? "incomplete" : source.Completeness;
                var state = new ContainerState { container = name, completeness = completeness,
                    reason = uiBlocksItems ? "ui_blocked_or_unverified" : source.Reason, item_refs = new List<string>() };
                details.containers.Add(state);
                if (completeness != "complete")
                {
                    allContainersComplete = false;
                }
                if (uiBlocksItems) continue;
                foreach (var sourceItem in source.Items)
                {
                    string itemRef = null;
                    presentObservedItems.Add(sourceItem.Identity);
                    itemRef = GetItemReference(sourceItem, name);
                    state.item_refs.Add(itemRef);
                    allItemRefs.Add(itemRef);
                    details.items.Add(new ItemState { item_ref = itemRef, container = name,
                        name = sourceItem.Name, units = sourceItem.Units, owned = sourceItem.Owned,
                        shape_status = sourceItem.ShapeStatus, width = sourceItem.Width, height = sourceItem.Height });
                }
            }
            RemoveUnobservedItemReferences(presentObservedItems);

            details.status = input.Ui.Complete && input.Ui.Status == "consistent" &&
                input.Ui.Coverage == "verified" && allContainersComplete ? "complete" : "partial";
            details.ui.status = input.Ui.Status;
            details.ui.coverage = input.Ui.Coverage;
            details.ui.blockers.AddRange(input.Ui.Blockers);

            ProjectQuote(input.Quote, details.quote, details.items, details.containers, hasCustomer, allContainersComplete, uiBlocksItems);
            string collectionFingerprint = null;
            if (!uiBlocksItems && hasCustomer && containersByName.TryGetValue("back", out var back) && back.Completeness == "complete" && back.Items.Length > 0)
            {
                bool allUnowned = true;
                var members = new List<BusinessCollectionMember>(back.Items.Length);
                foreach (var item in back.Items)
                {
                    if (item.Owned) allUnowned = false;
                    members.Add(new BusinessCollectionMember { item_ref = GetItemReference(item, "back"),
                        container = "back", units = item.Units, owned = item.Owned });
                }
                if (allUnowned)
                    collectionFingerprint = BusinessCollection.Fingerprint(worldEpoch, customerRef, customer.TradeType,
                        members, details.quote.mode, details.quote.status, details.quote.displayed_total);
            }
            // The current runtime reader does not establish a full customer-specific native capacity
            // proof, UI coverage or settlement evidence. Do not promote candidates to legal actions.
            details.capacity.status = "unverified";
            details.capacity.scope = "none";
            details.capacity.reason = "native_rules_unverified";
            details.eligibility.purchase_enabled = false;
            details.eligibility.missing = new List<string> { "quote_binding", "capacity", "settlement", "ui_coverage" };
            foreach (string action in new[] { "start_business", "receive_next_customer", "advance_dialogue",
                "choose_dialogue_option", "prepare_purchase_items", "cancel_confirmation", "dismiss_customer" })
                details.candidates.Add(new BusinessCandidate { action = action, status = uiBlocksItems ? "blocked" : "unverified",
                    reason = uiBlocksItems ? "ui_blocked_or_unverified" : "native_scope_not_verified" });
            details.candidates.Add(new BusinessCandidate { action = "refuse_trade", status = "blocked", reason = "unsupported_native_path" });
            details.candidates.Add(new BusinessCandidate { action = "purchase_visible_offer", status = "blocked", reason = "q_c_v_not_verified" });

            return new BusinessObservation
            {
                support = "partial",
                customer_ref = customerRef,
                item_refs = allItemRefs,
                offer_ref = null,
                collection_fingerprint = collectionFingerprint,
                details = details
            };
        }

        private void ProjectQuote(BusinessQuoteInput source, QuoteState target, List<ItemState> projectedItems,
            List<ContainerState> containers, bool hasCustomer, bool allContainersComplete, bool uiBlocksItems)
        {
            if (uiBlocksItems)
            {
                target.status = "unavailable";
                target.mode = "unknown";
                target.displayed_total = null;
                target.reason = "ui_blocked_or_unverified";
                return;
            }
            // This projector has no production row-binding attestation. A caller-supplied bool
            // cannot mint a verified offer, even if a future input claims complete evidence.
            target.status = source.Status == "unavailable" ? "unavailable" : "unverified";
            target.mode = "unknown";
            target.displayed_total = source.DisplayedTotal;
            target.reason = source.Reason;
            // Quote row/item binding and freshness are not proven by visible text alone. Preserve
            // rendered row text, but do not attach action-capable item references in this reader.
            foreach (var row in source.Rows)
                target.rows.Add(new QuoteRowState { item_ref = null, name = row.Name,
                    price_text = row.PriceText, amount = null });
            if (!source.BindingVerified)
            {
                target.mode = "unknown";
                target.reason = source.Reason;
            }
        }

        private string GetItemReference(BusinessItemInput item, string container)
        {
            if (!itemRefs.TryGetValue(item.Identity, out var record) ||
                record.units != item.Units || record.owned != item.Owned ||
                !string.Equals(record.name, item.Name, StringComparison.Ordinal) ||
                !string.Equals(record.shapeStatus, item.ShapeStatus, StringComparison.Ordinal) ||
                record.width != item.Width || record.height != item.Height)
            {
                itemRefs.Remove(item.Identity);
                record = new ItemRecord { reference = NewRef("item"), container = container,
                    name = item.Name, units = item.Units, owned = item.Owned,
                    shapeStatus = item.ShapeStatus, width = item.Width, height = item.Height };
                itemRefs.Add(item.Identity, record);
            }
            else record.container = container;
            return record.reference;
        }

        private void RemoveUnobservedItemReferences(HashSet<object> present)
        {
            var remove = new List<object>();
            foreach (var pair in itemRefs)
                if (!present.Contains(pair.Key)) remove.Add(pair.Key);
            foreach (object identity in remove) itemRefs.Remove(identity);
        }

        private void RemoveContainerReferences(string container)
        {
            var remove = new List<object>();
            foreach (var pair in itemRefs)
                if (string.Equals(pair.Value.container, container, StringComparison.Ordinal)) remove.Add(pair.Key);
            foreach (object identity in remove) itemRefs.Remove(identity);
        }

        private string NewRef(string type) => type + "-" + (++nextReference).ToString(CultureInfo.InvariantCulture);

        private void ClearDialogueRefs()
        {
            optionRefs.Clear();
            dialogueIdentity = null;
            dialogueFingerprint = null;
            dialogueOptions = Array.Empty<object>();
            dialogueRef = null;
        }

        private void ClearItemRefs() => itemRefs.Clear();

        private void ClearAll()
        {
            ClearItemRefs();
            ClearDialogueRefs();
            customerIdentity = null;
            customerRef = null;
        }

        private BusinessObservation Unavailable(string reason) => new BusinessObservation
        {
            support = "partial",
            customer_ref = null,
            item_refs = new List<string>(),
            offer_ref = null,
            collection_fingerprint = null,
            details = new BusinessDetails { status = "unavailable",
                customer = new CustomerState { presence = "unverified", phase = "unknown" },
                containers = new List<ContainerState> {
                    new ContainerState { container = "front", completeness = "unavailable", reason = reason },
                    new ContainerState { container = "back", completeness = "unavailable", reason = reason },
                    new ContainerState { container = "player", completeness = "unavailable", reason = reason }
                },
                quote = new QuoteState { status = "unavailable", mode = "unknown", reason = reason },
                ui = new BusinessUiState { status = "unverified", coverage = "known_roots_only" },
                capacity = new CapacityState { status = "unverified", scope = "none", reason = "native_rules_unverified" },
                eligibility = new PurchaseEligibility { purchase_enabled = false,
                    missing = new List<string> { "quote_binding", "capacity", "settlement", "ui_coverage" } }
            }
        };

        private static string Validate(BusinessInput input)
        {
            if (input == null || input.Customer == null || input.Containers == null || input.Quote == null || input.Ui == null)
                return "incomplete_sample";
            if (input.Containers.Length > 3 || input.Quote.Rows == null || input.Quote.Rows.Length > 64 ||
                input.Ui.Blockers == null || input.Ui.Blockers.Length > 16 || input.Customer.OptionIdentities == null ||
                input.Customer.OptionIdentities.Length > 16) return "sample_limit_exceeded";
            if (!Allowed(input.Customer.Presence, "absent", "present", "unverified") ||
                !Allowed(input.Customer.TradeType, "sell", "other", "unknown") ||
                !Allowed(input.Customer.Phase, "absent", "arriving", "dialogue", "trading", "leaving", "unknown") ||
                !Allowed(input.Customer.DialogueStage, "absent", "typing", "waiting", "choices", "updating", "unknown") ||
                !Allowed(input.Ui.Status, "consistent", "updating", "unverified", "blocked") ||
                !Allowed(input.Ui.Coverage, "known_roots_only", "verified") ||
                !Allowed(input.Quote.Status, "complete", "unverified", "unavailable") ||
                !Allowed(input.Quote.Mode, "normal", "wholesale", "unknown") ||
                !ValidText(input.Customer.Phase, 64) || !ValidText(input.Quote.Reason, 128)) return "invalid_sample";
            if (input.Customer.Presence == "present" && input.Customer.Identity == null) return "invalid_customer_identity";
            if (input.Customer.Presence != "present" && input.Customer.Identity != null) return "invalid_customer_identity";
            if ((input.Customer.Identity != null && !input.Customer.Identity.GetType().IsClass) ||
                (input.Customer.DialogueIdentity != null && !input.Customer.DialogueIdentity.GetType().IsClass)) return "invalid_customer_identity";
            var known = new HashSet<string>(new[] { "front", "back", "player" }, StringComparer.Ordinal);
            var items = new HashSet<object>(RefComparer.Instance);
            int totalItems = 0;
            int stringBudget = 0;
            AddText(ref stringBudget, input.Customer.Presence);
            AddText(ref stringBudget, input.Customer.TradeType);
            AddText(ref stringBudget, input.Customer.Phase);
            AddText(ref stringBudget, input.Customer.DialogueStage);
            AddText(ref stringBudget, input.Quote.Status);
            AddText(ref stringBudget, input.Quote.Mode);
            AddText(ref stringBudget, input.Quote.Reason);
            foreach (var container in input.Containers)
            {
                if (container == null || !known.Remove(container.Container) || container.Items == null ||
                    container.Items.Length > 64 || (totalItems += container.Items.Length) > 64 || !Allowed(container.Completeness, "complete", "incomplete", "unavailable", "unimplemented") ||
                    !ValidText(container.Reason, 128)) return "invalid_container";
                if (container.Completeness == "complete" ? container.Reason != null : string.IsNullOrEmpty(container.Reason))
                    return "invalid_container";
                AddText(ref stringBudget, container.Container);
                AddText(ref stringBudget, container.Completeness);
                AddText(ref stringBudget, container.Reason);
                if (container.Completeness == "unavailable" && container.Items.Length != 0) return "incomplete_sample";
                foreach (var item in container.Items)
                {
                    if (item == null || item.Identity == null || !item.Identity.GetType().IsClass || item.Units <= 0 ||
                        !ValidText(item.Name, 256) || !Allowed(item.ShapeStatus, "known", "unverified") ||
                        (item.Width.HasValue != item.Height.HasValue) ||
                        (item.ShapeStatus == "known" && !item.Width.HasValue) ||
                        (item.Width.HasValue && (item.Width.Value <= 0 || item.Height.Value <= 0))) return "invalid_item";
                    if (!items.Add(item.Identity)) return "duplicate_item_identity";
                    AddText(ref stringBudget, item.Name);
                }
            }
            foreach (var row in input.Quote.Rows)
                if (row == null || !ValidText(row.Name, 256) || !ValidText(row.PriceText, 256) ||
                    (row.Amount.HasValue && row.Amount.Value < 0)) return "invalid_quote_row";
            foreach (var row in input.Quote.Rows) { AddText(ref stringBudget, row.Name); AddText(ref stringBudget, row.PriceText); }
            AddText(ref stringBudget, input.Ui.Status);
            AddText(ref stringBudget, input.Ui.Coverage);
            foreach (string blocker in input.Ui.Blockers)
            {
                if (!ValidText(blocker, 128) || string.IsNullOrEmpty(blocker)) return "invalid_blocker";
                AddText(ref stringBudget, blocker);
            }
            if (input.Customer.DialogueIdentity == null && input.Customer.OptionIdentities.Length != 0) return "invalid_dialogue_identity";
            var optionSet = new HashSet<object>(RefComparer.Instance);
            foreach (object option in input.Customer.OptionIdentities)
                if (!optionSet.Add(option)) return "duplicate_option_identity";
            foreach (object option in input.Customer.OptionIdentities) if (option == null || !option.GetType().IsClass) return "invalid_option_identity";
            if (input.Quote.BindingVerified && input.Quote.Status != "complete") return "invalid_quote_binding";
            if (stringBudget > 8192) return "sample_limit_exceeded";
            return null;
        }

        private static void AddText(ref int budget, string value)
        {
            if (value != null) budget += value.Length;
        }

        private static bool SameIdentities(object[] left, object[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (!ReferenceEquals(left[i], right[i])) return false;
            return true;
        }

        private static bool Allowed(string value, params string[] allowed)
        {
            foreach (string item in allowed) if (string.Equals(value, item, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool ValidText(string value, int max) => value == null || value.Length <= max;

    }
}
