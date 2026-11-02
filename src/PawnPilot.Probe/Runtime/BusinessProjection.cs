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
        public BusinessBargainInput Bargain;
    }

    public sealed class BusinessBargainInput
    {
        public bool Available;
        public string Phase = "unavailable", Direction = "unknown";
        public int TermsRevision;
        public BusinessBargainOptionInput[] Options = Array.Empty<BusinessBargainOptionInput>();
        public string ReputationText, HintText, LastOptionRef;
        public string Result = "none", ResultText;
    }
    public sealed class BusinessBargainOptionInput
    {
        public object Identity;
        public string Text, Kind;
        public int PriceChangePercent;
        public bool Enabled, Selected;
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
        public string DialogueText;
        public string RequestText;
        public string TradePanel;
        public bool? IsTyping, WaitingForAdvance, MultiBuyDisabled;
        public object[] OptionIdentities = Array.Empty<object>();
        public string[] OptionTexts = Array.Empty<string>();
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
        public string Identifier; // Classification only; never enters a wire DTO.
        public string Name; // Visible item title, from the reviewed native display-name path.
        public int Units;
        public bool Owned;
        public string ShapeStatus = "unverified";
        public int? Width, Height;
    }

    public sealed class BusinessQuoteInput
    {
        public string Status = "unavailable";
        public string Mode = "unknown";
        public int? DisplayedTotal, WholesaleTotal;
        public string Reason = "not_observed";
        public bool BindingVerified;
        public object[] PurchaseIdentities = Array.Empty<object>();
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
                !OneOf(tradeType, "sell", "buy", "other", "unknown") || !OneOf(quoteMode, "normal", "wholesale", "unknown") ||
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

        public static int? ParseVisibleNormalTotal(string rendered)
        {
            if (string.IsNullOrWhiteSpace(rendered)) return null;
            int separator = rendered.IndexOf(" / ", StringComparison.Ordinal);
            if (separator < 0) return ParseVisibleTotal(rendered);
            if (rendered.IndexOf(" / ", separator + 3, StringComparison.Ordinal) >= 0) return null;
            return ParseVisibleTotal(rendered.Substring(0, separator));
        }

        public static int? ParseVisibleWholesaleTotal(string rendered)
        {
            if (string.IsNullOrWhiteSpace(rendered)) return null;
            int separator = rendered.IndexOf(" / ", StringComparison.Ordinal);
            if (separator < 0 || rendered.IndexOf(" / ", separator + 3, StringComparison.Ordinal) >= 0) return null;
            string wholesale = rendered.Substring(separator + 3).Trim();
            int discount = wholesale.IndexOf(" (", StringComparison.Ordinal);
            if (discount >= 0)
            {
                if (!wholesale.EndsWith(")", StringComparison.Ordinal)) return null;
                wholesale = wholesale.Substring(0, discount).TrimEnd();
            }
            return ParseVisibleTotal(wholesale);
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
    public sealed partial class BusinessProjection
    {
        private sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new RefComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        private sealed class ItemRecord
        {
            public object identity;
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
        private readonly bool preserveItemRefsAcrossCustomers;
        private readonly Dictionary<object, RefState> optionRefs = new Dictionary<object, RefState>(RefComparer.Instance);
        private readonly Dictionary<object, RefState> bargainRefs = new Dictionary<object, RefState>(RefComparer.Instance);
        private object[] bargainIdentities = Array.Empty<object>();
        private string bargainContext;
        private int currentOfferTermsRevision;
        private object customerIdentity, dialogueIdentity;
        private string customerRef, dialogueRef, dialogueFingerprint;
        private object[] dialogueOptions = Array.Empty<object>();
        private string[] dialogueOptionTexts = Array.Empty<string>();
        private string currentOfferRef, currentOfferFingerprint;
        private int currentPurchaseAmount;
        private object[] currentPurchaseIdentities = Array.Empty<object>();
        private string[] currentPurchaseItemRefs = Array.Empty<string>();
        private string currentFrontFingerprint;
        private object[] currentFrontIdentities = Array.Empty<object>();
        private string[] currentFrontItemRefs = Array.Empty<string>();
        private string world;
        private long nextReference;

        public BusinessProjection() : this(false) { }

        public BusinessProjection(bool preserveItemRefsAcrossCustomers)
        {
            this.preserveItemRefsAcrossCustomers = preserveItemRefsAcrossCustomers;
        }

        internal BusinessObservation Invalidate(string worldEpoch, string reason)
        {
            ObserveInspectionWorld(worldEpoch);
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

        internal bool TryResolveBargainOption(string worldEpoch, string expectedCustomerRef, string optionRef, out object identity)
        {
            identity = null;
            if (!TryResolveCustomer(worldEpoch, expectedCustomerRef, out _) || string.IsNullOrEmpty(optionRef)) return false;
            foreach (var pair in bargainRefs)
                if (pair.Value.reference == optionRef) { identity = pair.Key; return true; }
            return false;
        }

        internal bool TryResolveItem(string worldEpoch, string expectedCustomerRef, string expectedContainer,
            string itemRef, out object identity)
        {
            identity = null;
            if (!string.Equals(world, worldEpoch, StringComparison.Ordinal) || customerRef == null ||
                !string.Equals(customerRef, expectedCustomerRef, StringComparison.Ordinal) ||
                !Allowed(expectedContainer, "front", "back", "player") || string.IsNullOrEmpty(itemRef)) return false;
            foreach (var pair in itemRefs)
            {
                if (pair.Value.container != expectedContainer || pair.Value.reference != itemRef) continue;
                identity = pair.Key;
                return true;
            }
            return false;
        }

        internal bool TryGetItemReference(string worldEpoch, string expectedContainer, object identity,
            out string itemRef)
        {
            itemRef = null;
            if (identity == null || !string.Equals(world, worldEpoch, StringComparison.Ordinal) ||
                !Allowed(expectedContainer, "front", "back", "player") ||
                !itemRefs.TryGetValue(identity, out var record) || !ReferenceEquals(record.identity, identity) ||
                !string.Equals(record.container, expectedContainer, StringComparison.Ordinal)) return false;
            itemRef = record.reference;
            return !string.IsNullOrEmpty(itemRef);
        }

        internal bool TryResolvePurchase(string worldEpoch, string expectedCustomerRef, string expectedOfferRef,
            string expectedCollectionFingerprint, out object[] orderedItems, out string[] orderedItemRefs, out int amount)
        {
            orderedItems = Array.Empty<object>();
            orderedItemRefs = Array.Empty<string>();
            amount = 0;
            if (!string.Equals(world, worldEpoch, StringComparison.Ordinal) || customerRef == null ||
                !string.Equals(customerRef, expectedCustomerRef, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(currentOfferRef) || currentOfferRef != expectedOfferRef ||
                string.IsNullOrEmpty(currentOfferFingerprint) || currentOfferFingerprint != expectedCollectionFingerprint ||
                currentPurchaseIdentities.Length == 0 || currentPurchaseIdentities.Length != currentPurchaseItemRefs.Length) return false;
            orderedItems = (object[])currentPurchaseIdentities.Clone();
            orderedItemRefs = (string[])currentPurchaseItemRefs.Clone();
            amount = currentPurchaseAmount;
            return true;
        }

        internal bool TryResolveFrontCollection(string worldEpoch, string expectedCustomerRef,
            string expectedFingerprint, out object[] orderedItems, out string[] orderedItemRefs)
        {
            orderedItems = Array.Empty<object>();
            orderedItemRefs = Array.Empty<string>();
            if (!string.Equals(world, worldEpoch, StringComparison.Ordinal) || customerRef == null ||
                !string.Equals(customerRef, expectedCustomerRef, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(currentFrontFingerprint) || currentFrontFingerprint != expectedFingerprint ||
                currentFrontIdentities.Length == 0 || currentFrontIdentities.Length != currentFrontItemRefs.Length) return false;
            orderedItems = (object[])currentFrontIdentities.Clone();
            orderedItemRefs = (string[])currentFrontItemRefs.Clone();
            return true;
        }

        public BusinessObservation Capture(string worldEpoch, bool ready, Func<BusinessInput> read)
        {
            ObserveInspectionWorld(worldEpoch);
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
                ObserveInspection(input, projected);
                projected.details.inspection_handoff = BusinessWire.CloneInspection(inspectionHandoff);
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
            var details = new BusinessDetails { schema_version = 2 };
            var customer = input.Customer;
            bool hasCustomer = customer.Identity != null && customer.Presence == "present";
            if (!ReferenceEquals(customerIdentity, customer.Identity) || (hasCustomer && customerRef == null))
            {
                customerIdentity = customer.Identity;
                customerRef = hasCustomer ? NewRef("customer") : null;
                if (!preserveItemRefsAcrossCustomers) ClearItemRefs();
                ClearDialogueRefs();
                ClearCollectionRefs();
            }
            if (!hasCustomer)
            {
                customerRef = null;
                ClearDialogueRefs();
            }

            details.customer.presence = customer.Presence;
            details.customer.arrived = hasCustomer ? customer.Arrived : null;
            details.customer.trade_type = hasCustomer ? customer.TradeType : "unknown";
            details.customer.request_text = hasCustomer ? customer.RequestText : null;
            details.customer.trade_panel = hasCustomer ? customer.TradePanel : null;
            details.customer.phase = customer.Presence == "absent" ?
                (customer.Phase == "leaving" ? "leaving" : "absent") : hasCustomer ? customer.Phase : "unknown";
            details.customer.dialogue_stage = customer.Presence == "absent" ? "absent" : hasCustomer ? customer.DialogueStage : "unknown";
            if (hasCustomer && customer.DialogueIdentity != null && customer.DialogueStage != "absent")
            {
                details.customer.dialogue_text = customer.DialogueText;
                details.customer.is_typing = customer.IsTyping;
                details.customer.waiting_for_advance = customer.WaitingForAdvance;
            }
            if (hasCustomer && customer.DialogueIdentity != null && customer.DialogueStage != "absent")
            {
                string nextDialogueFingerprint = customer.DialogueStage;
                if (!ReferenceEquals(dialogueIdentity, customer.DialogueIdentity) ||
                    !string.Equals(dialogueFingerprint, nextDialogueFingerprint, StringComparison.Ordinal) ||
                    !SameIdentities(dialogueOptions, customer.OptionIdentities) ||
                    !SameStrings(dialogueOptionTexts, customer.OptionTexts))
                {
                    ClearDialogueRefs();
                    dialogueIdentity = customer.DialogueIdentity;
                    dialogueFingerprint = nextDialogueFingerprint;
                    dialogueOptions = (object[])customer.OptionIdentities.Clone();
                    dialogueOptionTexts = (string[])customer.OptionTexts.Clone();
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
                {
                    string optionRef = optionRefs[customer.OptionIdentities[i]].reference;
                    details.customer.option_refs.Add(optionRef);
                    details.customer.options.Add(new DialogueOptionState { option_ref = optionRef,
                        text = customer.OptionTexts[i] });
                }
            }
            else ClearDialogueRefs();

            details.bargain = ProjectBargain(input.Bargain, hasCustomer);

            var containersByName = new Dictionary<string, BusinessContainerInput>(StringComparer.Ordinal);
            foreach (var container in input.Containers) containersByName.Add(container.Container, container);
            bool uiBlocksItems = BusinessUiEvidence.BlocksTradeObservation(input.Ui.Status, input.Ui.Blockers);
            var presentObservedItems = new HashSet<object>(RefComparer.Instance);
            var itemReferenceByIdentity = new Dictionary<object, string>(RefComparer.Instance);
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
                    itemReferenceByIdentity[sourceItem.Identity] = itemRef;
                    details.items.Add(new ItemState { item_ref = itemRef, container = name,
                        name = sourceItem.Name, units = sourceItem.Units, owned = sourceItem.Owned,
                        shape_status = sourceItem.ShapeStatus, width = sourceItem.Width, height = sourceItem.Height });
                }
            }
            RemoveUnobservedItemReferences(presentObservedItems);

            // Schema 2 status reports the bounded business roots, not global UI coverage.
            details.status = allContainersComplete ? "complete" : "partial";
            details.ui.status = input.Ui.Status;
            details.ui.coverage = input.Ui.Coverage;
            details.ui.blockers.AddRange(input.Ui.Blockers);

            var purchaseRefs = new List<string>();
            var purchaseIdentities = new List<object>();
            bool purchaseSetKnown = TryBuildPurchaseSet(customer, input.Quote, containersByName,
                itemReferenceByIdentity, purchaseIdentities, purchaseRefs);
            bool quoteComplete = !uiBlocksItems && purchaseSetKnown && hasCustomer && customer.Arrived == true &&
                (customer.TradeType == "sell" || customer.TradeType == "buy") &&
                string.Equals(customer.Phase, "trading", StringComparison.Ordinal) &&
                input.Quote.Status == "complete" && input.Quote.Mode == "normal" &&
                input.Quote.BindingVerified && input.Quote.DisplayedTotal.HasValue && purchaseRefs.Count > 0;
            ProjectQuote(input.Quote, details.quote, quoteComplete, uiBlocksItems);

            string frontFingerprint = null;
            var frontIdentities = new List<object>();
            var frontRefs = new List<string>();
            if (!uiBlocksItems && hasCustomer && customer.TradeType == "sell" &&
                containersByName.TryGetValue("front", out var front) && front.Completeness == "complete")
            {
                var frontMembers = new List<BusinessCollectionMember>(front.Items.Length);
                foreach (var item in front.Items)
                {
                    if (!itemReferenceByIdentity.TryGetValue(item.Identity, out string reference)) continue;
                    frontIdentities.Add(item.Identity);
                    frontRefs.Add(reference);
                    frontMembers.Add(new BusinessCollectionMember { item_ref = reference, container = "front",
                        units = item.Units, owned = item.Owned });
                }
                frontFingerprint = BusinessCollection.Fingerprint(worldEpoch, customerRef, customer.TradeType,
                    frontMembers, "unknown", "unverified", null);
            }
            currentFrontFingerprint = frontFingerprint;
            currentFrontIdentities = frontIdentities.ToArray();
            currentFrontItemRefs = frontRefs.ToArray();

            string collectionFingerprint = null;
            string nextOfferRef = null;
            if (quoteComplete)
            {
                var purchaseMembers = new List<BusinessCollectionMember>(purchaseRefs.Count);
                var backItems = containersByName["back"].Items;
                foreach (object identity in purchaseIdentities)
                    foreach (var item in backItems)
                        if (ReferenceEquals(item.Identity, identity))
                        {
                            purchaseMembers.Add(new BusinessCollectionMember { item_ref = itemReferenceByIdentity[identity],
                                container = "back", units = item.Units, owned = item.Owned });
                            break;
                        }
                collectionFingerprint = BusinessCollection.Fingerprint(worldEpoch, customerRef, customer.TradeType,
                    purchaseMembers, "normal", "complete", details.quote.displayed_total);
                if (collectionFingerprint != null)
                {
                    int termsRevision = input.Bargain?.TermsRevision ?? 0;
                    if (!string.Equals(currentOfferFingerprint, collectionFingerprint, StringComparison.Ordinal) || currentOfferRef == null || currentOfferTermsRevision != termsRevision)
                        currentOfferRef = NewRef("offer");
                    currentOfferTermsRevision = termsRevision;
                    currentOfferFingerprint = collectionFingerprint;
                    nextOfferRef = currentOfferRef;
                    currentPurchaseIdentities = purchaseIdentities.ToArray();
                    currentPurchaseItemRefs = purchaseRefs.ToArray();
                    currentPurchaseAmount = details.quote.displayed_total.Value;
                }
                else quoteComplete = false;
            }
            if (!quoteComplete)
            {
                currentOfferRef = null;
                currentOfferFingerprint = null;
                currentPurchaseIdentities = Array.Empty<object>();
                currentPurchaseItemRefs = Array.Empty<string>();
                currentPurchaseAmount = 0;
                collectionFingerprint = null;
                nextOfferRef = null;
            }
            details.front_collection_fingerprint = frontFingerprint;
            // Unity purchase adapter fills current-grid eligibility after projection.
            details.capacity.status = "unverified";
            details.capacity.scope = "none";
            details.capacity.reason = "native_rules_unverified";
            details.eligibility.purchase_enabled = false;
            details.eligibility.missing = new List<string> { quoteComplete ? "purchase_preflight_pending" : "quote_binding" };
            foreach (string action in new[] { "start_business", "receive_next_customer", "advance_dialogue",
                "choose_dialogue_option", "prepare_purchase_items", "cancel_confirmation", "dismiss_customer", "open_trade_panel",
                "open_bargain_panel", "close_bargain_panel", "choose_bargain_option" })
                details.candidates.Add(new BusinessCandidate { action = action, status = uiBlocksItems ? "blocked" : "unverified",
                    reason = uiBlocksItems ? "ui_blocked_or_unverified" : "native_scope_not_verified" });
            details.candidates.Add(new BusinessCandidate { action = "refuse_trade", status = "blocked", reason = "unsupported_native_path" });
            details.candidates.Add(new BusinessCandidate { action = "purchase_visible_offer", status = "unverified", reason = "purchase_preflight_pending" });

            return new BusinessObservation
            {
                support = quoteComplete ? "available" : "partial",
                customer_ref = customerRef,
                item_refs = purchaseRefs,
                offer_ref = nextOfferRef,
                collection_fingerprint = collectionFingerprint,
                details = details
            };
        }

        private static bool TryBuildPurchaseSet(BusinessCustomerInput customer, BusinessQuoteInput quote,
            Dictionary<string, BusinessContainerInput> containers,
            Dictionary<object, string> itemReferenceByIdentity, List<object> identities, List<string> references)
        {
            identities.Clear(); references.Clear();
            if (customer == null || quote == null || customer.Presence != "present" || customer.Identity == null ||
                (customer.TradeType != "sell" && customer.TradeType != "buy") ||
                (customer.TradeType == "sell" && !customer.MultiBuyDisabled.HasValue) || quote.PurchaseIdentities == null ||
                !containers.TryGetValue("back", out var back) || back.Completeness != "complete") return false;

            // M3-A deliberately prepares one owned item into an empty counter. The native
            // reader binds that exact accepted item to BuildBuying's ordinary sale total.
            if (customer.TradeType == "buy")
            {
                if (back.Items.Length != 1 || !back.Items[0].Owned || quote.PurchaseIdentities.Length != 1 ||
                    !ReferenceEquals(back.Items[0].Identity, quote.PurchaseIdentities[0]) ||
                    !itemReferenceByIdentity.TryGetValue(back.Items[0].Identity, out var saleRef)) return false;
                identities.Add(back.Items[0].Identity);
                references.Add(saleRef);
                return true;
            }

            var expected = new List<BusinessItemInput>();
            foreach (var item in back.Items) if (!item.Owned) expected.Add(item);
            if (expected.Count == 0) return false;
            if (!customer.MultiBuyDisabled.Value || expected.Count == 1)
            {
                if (quote.PurchaseIdentities.Length != expected.Count) return false;
                for (int i = 0; i < expected.Count; i++)
                    if (!ReferenceEquals(expected[i].Identity, quote.PurchaseIdentities[i])) return false;
            }
            else
            {
                // A disabled multi-buy button consumes one selected row. The reader supplies
                // the one visible selected row in native list order; ambiguity is not guessed.
                if (quote.PurchaseIdentities.Length != 1) return false;
                bool found = false;
                foreach (var item in expected)
                    if (ReferenceEquals(item.Identity, quote.PurchaseIdentities[0])) { found = true; break; }
                if (!found) return false;
            }
            foreach (object identity in quote.PurchaseIdentities)
            {
                if (identity == null || !itemReferenceByIdentity.TryGetValue(identity, out string reference))
                    return false;
                identities.Add(identity);
                references.Add(reference);
            }
            return identities.Count > 0;
        }

        private static void ProjectQuote(BusinessQuoteInput source, QuoteState target, bool verified, bool uiBlocked)
        {
            if (uiBlocked)
            {
                target.status = "unavailable";
                target.mode = "unknown";
                target.displayed_total = null;
                target.wholesale_total = null;
                target.reason = "ui_blocked_or_unverified";
                return;
            }
            target.status = verified ? "complete" : source.Status == "unavailable" ? "unavailable" : "unverified";
            target.mode = string.Equals(source.Mode, "normal", StringComparison.Ordinal) ||
                string.Equals(source.Mode, "wholesale", StringComparison.Ordinal) ? source.Mode : "unknown";
            target.displayed_total = source.DisplayedTotal;
            target.wholesale_total = source.WholesaleTotal;
            target.reason = verified ? null : source.Reason;
            // Rows are optional displayed text; whole-offer binding is to the normal total
            // and the exact native purchase collection, not a fabricated row-to-item match.
            foreach (var row in source.Rows)
                target.rows.Add(new QuoteRowState { item_ref = null, name = row.Name,
                    price_text = row.PriceText, amount = null });
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
                record = new ItemRecord { identity = item.Identity, reference = NewRef("item"), container = container,
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

        private BargainState ProjectBargain(BusinessBargainInput source, bool hasCustomer)
        {
            bool active = hasCustomer && source != null && (source.Phase == "choosing" || source.Phase == "responding");
            if (!active) { bargainRefs.Clear(); bargainIdentities = Array.Empty<object>(); bargainContext = null; }
            if (source == null) return null;
            var state = new BargainState { available = source.Available, phase = source.Phase, direction = source.Direction,
                terms_revision = source.TermsRevision, reputation_text = source.ReputationText, hint_text = source.HintText,
                last_option_ref = source.LastOptionRef, result = source.Result, result_text = source.ResultText };
            if (!active) return state;
            var identities = new object[source.Options.Length];
            var context = new StringBuilder();
            context.Append(customerRef).Append('|').Append(source.Direction).Append('|').Append(source.Phase).Append('|').Append(source.Available).Append('|').Append(source.TermsRevision);
            for (int i = 0; i < source.Options.Length; i++)
            {
                var option = source.Options[i]; identities[i] = option.Identity;
                context.Append('|').Append(option.Text.Length).Append(':').Append(option.Text).Append('|').Append(option.Kind)
                    .Append('|').Append(option.PriceChangePercent).Append('|').Append(option.Enabled).Append('|').Append(option.Selected);
            }
            string nextContext = context.ToString();
            if (bargainContext != nextContext || !SameIdentities(bargainIdentities, identities))
            {
                bargainRefs.Clear(); bargainContext = nextContext; bargainIdentities = identities;
                foreach (object identity in identities) bargainRefs.Add(identity, new RefState { identity = identity, reference = NewRef("bargain-option") });
            }
            foreach (var option in source.Options) state.options.Add(new BargainOptionState { option_ref = bargainRefs[option.Identity].reference,
                text = option.Text, kind = option.Kind, price_change_percent = option.PriceChangePercent, enabled = option.Enabled, selected = option.Selected });
            return state;
        }

        private void ClearDialogueRefs()
        {
            optionRefs.Clear();
            dialogueIdentity = null;
            dialogueFingerprint = null;
            dialogueOptions = Array.Empty<object>();
            dialogueOptionTexts = Array.Empty<string>();
            dialogueRef = null;
        }

        private void ClearItemRefs() => itemRefs.Clear();

        private void ClearAll()
        {
            inspectionInput = null;
            ClearItemRefs();
            ClearDialogueRefs();
            ClearCollectionRefs();
            bargainRefs.Clear(); bargainIdentities = Array.Empty<object>(); bargainContext = null;
            customerIdentity = null;
            customerRef = null;
        }

        private void ClearCollectionRefs()
        {
            currentOfferRef = null;
            currentOfferFingerprint = null;
            currentPurchaseAmount = 0;
            currentPurchaseIdentities = Array.Empty<object>();
            currentPurchaseItemRefs = Array.Empty<string>();
            currentFrontFingerprint = null;
            currentFrontIdentities = Array.Empty<object>();
            currentFrontItemRefs = Array.Empty<string>();
        }

        private BusinessObservation Unavailable(string reason) => new BusinessObservation
        {
            support = "partial",
            customer_ref = null,
            item_refs = new List<string>(),
            offer_ref = null,
            collection_fingerprint = null,
            details = new BusinessDetails { schema_version = 2, status = "unavailable",
                inspection_handoff = BusinessWire.CloneInspection(inspectionHandoff),
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
            if (input.Bargain != null)
            {
                var b = input.Bargain;
                if (b.Options == null || b.Options.Length > 16) return "invalid_bargain";
                var dto = new BargainState { available = b.Available, phase = b.Phase, direction = b.Direction,
                    terms_revision = b.TermsRevision, reputation_text = b.ReputationText, hint_text = b.HintText,
                    last_option_ref = b.LastOptionRef, result = b.Result, result_text = b.ResultText };
                var identities = new HashSet<object>(RefComparer.Instance);
                for (int i = 0; i < b.Options.Length; i++)
                {
                    var option = b.Options[i];
                    if (option == null || option.Identity == null || !option.Identity.GetType().IsClass || !identities.Add(option.Identity)) return "invalid_bargain_identity";
                    dto.options.Add(new BargainOptionState { option_ref = "input-" + i, text = option.Text, kind = option.Kind,
                        price_change_percent = option.PriceChangePercent, enabled = option.Enabled, selected = option.Selected });
                }
                if (!BusinessWire.ValidateBargain(dto)) return "invalid_bargain";
            }
            if (input.Containers.Length > 3 || input.Quote.Rows == null || input.Quote.Rows.Length > 64 ||
                input.Ui.Blockers == null || input.Ui.Blockers.Length > 16 || input.Customer.OptionIdentities == null ||
                input.Customer.OptionIdentities.Length > 16 || input.Customer.OptionTexts == null ||
                input.Customer.OptionTexts.Length > 16 || input.Customer.OptionTexts.Length != input.Customer.OptionIdentities.Length ||
                input.Quote.PurchaseIdentities == null || input.Quote.PurchaseIdentities.Length > 64) return "sample_limit_exceeded";
            if (!Allowed(input.Customer.Presence, "absent", "present", "unverified") ||
                !Allowed(input.Customer.TradeType, "sell", "buy", "other", "unknown") ||
                !Allowed(input.Customer.Phase, "absent", "arriving", "dialogue", "trading", "leaving", "unknown") ||
                !Allowed(input.Customer.DialogueStage, "absent", "typing", "waiting", "choices", "updating", "unknown") ||
                !Allowed(input.Ui.Status, "consistent", "updating", "unverified", "blocked") ||
                !Allowed(input.Ui.Coverage, "known_roots_only", "verified") ||
                !Allowed(input.Quote.Status, "complete", "unverified", "unavailable") ||
                !Allowed(input.Quote.Mode, "normal", "wholesale", "unknown") ||
                !ValidText(input.Customer.Phase, 64) || !ValidText(input.Customer.DialogueText, 256) ||
                !ValidText(input.Customer.RequestText, 256) ||
                !ValidText(input.Quote.Reason, 128) ||
                (input.Quote.DisplayedTotal.HasValue && input.Quote.DisplayedTotal.Value < 0) ||
                (input.Quote.WholesaleTotal.HasValue && input.Quote.WholesaleTotal.Value < 0)) return "invalid_sample";
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
            AddText(ref stringBudget, input.Customer.DialogueText);
            AddText(ref stringBudget, input.Customer.RequestText);
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
            foreach (string optionText in input.Customer.OptionTexts)
            {
                if (!ValidText(optionText, 256) || string.IsNullOrEmpty(optionText)) return "invalid_option_text";
                AddText(ref stringBudget, optionText);
            }
            AddText(ref stringBudget, input.Ui.Status);
            AddText(ref stringBudget, input.Ui.Coverage);
            foreach (string blocker in input.Ui.Blockers)
            {
                if (!ValidText(blocker, 128) || string.IsNullOrEmpty(blocker)) return "invalid_blocker";
                AddText(ref stringBudget, blocker);
            }
            if (input.Customer.DialogueIdentity == null && input.Customer.OptionIdentities.Length != 0) return "invalid_dialogue_identity";
            if (input.Customer.DialogueIdentity == null && (input.Customer.DialogueText != null ||
                input.Customer.IsTyping.HasValue || input.Customer.WaitingForAdvance.HasValue)) return "invalid_dialogue_identity";
            var optionSet = new HashSet<object>(RefComparer.Instance);
            foreach (object option in input.Customer.OptionIdentities)
                if (!optionSet.Add(option)) return "duplicate_option_identity";
            foreach (object option in input.Customer.OptionIdentities) if (option == null || !option.GetType().IsClass) return "invalid_option_identity";
            var purchaseSet = new HashSet<object>(RefComparer.Instance);
            foreach (object purchaseItem in input.Quote.PurchaseIdentities)
                if (purchaseItem == null || !purchaseItem.GetType().IsClass || !purchaseSet.Add(purchaseItem)) return "invalid_purchase_identity";
            if (input.Quote.BindingVerified && input.Quote.Status != "complete") return "invalid_quote_binding";
            if (input.Quote.Status == "complete" && (!input.Quote.BindingVerified || input.Quote.Mode != "normal" ||
                !input.Quote.DisplayedTotal.HasValue || input.Quote.DisplayedTotal.Value < 0 ||
                (input.Quote.WholesaleTotal.HasValue && input.Quote.WholesaleTotal.Value < 0))) return "invalid_quote_binding";
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

        private static bool SameStrings(string[] left, string[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
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
