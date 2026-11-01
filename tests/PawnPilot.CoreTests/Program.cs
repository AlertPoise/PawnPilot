using PawnPilot;
using System.Text.Json;

try
{
if (args.Length >= 2 && (args[0] == "--simulate-mod" || args[0] == "--simulate-mod-v3"))
{
    Environment.ExitCode = await ModSimulator.Run(args);
    return;
}
int count = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name); count++;
}
Snapshot State(string version = "world:1", bool ready = true, bool loaded = true, bool open = true,
    string epoch = "world", int revision = 1, int cash = 10) =>
    new Snapshot
    {
        schema_version = Protocol.SchemaVersion, version = version, world_epoch = epoch, revision = revision,
        loaded = loaded, ready = ready, scene = "InventoryScene", phase = "MORNING",
        not_ready_reason = ready ? null : "game_ui_settling", menu_open = open, can_close_menu = ready && open,
        cash = cash,
        interaction = new InteractionObservation { phase = ready ? "MORNING" : "loading", consistency = "unverified", reason = ready ? "business_consistency_unverified" : "game_ui_settling", operable = ready },
        business = new BusinessObservation { support = "unsupported" },
        legal_actions = ready && open ? new List<LegalAction> { new LegalAction { action = "close_menu", arguments = new ActionArguments() } } : new List<LegalAction>()
    };
Command Cmd(string id = "a", string action = "close_menu", Snapshot state = null, string brokerEpoch = "broker",
    long start = 100, int timeout = 5000, ActionArguments args = null, int schema = 2)
{
    state ??= State();
    return new Command
    {
        schema_version = schema, id = id, broker_epoch = brokerEpoch, action = action,
        expected_version = state.version, expected_world_epoch = state.world_epoch,
        expected_revision = state.revision, start_deadline_unix_ms = start,
        execution_timeout_ms = timeout, arguments = args ?? new ActionArguments()
    };
}
int calls = 0;
void Close(Command _) { calls++; }
ExecutionStep Tick(ActionGate gate, Snapshot snapshot, long unix, long monotonic, Action<Command> native) =>
    gate.Advance("broker", snapshot, unix, monotonic, native, ActionGate.VerifyCloseMenu);

var notScene = GameReadiness.Evaluate(new ReadinessSignals(), null);
Check("main_menu_not_loaded", !notScene.loaded && !notScene.ready && notScene.reason == "not_gameplay_scene");
var singletonOnly = GameReadiness.Evaluate(new ReadinessSignals { gameplay_scene = true, store_present = true }, null);
Check("singleton_not_initialized", !singletonOnly.loaded && !singletonOnly.ready && singletonOnly.reason == "player_store_initializing");
var partialUi = GameReadiness.Evaluate(new ReadinessSignals { gameplay_scene = true, store_present = true, store_initialized = true }, "MORNING");
Check("loaded_but_ui_not_ready", partialUi.loaded && !partialUi.ready && partialUi.reason == "game_ui_initializing");
var settling = GameReadiness.Evaluate(new ReadinessSignals { gameplay_scene = true, store_present = true, store_initialized = true, ui_initialized = true, stable_ready_frames = 1 }, "MORNING");
Check("one_frame_is_not_ready", settling.loaded && !settling.ready && settling.reason == "game_ui_settling");
var ready = GameReadiness.Evaluate(new ReadinessSignals { gameplay_scene = true, store_present = true, store_initialized = true, ui_initialized = true, stable_ready_frames = 2 }, "MORNING");
Check("two_stable_frames_ready", ready.loaded && ready.ready && ready.phase == "MORNING");

var epochs = new WorldVersion();
var firstEpoch = epochs.Epoch;
Check("version_includes_epoch_and_revision", epochs.Observe("state") == firstEpoch + ":1");
Check("same_snapshot_keeps_revision", epochs.Observe("state") == firstEpoch + ":1");
Check("changed_snapshot_increments_revision", epochs.Observe("other") == firstEpoch + ":2");
epochs.Rotate();
Check("world_rotation_invalidates_old_version", epochs.Epoch != firstEpoch && epochs.Observe("state") == epochs.Epoch + ":1");

var identityTracker = new WorldIdentityTracker();
var identityEpoch = epochs.Epoch;
Check("identity_appearance_rotates_epoch", identityTracker.Ensure("slot-1:run-a", epochs) && epochs.Epoch != identityEpoch);
identityEpoch = epochs.Epoch;
Check("same_identity_keeps_epoch", !identityTracker.Ensure("slot-1:run-a", epochs) && epochs.Epoch == identityEpoch);
Check("identity_disappearance_rotates_once", identityTracker.Ensure(null, epochs) && !identityTracker.Ensure(null, epochs));
identityEpoch = epochs.Epoch;
identityTracker.OnGameLoaded("slot-1:run-a", epochs);
Check("same_save_reload_rotates_epoch", epochs.Epoch != identityEpoch);
identityEpoch = epochs.Epoch;
identityTracker.OnSceneChanged(epochs);
Check("scene_change_rotates_epoch", epochs.Epoch != identityEpoch && identityTracker.Ensure(null, epochs) == false);

var readyFrames = new ReadyFrameTracker();
readyFrames.Observe(true, 10, advance:true);
readyFrames.Observe(true, 10, advance:true);
Check("same_frame_does_not_count_twice", readyFrames.Count == 1);
readyFrames.Observe(true, 11, advance:true);
Check("two_different_frames_become_ready", readyFrames.Count == 2);
readyFrames.Observe(false, 12, advance:false);
Check("capture_negative_invalidates_readiness", readyFrames.Count == 0);
readyFrames.Observe(true, 12, advance:false);
readyFrames.Observe(true, 12, advance:true);
Check("capture_positive_does_not_count_frame", readyFrames.Count == 0);
readyFrames.Observe(true, 13, advance:true);
Check("first_frame_after_invalidation_not_ready", readyFrames.Count == 1);
readyFrames.Observe(true, 14, advance:true);
Check("readiness_restored_after_two_frames", readyFrames.Count == 2);

var state = State();
var admissionCopyGate = new ActionGate();
var rejectedCopy = admissionCopyGate.Accept(Cmd("reject-copy", state:State(ready:false)), State(ready:false), "broker", 1);
rejectedCopy.reason = "mutated";
rejectedCopy.before.version = "mutated-version";
rejectedCopy.execution.stage = "mutated-stage";
Check("admission_rejection_surface_is_detached", admissionCopyGate.PendingResult.reason == "game_ui_settling" &&
    admissionCopyGate.PendingResult.before.version == "world:1" && admissionCopyGate.PendingResult.execution.stage == "completed");
Check("not_ready_write_rejected", new ActionGate().Accept(Cmd("unready", state:State(ready:false)), State(ready:false), "broker", 1).reason == "game_ui_settling");
Check("not_loaded_write_rejected", new ActionGate().Accept(Cmd("unloaded", state:State(loaded:false,ready:false)), State(loaded:false,ready:false), "broker", 1).reason == "not_loaded");
Check("old_world_rejected", new ActionGate().Accept(Cmd("old-world", state:State(epoch:"old", version:"old:1")), state, "broker", 1).reason == "stale_state");
Check("old_revision_rejected", new ActionGate().Accept(Cmd("old-revision", state:State(revision:0)), state, "broker", 1).reason == "stale_state");
Check("wrong_broker_epoch_rejected", new ActionGate().Accept(Cmd("wrong-broker", brokerEpoch:"old-broker"), state, "broker", 1).reason == "stale_broker_epoch");
Check("unsupported_command_schema_rejected", new ActionGate().Accept(Cmd("schema", schema:1), state, "broker", 1).reason == "schema_version_unsupported");
Check("start_deadline_before_delivery", new ActionGate().Accept(Cmd("expired", start:1), state, "broker", 2).reason == "start_deadline_elapsed");
Check("start_deadline_equal_now_is_rejected", new ActionGate().Accept(Cmd("equal-deadline", start:1), state, "broker", 1).reason == "start_deadline_elapsed");
Check("unknown_action", new ActionGate().Accept(Cmd("unknown", "open_menu"), state, "broker", 1).reason == "unknown_action");
Check("observe_is_not_a_queued_action", new ActionGate().Accept(Cmd("observe", "observe"), state, "broker", 1).reason == "unknown_action");
Check("menu_precondition", new ActionGate().Accept(Cmd("closed", state:State(open:false)), State(open:false), "broker", 1).reason == "precondition");
var hiddenBusiness = State();
hiddenBusiness.business.customer_ref = "hidden-customer";
Check("snapshot_rejects_unimplemented_business_data", !ActionGate.IsSnapshotValid(hiddenBusiness) &&
    new ActionGate().Accept(Cmd("hidden-business", state:hiddenBusiness), hiddenBusiness, "broker", 1).reason == "invalid_state");
var missingLegalAction = new Snapshot { schema_version=2, version="world:1", world_epoch="world", revision=1, ready=true, loaded=true, scene="InventoryScene", phase="MORNING", can_close_menu=true, menu_open=true, interaction=new InteractionObservation {phase="MORNING",consistency="unverified",operable=true}, business=new BusinessObservation(), legal_actions=new List<LegalAction>() };
Check("legal_action_must_match_snapshot", new ActionGate().Accept(Cmd("bad-legal", state:missingLegalAction), missingLegalAction, "broker", 1).reason == "invalid_state");

var gate = new ActionGate();
var valid = Cmd(state:state);
Check("accepted_command_is_only_validated", gate.Accept(valid, state, "broker", 1) == null && gate.CurrentProgress.stage == "validated" && calls == 0);
Check("duplicate_inflight_delivery_does_not_reinvoke", gate.Accept(valid, state, "broker", 1) == null && gate.CurrentProgress.stage == "validated" && calls == 0);
var invoking = Tick(gate, state, 2, 2, Close);
Check("invocation_boundary_precedes_native_call", invoking.progress.stage == "invoking" && invoking.progress.may_have_side_effects && calls == 1);
Check("heartbeat_progress_does_not_complete_action", Tick(gate, state, 3, 3, Close).progress.stage == "verifying" && calls == 1);
int verifyFrames = 0;
VerificationDecision DelayVerification(Command command, Snapshot before, Snapshot after)
{
    verifyFrames++;
    return verifyFrames < 3 ? VerificationDecision.Pending() : ActionGate.VerifyCloseMenu(command, before, after);
}
var needsAgent = State("world:2", open:false, revision:2);
needsAgent.interaction.phase = "needs_agent";
Check("multiframe_verifier_waits_independently_of_interaction_phase", gate.Advance("broker", needsAgent, 4, 4, Close, DelayVerification).progress.stage == "verifying");
Check("three_frame_verification_still_pending", gate.Advance("broker", needsAgent, 5, 5, Close, DelayVerification).progress.stage == "verifying");
var success = gate.Advance("broker", needsAgent, 6, 6, Close, DelayVerification).result;
Check("multiframe_postcondition_succeeds_while_interaction_needs_agent", success.status == "succeeded" && success.execution.stage == "completed" && success.execution.may_have_side_effects && success.after.cash == state.cash && success.after.interaction.phase == "needs_agent" && verifyFrames == 3 && calls == 1);
success.reason = "mutated_current_step_result";
Check("current_step_result_is_detached", gate.PendingResult.reason == "postcondition_observed");
var competing = gate.Accept(Cmd("second-before-ack", state:State("world:2", open:false, revision:2)), State("world:2", open:false, revision:2), "broker", 7);
Check("second_command_cannot_overwrite_pending_result", competing.reason == "busy" && gate.PendingResult.id == "a" && gate.IsWriteBlocked);
Check("result_snapshot_retained_before_ack", gate.RetainedResultCount == 1 && gate.PendingResult.before.revision == 1);
var exposedSuccess = gate.PendingResult;
exposedSuccess.reason = "caller_mutation";
exposedSuccess.before.version = "caller-world:99";
Check("pending_result_surface_is_detached", gate.PendingResult.reason == "postcondition_observed" && gate.PendingResult.before.version == "world:1");
Check("write_lease_held_until_ack", gate.Acknowledge("a") && !gate.IsWriteBlocked && success.before.revision == 1 && gate.RetainedResultCount == 0);
var replay = gate.Accept(valid, State("world:2", open:false, revision:2), "broker", 8);
Check("duplicate_after_ack_is_local_rejection_without_replay", replay.reason == "command_already_acknowledged" && replay.status == "rejected" && gate.PendingResult == null && calls == 1);

var mutationGate = new ActionGate();
var mutationState = State();
var mutationCommand = Cmd("immutable-callbacks", state:mutationState);
mutationGate.Accept(mutationCommand, mutationState, "broker", 1);
var mutationStart = Tick(mutationGate, mutationState, 2, 2, command =>
{
    command.id = "mutated-id";
    command.execution_timeout_ms = 1;
    command.arguments.option_ref = "test-mutation";
});
bool verifierInputsIsolated = false;
var mutationVerify = mutationGate.Advance("broker", mutationState, 3, 3, _ => throw new Exception(),
    (command, before, after) =>
    {
        verifierInputsIsolated = command.id == "immutable-callbacks" && command.execution_timeout_ms == 5000 && before.version == mutationState.version;
        command.id = "verifier-mutated-id";
        before.version = "verifier-mutated-version";
        before.cash = -10;
        after.version = "verifier-mutated-after";
        return VerificationDecision.Pending();
    });
var mutationComplete = Tick(mutationGate, State("world:2", open:false, revision:2), 4, 4, _ => throw new Exception()).result;
Check("native_and_verifier_callbacks_receive_detached_evidence", mutationStart.progress.id == "immutable-callbacks" &&
    mutationVerify.progress.id == "immutable-callbacks" && verifierInputsIsolated && mutationComplete.status == "succeeded" &&
    mutationComplete.before.version == "world:1" && mutationComplete.before.cash == state.cash);

var conflictGate = new ActionGate();
var initial = Cmd("same-id", state:state);
conflictGate.Accept(initial, state, "broker", 1);
var differentArgs = Cmd("same-id", state:state, args:new ActionArguments { customer_ref="customer-1" });
Check("duplicate_args_conflict_even_if_action_args_unsupported", conflictGate.Accept(differentArgs, state, "broker", 1).reason == "command_id_conflict");
var timingConflict = Cmd("same-id", state:state, start:101);
Check("duplicate_timing_envelope_conflict", conflictGate.Accept(timingConflict, state, "broker", 1).reason == "command_id_conflict");
Check("malformed_item_refs_rejected", new ActionGate().Accept(Cmd("bad-items", args:new ActionArguments { item_refs=new List<string>{"same","same"} }), state, "broker", 1).reason == "invalid_arguments");
Check("request_fingerprint_is_stable", ActionGate.Fingerprint(initial) == ActionGate.Fingerprint(Cmd("same-id", state:state)));
Check("request_fingerprint_preserves_list_order", ActionGate.Fingerprint(Cmd("order", args:new ActionArguments { item_refs=new List<string>{"item-a","item-b"} })) != ActionGate.Fingerprint(Cmd("order", args:new ActionArguments { item_refs=new List<string>{"item-b","item-a"} })));

var expiring = new ActionGate();
Check("start_deadline_future_can_be_accepted", expiring.Accept(Cmd("late-start", start:6), state, "broker", 5) == null);
var lateStart = Tick(expiring, state, 6, 6, Close).result;
Check("start_expiry_proves_no_invocation", lateStart.status == "rejected" && lateStart.execution.stage == "completed" && !lateStart.execution.may_have_side_effects && !lateStart.execution.started_at_unix_ms.HasValue && calls == 1);
Check("start_expiry_ack_releases_gate", expiring.Acknowledge("late-start") && !expiring.IsWriteBlocked);

var preInvokeCancel = new ActionGate();
preInvokeCancel.Accept(Cmd("cancel-before", state:state), state, "broker", 1);
int callsBeforeCancel = calls;
var cancelledBefore = preInvokeCancel.Cancel("cancelled_before_invoke", state);
var noLateStart = Tick(preInvokeCancel, State("world:2", open:false, revision:2), 3, 3, Close);
Check("preinvoke_cancel_rejects_without_late_invocation", cancelledBefore.status == "rejected" && !cancelledBefore.execution.may_have_side_effects &&
    noLateStart.result == null && calls == callsBeforeCancel && preInvokeCancel.IsWriteBlocked);
Check("preinvoke_cancel_ack_releases_gate", preInvokeCancel.Acknowledge("cancel-before") && !preInvokeCancel.IsWriteBlocked);

var postInvokeCancel = new ActionGate();
postInvokeCancel.Accept(Cmd("cancel-after", state:state), state, "broker", 1);
Tick(postInvokeCancel, state, 2, 2, Close);
var cancelledAfter = postInvokeCancel.Cancel("cancelled_after_invoke", state);
var noLateCompletion = Tick(postInvokeCancel, State("world:2", open:false, revision:2), 3, 3, Close);
Check("postinvoke_cancel_is_unknown_and_cannot_late_complete", cancelledAfter.status == "outcome_unknown" && cancelledAfter.execution.stage == "uncertain" &&
    noLateCompletion.result == null && postInvokeCancel.IsWriteBlocked);
Check("postinvoke_cancel_ack_keeps_gate_blocked", postInvokeCancel.Acknowledge("cancel-after") && postInvokeCancel.IsWriteBlocked);

var throwsGate = new ActionGate();
throwsGate.Accept(Cmd("throws", state:state), state, "broker", 1);
var thrown = Tick(throwsGate, state, 2, 2, _ => throw new Exception()).result;
Check("invoked_exception_is_unknown", thrown.status == "outcome_unknown" && thrown.execution.stage == "uncertain" && thrown.execution.may_have_side_effects);
Check("unknown_ack_does_not_release_gate", throwsGate.Acknowledge("throws") && throwsGate.IsWriteBlocked);
var postAckUnknownStep = throwsGate.Advance("broker", state, 5, 5, Close, ActionGate.VerifyCloseMenu);
Check("unknown_ack_does_not_resend_result_or_terminal_progress", throwsGate.PendingResult == null && throwsGate.CurrentProgress == null &&
    postAckUnknownStep.progress == null && throwsGate.IsWriteBlocked);
var unknownReplay = throwsGate.Accept(Cmd("throws", state:state), state, "broker", 4);
Check("unknown_tombstone_prevents_replay", unknownReplay.status == "rejected" && unknownReplay.reason == "command_already_acknowledged" && throwsGate.IsWriteBlocked);

var verifierThrowsGate = new ActionGate();
verifierThrowsGate.Accept(Cmd("verifier-throws", state:state), state, "broker", 1);
Tick(verifierThrowsGate, state, 2, 2, Close);
var verifierException = verifierThrowsGate.Advance("broker", state, 3, 3, Close,
    (_, _, _) => throw new InvalidOperationException()).result;
Check("verifier_exception_becomes_unknown_without_retry", verifierException.status == "outcome_unknown" && verifierException.reason == "verification_exception" && verifierThrowsGate.IsWriteBlocked);

var driverBeforeGate = new ActionGate();
driverBeforeGate.Accept(Cmd("driver-capture-before", state:state), state, "broker", 1);
var driverBefore = new ActionDriver(driverBeforeGate);
int driverBeforeCalls = 0;
var captureBeforeFailure = driverBefore.Tick(() => { }, () => throw new InvalidOperationException("capture"),
    "broker", 2, 2, _ => driverBeforeCalls++, ActionGate.VerifyCloseMenu);
var afterCaptureBeforeFailure = driverBefore.Tick(() => { }, () => State("world:2", open:false, revision:2),
    "broker", 3, 3, _ => driverBeforeCalls++, ActionGate.VerifyCloseMenu);
Check("driver_capture_failure_before_invoke_rejects_and_halts", captureBeforeFailure.newlyFailed &&
    driverBeforeGate.PendingResult.status == "rejected" && !driverBeforeGate.PendingResult.execution.may_have_side_effects &&
    afterCaptureBeforeFailure.failed && !afterCaptureBeforeFailure.newlyFailed && driverBeforeCalls == 0);

var driverAfterGate = new ActionGate();
driverAfterGate.Accept(Cmd("driver-capture-after", state:state), state, "broker", 1);
var driverAfter = new ActionDriver(driverAfterGate);
int driverAfterCalls = 0;
var driverInvoked = driverAfter.Tick(() => { }, () => state, "broker", 2, 2,
    _ => driverAfterCalls++, ActionGate.VerifyCloseMenu);
var captureAfterFailure = driverAfter.Tick(() => { }, () => throw new InvalidOperationException("capture"),
    "broker", 3, 3, _ => driverAfterCalls++, ActionGate.VerifyCloseMenu);
var afterCaptureAfterFailure = driverAfter.Tick(() => { }, () => State("world:2", open:false, revision:2),
    "broker", 4, 4, _ => driverAfterCalls++, ActionGate.VerifyCloseMenu);
Check("driver_capture_failure_after_invoke_is_unknown_and_halts", driverInvoked.progress.stage == "invoking" &&
    captureAfterFailure.newlyFailed && driverAfterGate.PendingResult.status == "outcome_unknown" &&
    driverAfterGate.PendingResult.execution.stage == "uncertain" && afterCaptureAfterFailure.failed &&
    !afterCaptureAfterFailure.newlyFailed && driverAfterCalls == 1);

var refreshFailureGate = new ActionGate();
refreshFailureGate.Accept(Cmd("refresh-failure", state:state), state, "broker", 1);
int refreshNativeCalls = 0;
var refreshFailure = new ActionDriver(refreshFailureGate).Tick(() => throw new InvalidOperationException("refresh"),
    () => state, "broker", 2, 2, _ => refreshNativeCalls++, ActionGate.VerifyCloseMenu);
Check("driver_refresh_failure_is_fail_closed", refreshFailure.newlyFailed && refreshFailureGate.PendingResult.status == "rejected" && refreshNativeCalls == 0);

var timeoutGate = new ActionGate();
timeoutGate.Accept(Cmd("timeout", state:state, timeout:5), state, "broker", 1);
Tick(timeoutGate, state, 2, 10, Close);
var timeout = Tick(timeoutGate, state, 3, 15, Close).result;
Check("monotonic_execution_timeout_becomes_unknown", timeout.status == "outcome_unknown" && timeout.reason == "execution_timeout" && calls == 4);

var epochGate = new ActionGate();
epochGate.Accept(Cmd("epoch-before", state:state), state, "broker", 1);
var epochRejected = epochGate.OnBrokerEpochChanged("new-broker", state);
epochRejected.reason = "mutated-epoch-result";
Check("epoch_change_before_invoke_rejects", epochRejected.status == "rejected" && epochGate.PendingResult.reason == "stale_broker_epoch" && calls == 4);
var epochAfterGate = new ActionGate();
epochAfterGate.Accept(Cmd("epoch-after", state:state), state, "broker", 1);
Tick(epochAfterGate, state, 2, 2, Close);
Check("epoch_change_after_invoke_is_unknown", epochAfterGate.OnBrokerEpochChanged("new-broker", state).status == "outcome_unknown" && epochAfterGate.IsWriteBlocked && calls == 5);

var sceneGate = new ActionGate();
sceneGate.Accept(Cmd("scene-exit", state:state), state, "broker", 1);
Tick(sceneGate, state, 2, 2, Close);
var mainMenu = State(loaded:false,ready:false,open:false,epoch:"world",revision:2);
mainMenu.scene = "MainMenu";
Check("scene_exit_after_invoke_is_unknown", Tick(sceneGate, mainMenu, 3, 3, Close).result.status == "outcome_unknown");
var newWorldGate = new ActionGate();
newWorldGate.Accept(Cmd("new-world", state:state), state, "broker", 1);
Tick(newWorldGate, state, 2, 2, Close);
Check("world_change_after_invoke_is_unknown", Tick(newWorldGate, State("other-world:2", open:false, epoch:"other-world", revision:2), 3, 3, Close).result.status == "outcome_unknown");
var cashGate = new ActionGate();
cashGate.Accept(Cmd("cash-change", state:state), state, "broker", 1);
Tick(cashGate, state, 2, 2, Close);
Tick(cashGate, state, 3, 3, Close);
Check("cash_change_does_not_verify_close", Tick(cashGate, State("world:2",open:false,revision:2,cash:11), 4, 4, Close).result.status == "outcome_unknown");

// Same dispatcher with injected native and verifier state: a second delivery while active is busy.
var reentrantGate = new ActionGate();
reentrantGate.Accept(Cmd("outer", state:state), state, "broker", 1);
ActionResult nested = null;
int callsBeforeReentrant = calls;
Tick(reentrantGate, state, 2, 2, _ => nested = reentrantGate.Accept(Cmd("inner", state:state), state, "broker", 2));
Check("reentrant_write_rejected", nested != null && nested.reason == "busy" && calls == callsBeforeReentrant);

Check("null_command_safe", new ActionGate().Accept(null, State(), "broker", 1).reason == "invalid_command");

var fixturePath = Path.Combine(AppContext.BaseDirectory, "v2-close-menu.json");
using (var fixtureDoc = JsonDocument.Parse(File.ReadAllText(fixturePath)))
{
    var fixture = fixtureDoc.RootElement;
    var options = new JsonSerializerOptions { IncludeFields = true };
    var fixtureBefore = JsonSerializer.Deserialize<Snapshot>(fixture.GetProperty("before").GetRawText(), options);
    var fixtureAfter = JsonSerializer.Deserialize<Snapshot>(fixture.GetProperty("after").GetRawText(), options);
    var fixtureCommand = JsonSerializer.Deserialize<Command>(fixture.GetProperty("command").GetRawText(), options);
    Check("fixture_source_declares_fixture", fixture.GetProperty("source").GetString() == "fixture");
    Check("fixture_snapshot_roundtrip_fields", fixtureBefore.schema_version == 2 && fixtureBefore.world_epoch == "fixture-world" &&
        fixtureBefore.version == "fixture-world:1" && fixtureBefore.revision == 1 && fixtureBefore.business.support == "unsupported" &&
        fixtureBefore.business.item_refs == null && fixtureBefore.legal_actions.Count == 1 && fixtureAfter.legal_actions.Count == 0);
    Check("fixture_close_menu_runs_same_dispatcher", new ActionGate().Accept(fixtureCommand, fixtureBefore, "fixture-broker", 100000) == null);
    var fingerprintCases = fixture.GetProperty("fingerprint_cases");
    bool fingerprintsMatch = true, canonicalMatch = true;
    foreach (var item in fingerprintCases.EnumerateArray())
    {
        var requestCommand = JsonSerializer.Deserialize<Command>(item.GetProperty("request").GetRawText(), options);
        fingerprintsMatch &= ActionGate.Fingerprint(requestCommand) == item.GetProperty("sha256").GetString();
        canonicalMatch &= ActionGate.CanonicalRequestJson(requestCommand) == item.GetProperty("canonical_json").GetString();
    }
    Check("fixture_canonical_json_matches_python", canonicalMatch);
    Check("fixture_sha256_matches_python", fingerprintsMatch);
    var fullArgs = JsonSerializer.Deserialize<Command>(fingerprintCases[1].GetProperty("request").GetRawText(), options);
    fullArgs.start_deadline_unix_ms = 105000;
    fullArgs.execution_timeout_ms = 5000;
    Check("fixture_parameterized_request_fingerprints_without_enabling_action", ActionGate.Fingerprint(fullArgs) == fingerprintCases[1].GetProperty("sha256").GetString() &&
        new ActionGate().Accept(fullArgs, fixtureBefore, "fixture-broker", 100000).reason == "invalid_arguments");
}
TradeDiagnosticTests.Run(Check);
VerificationDiagnosticTests.Run(Check);
BusinessWireTests.Run(Check);
BusinessUiTests.Run(Check);
BusinessGateTests.Run(Check);
BusinessObservationTests.Run(Check);
BusinessActionTests.Run(Check);
PurchaseVerifierTests.Run(Check);
CapacityProofTests.Run(Check);
M3ObservationTests.Run(Check);
M3ActionPolicyTests.Run(Check);
BargainTests.Run(Check);
TradePanelTests.Run(Check);
SaleVerifierTests.Run(Check);
InventoryVerifierTests.Run(Check);
Console.WriteLine($"TOTAL {count} passed, 0 failed (managed lifecycle, wire v2/v3, business and verifier fixtures; no Unity runtime)");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
