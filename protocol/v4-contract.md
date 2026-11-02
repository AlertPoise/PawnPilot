# PawnPilot wire v4

Wire v4 is additive. Wire v2 remains the close-menu-only contract, and wire v3 remains the frozen business-observation and registered-action contract. A v4 snapshot keeps the business-details schema at version 2 and adds a required top-level `surface`. A v2 or v3 snapshot cannot contain `surface`.

## Surface observation

`surface` has schema version 1 and always serializes all fields, including nullable references and text. It carries a bounded kind, phase, status, completeness, reason, optional visible text and references, and up to 16 controls. Each control has an opaque reference, one of `advance_dialogue`, `choose_dialogue_option`, or `dismiss_customer`, nullable mode and option reference, visible text, native enabled state, an audited effect contract, a finite list of declared effects, and a reason. Unknown contracts never authorize actions.

The legal-action list uses the same three action names as legacy dialogue actions. A generic variant adds both `surface_ref` and `control_ref`; its complete argument key set is action-specific:

- `advance_dialogue`: `customer_ref`, `dialogue_ref`, `mode`, `surface_ref`, `control_ref`.
- `choose_dialogue_option`: `customer_ref`, `dialogue_ref`, `option_ref`, `surface_ref`, `control_ref`.
- `dismiss_customer`: `customer_ref`, `surface_ref`, `control_ref`.

Both references, the customer and dialogue references, and the mode or option must bind to the same final snapshot and selected control. Generic candidates require a complete, actionable surface; the current arrived customer; an open, ready store; no menu, day transition, settlement/report, or pending inspection handoff; and no concrete blocking UI evidence. Only the existing named unobservable UI notes and `dialogue_active` are informational. Controls with an unknown effect contract are never legal. A generic candidate may not duplicate the same action and semantic arguments already exposed through the legacy route.

Wire v4 also accepts old v3 action argument shapes. Those actions are validated through the unchanged v3 binding, phase, and success checks. Absence of both surface references selects this compatibility route; a partial reference pair is invalid. This compatibility route does not authorize arbitrary input or make unknown economic actions available.

`surface.status` is a final publication status. Pending inspection handoff reports `awaiting_user`; otherwise, a nonempty legal-action list reports `awaiting_agent`, and `awaiting_agent` requires a nonempty list. Without legal actions, an absent surface reports `idle`; a proven typing/progressing phase reports `progressing`; unknown and unsupported phases retain their corresponding statuses; other waiting/completed surfaces without a supported action report `unsupported`.

Complete ready surfaces must agree with the business customer reference. A dialogue surface requires a present customer and the same non-null dialogue reference; customer_completion requires a present customer and null dialogue references. A ready kind=none cannot coexist with a present customer; phase=absent is restricted to kind=none. Partial/unavailable captures retain diagnostic evidence without pretending reference completeness. Contradictions are rejected rather than normalized.

Production v4 retains item references across customer changes when the world, actual item instance and observed properties are unchanged. Customer, dialogue and collection bindings still expire. Historical projection behavior remains unchanged.

## Interaction result

Only a generic v4 result may include optional `interaction_result`. A successful generic result requires the receipt; a rejected pre-entry action may omit it or carry `input_receipt=not_invoked`; an invoked but unresolved action carries unknown verification and an unknown or returned input receipt. V2/v3 results never accept this field.

The receipt contains input receipt (`not_invoked`, `returned`, `unknown`), verification (`confirmed`, `unknown`), the effect contract, observed and unverified finite effect lists, nullable signed Int32 cash delta, and bounded added/removed item references. Observed effects must be declared by the selected control. Unverified effects equal declared effects minus observed effects. The receipt describes only the finite observed scope and makes no claim about later story or inventory changes.

Generic success requires a returned input receipt and confirmed verification, the expected before identity, a ready after snapshot in the same world and scene with an advanced revision, unchanged cash, and complete front/back/player inventory evidence. Existing item location, ownership, and units cannot change. Presentation requires dialogue progress and no inventory change. A document gift permits zero or one new owned front item, requires dialogue progress, reports `inventory_transfer` as observed only when that item is present, and reports `story_state` as unverified. Customer departure requires the previous customer to be absent or replaced; it does not require the next customer to be absent. Cash or unexpected inventory changes make the result uncertain. A revision increase by itself is never generic success evidence.

Unknown receipts retain actual cash and item-reference deltas when comparable same-world evidence is available. Incomparable world/scene observations are not attached as post-action evidence. A delta is an observed fact, not authorization for the change or a successful contract. Hidden story/reputation effects cannot be claimed as observed from a callback return.

## Validators and persistence

`bridge.protocol` keeps v2/v3 validators distinct and exposes v4 validation separately. Generic validation checks surface and control binding as well as the action-specific postcondition; legacy v4 commands are projected to a temporary v3 view only for the frozen legacy policy. Broker sessions stay pinned to one exact wire version. The journal stores v4 command/result evidence in the existing journal format, with v4-specific validators, without rewriting old records or fingerprints.

The outer MCP tool reply schema remains 2. `observe.action_schema_version` and `act.schema_version` carry the wire version. V4 state, action receipts, and resolutions are typed separately from the frozen v2/v3 MCP models.
