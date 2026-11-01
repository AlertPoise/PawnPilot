# Wire v3 — business observation and bounded action policies

Development contract. Wire v2 remains frozen and readable, including historical journal evidence.
The journal container format remains 2; each record retains its original wire schema and fingerprint.
No historical snapshot/result is relabelled. Broker accepts v2 and v3 sessions, but a session must not mix
snapshot/command/result versions. MCP models explicitly distinguish both versions.

v3 retains v2 envelopes and canonical request hashing. The only snapshot shape change is
`business`: the five v2 keys plus mandatory `details`. The broker reads both historical business
details schema 1 and current schema 2; the outer wire version remains 3.
The authoritative field names are `Runtime/BusinessState.cs`; all required fields including nullable ones
are serialized (the optional request_text may be omitted), collections are bounded to 64 items/rows and 3 containers, and text to 256 characters.
`support` is unsupported / partial / available. Partial observations are never economic authority.

Details enums:
- status: unavailable / partial / complete
- customer.presence: absent / present / unverified; arrived: boolean or null
- customer.trade_type: sell / buy / other / unknown in schema 2; schema 1 remains sell / other / unknown
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
`quote.wholesale_total` is separate from `displayed_total`; the latter is the normal total bound to
the current visible offer for the customer’s trade direction.
Schema 1 remains strict and readable for existing journal results. In schema 2, the top-level
`business.item_refs` and `collection_fingerprint` identify the current trade subset in native order:
non-owned back items for `trade_type=sell`, and player-owned back items for `trade_type=buy`.
`back.item_refs` and `items` continue to describe the full back collection. Optional
`customer.request_text` carries the currently rendered customer request, bounded to 256 characters;
older schema-2 journal records and observations without a rendered request may omit it. `prepare_purchase_items` binds to the front list and
`front_collection_fingerprint` instead.

Optional `customer.trade_panel` reports `closed`, `buy`, `sell`, or `unavailable`. Buy/sell
describe the actually built open panel in NPC direction, matching `customer.trade_type`;
null/omission means unknown in older schema-2 records. Schema 1 does not accept the extension.
An empty panel may expose a visible normal total of zero with quote status `unverified` and
no executable offer. It does not authorize a zero-item purchase or sale.

Optional schema-2 `details.bargain` contains `available`, `phase` (`unavailable`, `closed`,
`choosing`, `responding`), NPC `direction` (`buy`, `sell`, `unknown`), `terms_revision`,
`options`, `reputation_text`, `hint_text`, `last_option_ref`, `result`, and `result_text`.
Null/omission remains readable in old schema-2 snapshots; schema 1 does not accept the extension.
Each visible option contains `option_ref`, rendered `text`, semantic `kind`, signed
`price_change_percent`, `enabled`, and `selected`. Kinds are `lower_purchase_price`,
`raise_purchase_price`, `raise_sale_price`, and `lower_sale_price` from the player's perspective.
These are price changes, not success probabilities. Closed/unavailable panels expose no options.
Text hints are null when not actually rendered; observation does not calculate hover tooltips,
read RNG or hidden customer budgets, or simulate bargaining.

An acknowledged choice increments `terms_revision` and invalidates the current `offer_ref`,
even if the displayed amount remains unchanged. Option references expire when the visible
panel context, direction, terms, customer or world changes. `selected` acknowledges a submitted
offer only. `result=responding` awaits visible response; `price_changed` and `unchanged` compare
the same observed counter collection's actual normal total, so unchanged is not proof of refusal.
`customer_left` records departure during the response. `unknown` is not permission to retry.
Subsequent purchase/sale continues to use ordinary payment `mode=normal` with the new effective
displayed amount and offer reference, including accepted native bargain adjustments.

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
- open_trade_panel: customer_ref. Requires the current arrived ordinary BUY/SELL customer,
  a closed panel, completed necessary dialogue, and available native interaction. Existing
  global UI coverage placeholders do not block this local action. The native adapter calls
  the open-only branch of the normal customer interaction; an already-open panel is not legal.
  Success requires the same customer, correct built panel direction, trading phase and readable
  normal displayed total, unchanged cash and a newer revision. Invocation alone is not success.
- open_bargain_panel: customer_ref, mode=buy/sell (NPC direction). Requires a current arrived
  supported ordinary customer, visible enabled native bargaining entry, closed bargain panel,
  and completed necessary dialogue. Success observes the same customer's choosing panel/options.
- choose_bargain_option: customer_ref, mode=buy/sell, option_ref. Requires an enabled unselected
  visible ordinary option plus a complete current normal quote and nonempty bound offer.
  Executes its existing native button callback once; success acknowledges the selected marker,
  matching last_option_ref and increased terms_revision, not acceptance or settlement.
- close_bargain_panel: customer_ref, mode=buy/sell. Returns normally from choosing/responding.
  An acknowledged departure response may retain its original direction after the same customer
  changes intent to leave. Closing does not undo bargain terms; manual dialogue may then advance.
  All three actions preserve cash/world/customer and verify a newer revision. The docked bargain
  panel permits quote observation but blocks purchase/sale until closed; other known modals still block.
- prepare_sale_items: customer_ref, exactly one player-owned item ref, collection_fingerprint. Requires
  an arrived BUY customer in trading, complete player and back containers, and an empty back container.
  The fingerprint is the player `BusinessCollection` fingerprint for the selected item with
  container=player, trade_type=buy, quoteMode=unknown, quoteStatus=unverified, and total=null.
  The broker binds the command to the exact current legal action; the native runtime recomputes and
  checks this player collection fingerprint immediately before invoking the transfer.
- sell_visible_offer: customer_ref, offer_ref, item_refs, collection_fingerprint, mode (`normal`), amount.
  It binds the normal visible quote to the current BUY trade subset. It does not depend on
  `purchase_enabled`, which remains purchase-specific. Known global UI coverage placeholders do not
  block this action when the visible offer and local action state are otherwise valid.
- sort_inventory_default: container (`player`). Requires a complete player collection, no active
  dialogue or typing, and no concrete UI action blocker. Unobservable global UI coverage markers
  are allowed while status is unverified / coverage is known_roots_only. It has no mode argument.

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

The schema-2 `legal_actions` list is bounded to 64 entries. `prepare_purchase_items` and
`prepare_sale_items` may each appear more than once when different item choices are independently
legal; exact duplicate action/argument entries are invalid. Successful sale evidence requires cash to
increase by `amount`, all selected owned-back units to leave back, and every observed non-sale item to
remain with its prior identity, ownership, container, and units. Sale preparation requires only the
selected player item to move to back, with cash, units, ownership, and all other observed inventory
identities unchanged. Sorting requires equal cash and the same item refs, units, ownership, and
containers, regardless of order.

`purchase_visible_offer` arguments: customer_ref, offer_ref, item_refs, collection_fingerprint,
mode (`normal`), amount, max_spend. The amount must equal the current normal total, and `amount <= max_spend <= cash`.
No separate approval token is required; exact current snapshot legality and the native purchase
preflight/verifier remain mandatory.

v2 validators, schema and fixture remain unchanged. Strict v3 schemas and tests reject unknown keys,
cross-version results, invalid collection references, and legal arguments that do not exactly match
the current snapshot. Business schema 1 retains its original stricter quote semantics; schema 2 scopes
quote validity to the visible normal total and current trade subset. No generic unknown
reconciliation is added.

Manual state alignment retains the existing operator-only endpoint and all expiry, fresh-Mod-session,
ready-snapshot and advanced-version checks. Alongside `close_menu`, only wire3
`open_bargain_panel` and `close_bargain_panel` may be aligned after a reviewed restart. The original
unknown result remains unknown with separate persisted resolution evidence. Bargain choices and
economic actions remain ineligible; no command is replayed.

## Manual inspection handoff (M3-C)

Optional schema-2 `details.inspection_handoff` is absent/null before any handoff. When present:

- `requires_manual_inspection`: boolean; true exactly when `status=waiting_for_user`, otherwise `resumed`.
- `handoff_ref`, `customer_ref`: bounded short references for this handoff and its triggering visit.
- `items`: 1..64 distinct `{item_ref, name}` entries; name is current player-visible text or null.
- `reason`: `manual_item_inspection` or the explicitly enabled test-build `test_inspection_handoff`.
- `last_resumed_handoff_ref`: null before any confirmation, otherwise the last confirmed reference.

The customer/item references describe the triggering batch and can outlive its current visibility,
ownership or departure. They are not current purchase capabilities. No identifier, authenticity,
water quality, hidden feature or true-value field is exported. Exact confirmed identifiers are
classified inside production `BusinessProjection`, from `BusinessReader`'s existing visible instances.

Waiting survives unavailable UI, manual gameplay and world changes. Current command world/version
validation still applies. While waiting, all business writes (including `close_menu`) are blocked;
only `resume_inspection_handoff` with exactly `{handoff_ref}` may be legal when ready. Read-only tools
remain available. A command already inside native execution continues its normal result verification.
This state does not reuse or clear broker `manual_action_required` / unknown-command recovery.

Resume only acknowledges the handoff in Mod memory and does not invoke a game transaction. Codex may
submit it only after explicit user confirmation in the current conversation. Success evidence is the
before pending reference matching the argument, same world, advanced revision, and the after
`last_resumed_handoff_ref` matching it. The after state may already contain a new waiting handoff;
that requires a new human confirmation. Player input remains active, so unrelated manual changes
between snapshots are not grounds to reinterpret a correctly acknowledged resume as a transaction.

Clients must observe again after resume and use fresh actual terms. Confirmed batches are suppressed
by world, visit and real object identity; names never identify a batch. Historical v2 and business
schema-1 records keep their original shapes and do not acquire this extension.

## Single-day close and settlement (M4)

Schema-2 `details.day` is an optional nullable object so earlier business-schema-2 journal records
remain valid. When present it contains `day_number` and `remaining_customers` (nullable nonnegative
integers), `stage` (`unavailable`, `morning`, `open`, `closed`, `settlement`, or `next_morning`),
nullable `reason`, the `can_close`, `can_sleep`, `can_confirm`, `report_open`, and
`transition_pending` flags, plus nullable `settlement`. A settlement has a bounded `settlement_ref`,
signed 32-bit `total`, and at most 32 services; each service carries visible `label`, `cost_text`,
signed `cost`, and `selected`. This describes current visible day state only; it does not imply
approval of optional services.

The registered day actions are `close_business {}`, `open_end_of_day {}`,
`confirm_end_of_day {settlement_ref, amount, max_spend}`, and `close_day_report {}`. Close requires
phase/stage `OPEN`/`open`, zero remaining customers, absent current customer, and `can_close`.
Opening end-of-day requires phase/stage `CLOSED`/`closed` and `can_sleep`. Confirmation requires
`CLOSED`/`settlement`, `can_confirm`, and the currently displayed settlement reference and total.
Report close requires `MORNING`/`next_morning` with `report_open`. All four require a ready snapshot,
an observed day number, no open menu or pending transition, and pass the existing inspection-handoff
gate. While `transition_pending`, `report_open`, or `stage=settlement` holds, other business writes
are blocked; only the day action valid for the current stage (or a pending inspection resume) can be
submitted. In particular the next-day report must be closed before `start_business` becomes legal.

The legal confirmation template carries `max_spend=max(0, total)`. A caller may submit a higher
ceiling only as explicit authorization in its `confirm_end_of_day` arguments; it must still be at
least `max(0, amount)`, and a positive amount requires enough current cash to cover that ceiling.
Negative totals are allowed and are checked against the observed signed total. The broker permits
only this ceiling adjustment while keeping settlement reference and amount bound to the current
legal action. It does not alter the snapshot's canonical legal template.

Successful close/open/report actions preserve day number and cash and must reach their corresponding
observable stage. Closing the day report clears `report_open`; the game may retain `stage=next_morning`
as the completed-settlement state. Successful confirmation must remain in the same world, advance the day number by
one, enter `MORNING`/`next_morning`, clear `transition_pending` and settlement, open the report, and
change cash by exactly the confirmed amount. Old schema-2 history without `day` and all schema-1
business records retain their original shape and action space.

## Transport size budgets

MCP tool replies retain their independent outer `schema_version: 2` envelope format.
`health_check` and `observe` additionally expose `action_schema_version: 2|3|null`:
it copies the connected `snapshot.state.schema_version`, or is null without a connected
game state. Clients copy the latest **observe.action_schema_version** into **act.schema_version**,
alongside identity/version and legal arguments from that same observation. The outer reply format
and `business.details.schema_version` are not action wire versions. This additive hint does not
grant permission, change v2/v3 validation, infer/upgrade a submitted version, or replay a command.

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
