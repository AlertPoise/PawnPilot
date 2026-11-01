# Wire v3 — business observation and bounded action policies

Development contract. Wire v2 remains frozen and readable, including historical journal evidence.
The journal container format remains 2; each record retains its original wire schema and fingerprint.
No historical snapshot/result is relabelled. Broker accepts v2 and v3 sessions, but a session must not mix
snapshot/command/result versions. MCP models explicitly distinguish both versions.

v3 retains v2 envelopes and canonical request hashing. The only snapshot shape change is
`business`: the five v2 keys plus mandatory `details`. The broker reads both historical business
details schema 1 and current schema 2; the outer wire version remains 3.
The authoritative field names are `Runtime/BusinessState.cs`; all fields including nullable ones
are serialized, collections are bounded to 64 items/rows and 3 containers, and text to 256 characters.
`support` is unsupported / partial / available. Partial observations are never economic authority.

Details enums:
- status: unavailable / partial / complete
- customer.presence: absent / present / unverified; arrived: boolean or null
- customer.trade_type: sell / other / unknown
- customer.phase: absent / arriving / dialogue / trading / leaving / unknown
- dialogue_stage: absent / typing / waiting / choices / updating / unknown
- container: front / back / player; completeness: complete / incomplete / unavailable / unimplemented
- item.shape_status: known / unverified; dimensions positive integers or null
- quote.status: complete / unverified / unavailable; mode: normal / wholesale / unknown
- ui.status: consistent / updating / unverified / blocked; coverage: known_roots_only / verified
- capacity.status: sufficient / insufficient / unverified; scope/reason: bounded strings
- candidates.status: candidate / blocked / unverified; never an executable action by itself.

References use the existing bounded ASCII reference grammar. World/customer replacement invalidates
offer/collection/dialogue refs. Direct collection references must exactly match emitted item objects.
Unknown/filtered containers are not represented as complete empty containers. Item identity follows
reference identity, never name, price, list position or a mutating game ID getter.

Current business details schema 2 adds `front_collection_fingerprint` for the exact front collection,
and `customer.dialogue_text`, `is_typing`, `waiting_for_advance`, plus ordered visible `options` with
text. `option_refs` remains for compatibility and must equal the option references in the same order.
`quote.wholesale_total` is separate from `displayed_total`; the latter is always the normal OnBuyAll
total used by `purchase_visible_offer`.
Schema 1 remains strict and readable for existing journal results. In schema 2, the top-level
`business.item_refs` and `collection_fingerprint` identify the current native purchase subset
(non-owned back items, in native order); `back.item_refs` and `items` continue to describe the full
back collection, including player-owned objects. `prepare_purchase_items` binds to the front list and
`front_collection_fingerprint` instead.

`shape_status=known` describes the current orientation-aware bounding width/height only, not the
occupied-cell mask, grid placement, transfer rule or proven receiving capacity. Runtime capture
uses a stricter conservative escaped-text budget than the wire maximum and degrades oversized
observations explicitly; it does not truncate a collection and call it complete.

Action argument extension (v3 only): optional `dialogue_ref`, `mode`, `amount`, `max_spend`,
`authorization_ref`, in addition to the five existing optional fields. Amount/max_spend are nonnegative
integers, not bool. Strings use the reference grammar. Explicit null and extra properties are rejected.
Fingerprint includes every supplied argument, with normal sorted canonical JSON, and ordered arrays.
Production action shapes are action-specific. In particular `purchase_visible_offer` does not accept or
require `authorization_ref`; the field remains only in the generic fingerprinted request vocabulary.

Registered non-economic/business-interaction policies:
- close_menu: {}; user-directed diagnostic only, never automatic pause recovery.
- start_business: {}.
- receive_next_customer: {}.
- advance_dialogue: customer_ref, dialogue_ref, mode (skip / next).
- choose_dialogue_option: customer_ref, dialogue_ref, option_ref.
- prepare_purchase_items: customer_ref, item_refs, collection_fingerprint.
- cancel_confirmation: {}.
- refuse_trade: customer_ref.
- dismiss_customer: customer_ref.

Registration does not establish runtime availability. A command must exactly match a legal_actions
entry of the same snapshot, pass that action's snapshot-local reference and phase checks, then pass the
native adapter immediately before its single invocation. Actions are enabled independently; unrelated
observation completeness does not close opening or receiving. `refuse_trade` and `cancel_confirmation` remain descriptive and
are not enabled for production dispatch. `purchase_visible_offer` additionally requires a
schema 2 complete normal quote, the current non-owned back subset, sufficient capacity, fresh cash, and
the current customer. A candidate alone never makes an action executable. Postconditions are
action-specific and immutable evidence is retained under the existing ACK/unknown rules.

`wait_for_update(operable)` preserves v2's basic UI/lifecycle meaning. It is not an assertion that
legal_actions is nonempty; every write still requires a fresh legal entry and its own policy.

`purchase_visible_offer` arguments: customer_ref, offer_ref, item_refs, collection_fingerprint,
mode (`normal`), amount, max_spend. The amount must equal the current normal total, and `amount <= max_spend <= cash`.
No separate approval token is required; exact current snapshot legality and the native purchase
preflight/verifier remain mandatory.

v2 validators, schema and fixture remain unchanged. Strict v3 schemas and tests reject unknown keys,
cross-version results, invalid collection references, and legal arguments that do not exactly match
the current snapshot. Business schema 1 retains its original stricter quote semantics; schema 2 scopes
quote validity to the visible normal total and current purchasable subset. No generic unknown
reconciliation is added.

## Transport size budgets

The existing v2 HTTP request and response limit remains 64 KiB. A v3 game snapshot is limited to
64 KiB measured as deterministic compact JSON encoded as UTF-8 (sorted keys, no insignificant
whitespace). This makes the limit independent of source key order and spacing. V3 exchange bodies,
broker replies, and MCP broker reads are limited to 256 KiB so an exchange can retain its current
snapshot plus bounded before/after evidence in one terminal result. Each embedded v3 snapshot still
has its own 64 KiB limit. Oversized messages fail closed before delivery or ACK.

HTTP clients may omit `X-PawnPilot-Wire-Version` for requests no larger than 64 KiB. A larger v3
request must predeclare `X-PawnPilot-Wire-Version: 3`; if present, the header must be `2` or `3`
and must match the body's exact integer `schema_version`. Requests over 256 KiB are rejected.

Container `complete` describes direct-member enumeration and local geometry/known-mask checks only,
not global unobstructed visibility. UI `unverified` / `known_roots_only` reports unresolved global
coverage, without vetoing independently checked native actions. Current normal quote binding and
known action-specific blockers are evaluated locally. Descriptive candidates never authorize a write;
only the current legal action plus native preflight does.
