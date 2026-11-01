using System;
using System.Collections.Generic;

namespace PawnPilot
{
    // Strict, Unity-independent validation and copying for the v3 business payload.
    public static class BusinessWire
    {
        private const int MaxText = 256;
        private const int MaxRefs = 64;

        public static bool Validate(BusinessObservation business)
        {
            if (business == null || !OneOf(business.support, "unsupported", "partial", "available") ||
                !OptionalRef(business.customer_ref) || !OptionalRef(business.offer_ref) ||
                !OptionalRef(business.collection_fingerprint) || !ValidList(business.item_refs, MaxRefs, false) ||
                business.details == null) return false;

            BusinessDetails d = business.details;
            if ((d.schema_version != 1 && d.schema_version != 2) || !OneOf(d.status, "unavailable", "partial", "complete") ||
                d.customer == null || d.containers == null || d.items == null || d.quote == null ||
                d.ui == null || d.capacity == null || d.eligibility == null || d.candidates == null ||
                d.containers.Count > 3 || d.items.Count > MaxRefs || d.candidates.Count > MaxRefs) return false;
            if (d.customer.presence != "present" && (business.customer_ref != null || business.collection_fingerprint != null ||
                d.customer.dialogue_ref != null || d.customer.option_refs == null || d.customer.option_refs.Count != 0)) return false;

            if ((d.schema_version == 1 && d.front_collection_fingerprint != null) ||
                (d.schema_version == 1 && d.bargain != null) || (d.bargain != null && !ValidateBargain(d.bargain)) ||
                (d.schema_version == 2 && !OptionalRef(d.front_collection_fingerprint)) ||
                !ValidateCustomer(d.customer, business.customer_ref, d.schema_version) || !ValidateUi(d.ui) ||
                !ValidateItemsAndContainers(d.containers, d.items, business.item_refs, d) ||
                !ValidateFrontFingerprint(d) ||
                !ValidateQuote(d.quote, d, business) || !ValidateCapacity(d.capacity) ||
                !ValidateEligibility(d.eligibility) || !ValidateCandidates(d.candidates)) return false;
            if (d.eligibility.purchase_enabled && (d.schema_version != 2 || d.quote.status != "complete" ||
                d.quote.mode != "normal" || d.capacity.status != "sufficient" ||
                d.customer.trade_type != "sell" || business.offer_ref == null || business.customer_ref == null || business.item_refs.Count == 0)) return false;
            return WithinProductionTextBudget(business);
        }

        // Conservative escaped-text budget; even all controls/non-ASCII require at most six
        // bytes per UTF-16 unit. Bounded object keys/scalars have a separate <30 KiB allowance.
        // The Unity serializer also checks the final snapshot's actual UTF-8 length.
        private static bool WithinProductionTextBudget(BusinessObservation b)
        {
            long bytes = 0;
            void S(string value) { if (value != null) bytes += 2L + 6L * value.Length; }
            void L(List<string> values) { foreach (var value in values) S(value); }
            S(b.support); S(b.customer_ref); S(b.offer_ref); S(b.collection_fingerprint); L(b.item_refs);
            var d = b.details;
            S(d.status); S(d.customer.presence); S(d.customer.trade_type); S(d.customer.phase); S(d.customer.request_text);
            S(d.customer.trade_panel);
            S(d.customer.dialogue_ref); S(d.customer.dialogue_stage); S(d.customer.dialogue_text);
            L(d.customer.option_refs);
            if (d.customer.options != null)
                foreach (var option in d.customer.options) { S(option?.option_ref); S(option?.text); }
            foreach (var c in d.containers) { S(c.container); S(c.completeness); S(c.reason); L(c.item_refs); }
            foreach (var i in d.items) { S(i.item_ref); S(i.container); S(i.name); S(i.shape_status); }
            S(d.front_collection_fingerprint); S(d.quote.status); S(d.quote.mode); S(d.quote.reason);
            if (d.bargain != null)
            {
                S(d.bargain.phase); S(d.bargain.direction); S(d.bargain.reputation_text); S(d.bargain.hint_text);
                S(d.bargain.last_option_ref); S(d.bargain.result); S(d.bargain.result_text);
                foreach (var option in d.bargain.options) { S(option.option_ref); S(option.text); S(option.kind); }
            }
            foreach (var row in d.quote.rows) { S(row.item_ref); S(row.name); S(row.price_text); }
            S(d.ui.status); S(d.ui.coverage); L(d.ui.blockers);
            S(d.capacity.status); S(d.capacity.reason); S(d.capacity.scope); L(d.eligibility.missing);
            foreach (var c in d.candidates) { S(c.action); S(c.status); S(c.reason); }
            return bytes <= 30000;
        }

        public static BusinessDetails Clone(BusinessDetails details)
        {
            if (details == null) return null;
            return new BusinessDetails
            {
                schema_version = details.schema_version,
                status = details.status,
                customer = details.customer == null ? null : new CustomerState
                {
                    presence = details.customer.presence, arrived = details.customer.arrived,
                    trade_type = details.customer.trade_type, phase = details.customer.phase,
                    request_text = details.customer.request_text,
                    trade_panel = details.customer.trade_panel,
                    dialogue_ref = details.customer.dialogue_ref, dialogue_stage = details.customer.dialogue_stage,
                    dialogue_text = details.customer.dialogue_text, is_typing = details.customer.is_typing,
                    waiting_for_advance = details.customer.waiting_for_advance,
                    option_refs = Copy(details.customer.option_refs),
                    options = CopyMapped(details.customer.options, x => x == null ? null : new DialogueOptionState
                    { option_ref = x.option_ref, text = x.text })
                },
                containers = CopyMapped(details.containers, x => x == null ? null : new ContainerState
                {
                    container = x.container, completeness = x.completeness, reason = x.reason, item_refs = Copy(x.item_refs)
                }),
                items = CopyMapped(details.items, x => x == null ? null : new ItemState
                {
                    item_ref = x.item_ref, container = x.container, name = x.name, units = x.units, owned = x.owned,
                    shape_status = x.shape_status, width = x.width, height = x.height
                }),
                front_collection_fingerprint = details.front_collection_fingerprint,
                bargain = details.bargain == null ? null : new BargainState
                {
                    available = details.bargain.available, phase = details.bargain.phase, direction = details.bargain.direction,
                    terms_revision = details.bargain.terms_revision, reputation_text = details.bargain.reputation_text,
                    hint_text = details.bargain.hint_text, last_option_ref = details.bargain.last_option_ref,
                    result = details.bargain.result, result_text = details.bargain.result_text,
                    options = CopyMapped(details.bargain.options, x => x == null ? null : new BargainOptionState
                    { option_ref = x.option_ref, text = x.text, kind = x.kind, price_change_percent = x.price_change_percent,
                        enabled = x.enabled, selected = x.selected })
                },
                quote = details.quote == null ? null : new QuoteState
                {
                    status = details.quote.status, mode = details.quote.mode, displayed_total = details.quote.displayed_total,
                    wholesale_total = details.quote.wholesale_total,
                    rows = CopyMapped(details.quote.rows, x => x == null ? null : new QuoteRowState
                    { item_ref = x.item_ref, name = x.name, price_text = x.price_text, amount = x.amount }),
                    reason = details.quote.reason
                },
                ui = details.ui == null ? null : new BusinessUiState
                { status = details.ui.status, blockers = Copy(details.ui.blockers), coverage = details.ui.coverage },
                capacity = details.capacity == null ? null : new CapacityState
                { status = details.capacity.status, reason = details.capacity.reason, scope = details.capacity.scope },
                eligibility = details.eligibility == null ? null : new PurchaseEligibility
                { purchase_enabled = details.eligibility.purchase_enabled, missing = Copy(details.eligibility.missing) },
                candidates = CopyMapped(details.candidates, x => x == null ? null : new BusinessCandidate
                { action = x.action, status = x.status, reason = x.reason })
            };
        }

        internal static bool ValidateBargain(BargainState b)
        {
            if (b == null || !OneOf(b.phase, "unavailable", "closed", "choosing", "responding") ||
                !OneOf(b.direction, "buy", "sell", "unknown") || b.terms_revision < 0 ||
                b.options == null || b.options.Count > 16 || !OptionalText(b.reputation_text) || !OptionalText(b.hint_text) ||
                !OptionalRef(b.last_option_ref) || !OneOf(b.result, "none", "responding", "price_changed", "unchanged", "customer_left", "unknown") ||
                !OptionalText(b.result_text) || ((b.phase == "closed" || b.phase == "unavailable") && b.options.Count != 0)) return false;
            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in b.options)
                if (option == null || !ValidRef(option.option_ref) || !refs.Add(option.option_ref) ||
                    string.IsNullOrEmpty(option.text) || !OptionalText(option.text) ||
                    !OneOf(option.kind, "lower_purchase_price", "raise_purchase_price", "raise_sale_price", "lower_sale_price") ||
                    option.price_change_percent == 0 || option.price_change_percent < -100 || option.price_change_percent > 100) return false;
            return true;
        }

        private static bool ValidateCustomer(CustomerState c, string customerRef, int schemaVersion)
        {
            if (!OneOf(c.presence, "absent", "present", "unverified") ||
                (schemaVersion == 1 && (c.request_text != null || c.trade_panel != null)) ||
                (c.trade_panel != null && !OneOf(c.trade_panel, "closed", "buy", "sell", "unavailable")) ||
                !OneOf(c.trade_type, "sell", "buy", "other", "unknown") ||
                (schemaVersion == 1 && c.trade_type == "buy") ||
                !OneOf(c.phase, "absent", "arriving", "dialogue", "trading", "leaving", "unknown") ||
                !OneOf(c.dialogue_stage, "absent", "typing", "waiting", "choices", "updating", "unknown") ||
                !OptionalRef(c.dialogue_ref) || !ValidList(c.option_refs, MaxRefs, false) || !AllRefs(c.option_refs) || !Unique(c.option_refs)) return false;
            if (c.presence == "absent" && (customerRef != null || c.arrived.HasValue || c.dialogue_ref != null ||
                c.option_refs.Count != 0 || (c.phase != "absent" && c.phase != "leaving") ||
                (c.dialogue_stage != "absent" && c.dialogue_stage != "unknown") || c.trade_type != "unknown")) return false;
            if (c.presence == "present" && customerRef == null) return false;
            if (c.dialogue_ref == null && c.option_refs.Count != 0) return false;
            if (schemaVersion == 2)
            {
                if (!OptionalText(c.dialogue_text) || !OptionalText(c.request_text) || c.options == null || c.options.Count > 16) return false;
                if (c.dialogue_ref == null && (c.dialogue_text != null || c.is_typing.HasValue ||
                    c.waiting_for_advance.HasValue || c.options.Count != 0)) return false;
                if (c.presence == "absent" && (c.dialogue_text != null || c.is_typing.HasValue ||
                    c.waiting_for_advance.HasValue || c.options.Count != 0)) return false;
                if (c.options.Count != c.option_refs.Count) return false;
                for (int i = 0; i < c.options.Count; i++)
                {
                    var option = c.options[i];
                    if (option == null || !ValidRef(option.option_ref) || !OptionalText(option.text) ||
                        string.IsNullOrEmpty(option.text) ||
                        !string.Equals(option.option_ref, c.option_refs[i], StringComparison.Ordinal)) return false;
                }
                if (c.options.Count != 0 && c.dialogue_stage != "choices") return false;
            }
            return true;
        }

        private static bool ValidateItemsAndContainers(List<ContainerState> containers, List<ItemState> items,
            List<string> topRefs, BusinessDetails details)
        {
            var containerNames = new HashSet<string>(StringComparer.Ordinal);
            var byContainer = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var c in containers)
            {
                if (c == null || !OneOf(c.container, "front", "back", "player") || !containerNames.Add(c.container) ||
                    !OneOf(c.completeness, "complete", "incomplete", "unavailable", "unimplemented") ||
                    !OptionalText(c.reason) || !ValidList(c.item_refs, MaxRefs, false) || !AllRefs(c.item_refs) || !Unique(c.item_refs)) return false;
                if (c.completeness == "complete" ? c.reason != null : string.IsNullOrEmpty(c.reason)) return false;
                byContainer.Add(c.container, c.item_refs);
            }

            var itemRefs = new HashSet<string>(StringComparer.Ordinal);
            var emitted = new List<string>();
            var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var i in items)
            {
                if (i == null || !ValidRef(i.item_ref) || !itemRefs.Add(i.item_ref) ||
                    !OneOf(i.container, "front", "back", "player") || !byContainer.ContainsKey(i.container) ||
                    !OptionalText(i.name) || i.units < 1 || !OneOf(i.shape_status, "known", "unverified") ||
                    (i.width.HasValue && i.width.Value < 1) || (i.height.HasValue && i.height.Value < 1) ||
                    (i.shape_status == "known" && (!i.width.HasValue || !i.height.HasValue))) return false;
                foreach (var c in containers)
                    if (c.container == i.container && (c.completeness == "unavailable" || c.completeness == "unimplemented")) return false;
                emitted.Add(i.item_ref);
                if (!grouped.TryGetValue(i.container, out var list)) grouped[i.container] = list = new List<string>();
                list.Add(i.item_ref);
            }
            foreach (var c in containers)
            {
                grouped.TryGetValue(c.container, out var expected);
                expected ??= new List<string>();
                if (!SameSequence(expected, c.item_refs)) return false;
            }

            if (details.schema_version == 1)
            {
                if (!SameSequence(emitted, topRefs)) return false;
            }
            else
            {
                var backRefs = new List<string>();
                ContainerState back = null;
                foreach (var container in containers) if (container.container == "back") back = container;
                if (details.customer.presence == "present" && (details.customer.trade_type == "sell" || details.customer.trade_type == "buy") &&
                    back != null && back.completeness == "complete")
                {
                    foreach (string reference in back.item_refs)
                        foreach (var item in items)
                            if (item.item_ref == reference && item.owned == (details.customer.trade_type == "buy")) { backRefs.Add(reference); break; }
                }
                if (!IsSubsequence(topRefs, backRefs)) return false;
                if (details.quote.status == "complete" && topRefs.Count == 0) return false;
                if (details.customer.presence != "present" && topRefs.Count != 0) return false;
            }
            return true;
        }

        private static bool ValidateFrontFingerprint(BusinessDetails details)
        {
            if (details.front_collection_fingerprint == null) return true;
            if (details.schema_version != 2 || details.customer.presence != "present" ||
                details.customer.trade_type != "sell") return false;
            foreach (var container in details.containers)
                if (container.container == "front") return container.completeness == "complete" && container.item_refs.Count > 0;
            return false;
        }

        private static bool ValidateQuote(QuoteState q, BusinessDetails d, BusinessObservation business)
        {
            if (!OneOf(q.status, "complete", "unverified", "unavailable") || !OneOf(q.mode, "normal", "wholesale", "unknown") ||
                !OptionalText(q.reason) || q.rows == null || q.rows.Count > MaxRefs ||
                (q.displayed_total.HasValue && q.displayed_total.Value < 0) ||
                (q.wholesale_total.HasValue && q.wholesale_total.Value < 0) ||
                (d.schema_version == 1 && q.wholesale_total.HasValue)) return false;
            var refs = new HashSet<string>(StringComparer.Ordinal);
            var rowRefs = new List<string>();
            var emittedRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in d.items) emittedRefs.Add(item.item_ref);
            long normalTotal = 0;
            foreach (var row in q.rows)
            {
                if (row == null || (row.item_ref != null && (!ValidRef(row.item_ref) || !refs.Add(row.item_ref) || !emittedRefs.Contains(row.item_ref))) || !OptionalText(row.name) ||
                    !OptionalText(row.price_text) || (row.amount.HasValue && row.amount.Value < 0)) return false;
                if (row.item_ref != null) rowRefs.Add(row.item_ref);
                if (row.amount.HasValue) normalTotal += row.amount.Value;
                if (normalTotal > int.MaxValue) return false;
            }
            if (q.status != "complete" && business.offer_ref != null) return false;
            if (q.status == "complete" && d.schema_version == 1)
            {
                if (d.status != "complete" || d.ui.status != "consistent" || d.ui.coverage != "verified" ||
                    d.ui.blockers.Count != 0 || d.customer.presence != "present" || business.customer_ref == null ||
                    business.offer_ref == null || !ValidRef(business.collection_fingerprint) || q.mode == "unknown" ||
                    !q.displayed_total.HasValue || q.rows.Count == 0 || rowRefs.Count != q.rows.Count) return false;
                foreach (var row in q.rows) if (!row.amount.HasValue || row.price_text == null) return false;
                if (!OneOf(q.mode, "normal", "wholesale")) return false;
                if (q.mode == "normal" && normalTotal != q.displayed_total.Value) return false;
                ContainerState back = null;
                foreach (var container in d.containers) if (container.container == "back") back = container;
                if (back == null || back.completeness != "complete" || !SameSequence(back.item_refs, rowRefs)) return false;
                foreach (var item in d.items) if (item.container == "back" && item.owned) return false;
            }
            else if (q.status == "complete")
            {
                ContainerState back = null;
                foreach (var container in d.containers) if (container.container == "back") back = container;
                if (d.customer.presence != "present" || (d.customer.trade_type != "sell" && d.customer.trade_type != "buy") ||
                    d.customer.phase != "trading" || business.customer_ref == null ||
                    business.offer_ref == null || !ValidRef(business.collection_fingerprint) ||
                    q.mode != "normal" || !q.displayed_total.HasValue || business.item_refs.Count == 0 ||
                    back == null || back.completeness != "complete" || back.item_refs.Count == 0 ||
                    BusinessUiEvidence.BlocksTradeObservation(d.ui.status, d.ui.blockers)) return false;
                foreach (string reference in business.item_refs)
                {
                    bool found = false;
                    foreach (var item in d.items)
                        if (item.item_ref == reference && item.container == "back" && item.owned == (d.customer.trade_type == "buy")) { found = true; break; }
                    if (!found) return false;
                }
                if (q.reason != null) return false;
            }
            if ((d.status == "unavailable" || (d.schema_version == 1 ? d.ui.status == "blocked" : BusinessUiEvidence.BlocksTradeObservation(d.ui.status, d.ui.blockers)) || d.ui.status == "updating" ||
                (d.schema_version == 1 && (d.ui.status == "unverified" || d.ui.coverage != "verified"))) && q.status == "complete") return false;
            return true;
        }

        private static bool ValidateUi(BusinessUiState ui) =>
            OneOf(ui.status, "consistent", "updating", "unverified", "blocked") &&
            OneOf(ui.coverage, "known_roots_only", "verified") && ValidList(ui.blockers, MaxRefs, false) &&
            AllText(ui.blockers, MaxText);

        private static bool ValidateCapacity(CapacityState c) =>
            OneOf(c.status, "sufficient", "insufficient", "unverified") &&
            ValidText(c.reason, MaxText) && ValidText(c.scope, MaxText);

        private static bool ValidateEligibility(PurchaseEligibility e)
        {
            if (!ValidList(e.missing, MaxRefs, false) || (e.purchase_enabled && e.missing.Count != 0)) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in e.missing) if (!ValidText(x, MaxText) || !seen.Add(x)) return false;
            return true;
        }

        private static bool ValidateCandidates(List<BusinessCandidate> candidates)
        {
            foreach (var c in candidates)
                if (c == null || !OneOf(c.action, "close_menu", "start_business", "receive_next_customer", "advance_dialogue",
                    "choose_dialogue_option", "prepare_purchase_items", "cancel_confirmation", "refuse_trade", "dismiss_customer",
                    "purchase_visible_offer", "prepare_sale_items", "sell_visible_offer", "sort_inventory_default", "open_trade_panel",
                    "open_bargain_panel", "close_bargain_panel", "choose_bargain_option") ||
                    !OneOf(c.status, "candidate", "blocked", "unverified") || !ValidText(c.reason, MaxText)) return false;
            return true;
        }

        private static bool ValidList(List<string> values, int max, bool allowNull) =>
            values != null && values.Count <= max && (allowNull || NoNulls(values));
        private static bool NoNulls(List<string> values) { foreach (var s in values) if (s == null) return false; return true; }
        private static bool AllText(List<string> values, int max) { foreach (var s in values) if (!ValidText(s, max)) return false; return true; }
        private static bool AllRefs(List<string> values) { foreach (var s in values) if (!ValidRef(s)) return false; return true; }
        private static bool Unique(List<string> values)
        { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var s in values) if (s == null || !seen.Add(s)) return false; return true; }
        private static bool OptionalText(string s) => s == null || (s.Length > 0 && s.Length <= MaxText);
        private static bool ValidText(string s, int max) => s != null && s.Length > 0 && s.Length <= max;
        private static bool OptionalRef(string s) => s == null || ValidRef(s);
        private static bool ValidRef(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 128) return false;
            foreach (char c in s)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == ':' || c == '-')) return false;
            return true;
        }
        private static bool OneOf(string value, params string[] allowed)
        { if (value == null) return false; foreach (var x in allowed) if (value == x) return true; return false; }
        private static bool SameSequence(List<string> a, List<string> b)
        {
            if (b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
        private static bool IsSubsequence(List<string> candidate, List<string> source)
        {
            if (candidate == null || source == null) return false;
            int at = 0;
            foreach (string reference in source)
                if (at < candidate.Count && string.Equals(candidate[at], reference, StringComparison.Ordinal)) at++;
            return at == candidate.Count;
        }
        private static List<string> Copy(List<string> source) => source == null ? null : new List<string>(source);
        private static List<TOut> CopyMapped<TIn, TOut>(List<TIn> source, Func<TIn, TOut> map)
        {
            if (source == null) return null;
            var result = new List<TOut>(source.Count);
            foreach (var item in source) result.Add(map(item));
            return result;
        }
    }
}
