# Wire v3 — business observation and bounded action policies

Development contract. Wire v2 remains frozen and readable, including historical journal evidence.
The journal container format remains 2; each record retains its original wire schema and fingerprint.
No historical snapshot/result is relabelled. Broker accepts v2 and v3 sessions, but a session must not mix
snapshot/command/result versions. MCP models explicitly distinguish both versions.

v3 retains v2 envelopes and canonical request hashing. The only snapshot shape change is
`business`: the five v2 keys plus mandatory `details` (business schema_version 1).
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

`shape_status=known` describes the current orientation-aware bounding width/height only, not the
occupied-cell mask, grid placement, transfer rule or proven receiving capacity. Runtime capture
uses a stricter conservative escaped-text budget than the wire maximum and degrades oversized
observations explicitly; it does not truncate a collection and call it complete.

Action argument extension (v3 only): optional `dialogue_ref`, `mode`, `amount`, `max_spend`,
`authorization_ref`, in addition to the five existing optional fields. Amount/max_spend are nonnegative
integers, not bool. Strings use the reference grammar. Explicit null and extra properties are rejected.
Fingerprint includes every supplied argument, with normal sorted canonical JSON, and ordered arrays.

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
entry of the same snapshot, pass the action's pure policy, then pass the native adapter immediately
before its single invocation. The snapshot must provide verified scene-specific UI/callback evidence;
unknown coverage remains closed. Any native boundary may have effects, including dialogue callbacks.
Postconditions are action-specific and immutable evidence is retained under the existing ACK/unknown rules.
In this PARTIAL build **all new business policies remain disabled**; the actual v3 command/dispatch
and legal_actions allowlist contains only close_menu. The listed business arguments can be validated
and fingerprinted as candidates/rejected requests, but cannot reach a native call.

`wait_for_update(operable)` preserves v2's basic UI/lifecycle meaning. It is not an assertion that
legal_actions is nonempty; every write still requires a fresh legal entry and its own policy.

`purchase_visible_offer` arguments: customer_ref, offer_ref, item_refs, collection_fingerprint, mode
(normal), amount, max_spend, authorization_ref. **Not production-enabled** until Q/C/V and runtime
scope are independently established. Purchase preflight/verifier are developed independently; neither
candidate data nor an agent-supplied spending limit can enable the native dispatch.

v2 validators, schema and fixture remain unchanged. Strict v3 schemas and tests must reject unknown
keys, cross-version results, invalid collection references, nonmatching legal arguments, and claims
of economic availability without the production policy. No generic unknown reconciliation is added.

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

Container `complete` describes direct-member enumeration and local geometry/known-mask checks only. It does not assert global unobstructed visibility. With UI `unverified` / `known_roots_only`, returned item attributes and quote rows remain candidates; global visibility and minimum exposure are unresolved. No business write is enabled from these candidates.
