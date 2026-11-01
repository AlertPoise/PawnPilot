using System;
using System.Collections.Generic;

namespace PawnPilot
{
    // Wire v3 business schema 1. Null is unknown, never an empty/zero substitute.
    [Serializable] public sealed class BusinessDetails
    {
        public int schema_version = 1;
        public string status = "unavailable";
        public CustomerState customer = new CustomerState();
        public List<ContainerState> containers = new List<ContainerState>();
        public List<ItemState> items = new List<ItemState>();
        public QuoteState quote = new QuoteState();
        public BusinessUiState ui = new BusinessUiState();
        public CapacityState capacity = new CapacityState();
        public PurchaseEligibility eligibility = new PurchaseEligibility();
        public List<BusinessCandidate> candidates = new List<BusinessCandidate>();
    }
    [Serializable] public sealed class CustomerState
    {
        public string presence = "unverified";
        public bool? arrived;
        public string trade_type = "unknown", phase = "unknown";
        public string dialogue_ref;
        public string dialogue_stage = "unknown";
        public List<string> option_refs = new List<string>();
    }
    [Serializable] public sealed class ContainerState
    {
        public string container;
        public string completeness = "unavailable";
        public string reason;
        public List<string> item_refs = new List<string>();
    }
    [Serializable] public sealed class ItemState
    {
        public string item_ref, container, name;
        public int units;
        public bool owned;
        public string shape_status = "unverified";
        public int? width, height;
    }
    [Serializable] public sealed class QuoteRowState
    {
        public string item_ref, name, price_text;
        public int? amount;
    }
    [Serializable] public sealed class QuoteState
    {
        public string status = "unavailable", mode = "unknown";
        public int? displayed_total;
        public List<QuoteRowState> rows = new List<QuoteRowState>();
        public string reason = "not_observed";
    }
    [Serializable] public sealed class BusinessUiState
    {
        public string status = "unverified";
        public List<string> blockers = new List<string>();
        public string coverage = "known_roots_only";
    }
    [Serializable] public sealed class CapacityState
    {
        public string status = "unverified", reason = "native_rules_unverified", scope = "none";
    }
    [Serializable] public sealed class PurchaseEligibility
    {
        public bool purchase_enabled;
        public List<string> missing = new List<string> { "quote_binding", "capacity", "settlement", "ui_coverage" };
    }
    [Serializable] public sealed class BusinessCandidate
    {
        public string action, status, reason;
    }
}
