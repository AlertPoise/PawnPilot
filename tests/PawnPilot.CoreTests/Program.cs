using PawnPilot;
using System.Text.Json;

try
{
if (args.Length == 1 && args[0] == "--export-candidate-fixtures")
{
    Console.WriteLine(CandidateAdmissionTests.Export());
    return;
}
if (args.Length == 1 && args[0] == "--export-inspection-fixtures")
{
    Console.WriteLine(InspectionHandoffTests.Export());
    return;
}
if (args.Length == 1 && args[0] == "--export-interaction-fixtures")
{
    Console.WriteLine(InteractionTests.Export());
    return;
}
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
CandidateAdmissionTests.Run(Check);
InteractionTests.Run(Check);
VersionedSerializationTests.Run(Check);
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
InspectionHandoffTests.Run(Check);
DayActionPolicyTests.Run(Check);
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

public static class DayActionPolicyTests
{
    public static void Run(Action<string, bool> check)
    {
        var open = State("OPEN", "open"); open.business.details.day.can_close = true;
        var close = Command("close_business", open);
        check("day_optional_schema2_extension_valid", BusinessWire.Validate(open.business) && ActionGate.IsSnapshotValid(open));
        check("day_close_registered_enabled_empty_arguments", BusinessActionPolicy.IsRegistered(close.action) &&
            BusinessActionPolicy.IsEnabled(close.action) && BusinessActionPolicy.ArgumentsValid(close.action, close.arguments));
        check("day_close_requires_native_closable_zero_remaining_absent_customer", DayActionPolicy.Validate(close, open) == null);
        open.business.details.day.remaining_customers = 1;
        check("day_close_cannot_equate_no_receive_action_with_finished_queue", DayActionPolicy.Validate(close, open) != null);
        open.business.details.day.remaining_customers = 0;
        open.business.details.customer.presence = "present";
        check("day_close_current_customer_blocks", DayActionPolicy.Validate(close, open) != null);
        open.business.details.customer.presence = "absent";
        open.business.details.day.transition_pending = true;
        check("day_close_animation_blocks", DayActionPolicy.Validate(close, open) == "day_transition_pending");
        open.business.details.day.transition_pending = false;
        open.business.details.day.can_close = false;
        check("day_close_explicit_native_permission_required", DayActionPolicy.Validate(close, open) != null);
        open.business.details.day.can_close = true;
        var closed = State("CLOSED", "closed", revision: 2); closed.business.details.day.can_sleep = true;
        check("day_close_verifies_stage_without_advancing_date_or_cash", DayActionPolicy.Postcondition(close, open, closed));
        var sleep = Command("open_end_of_day", closed);
        check("day_sleep_only_closed_permitted_state", DayActionPolicy.Validate(sleep, closed) == null && DayActionPolicy.Validate(sleep, open) != null);
        var settlement = State("CLOSED", "settlement", revision: 3); SetSettlement(settlement, 7);
        check("day_sleep_verifies_visible_settlement", DayActionPolicy.Postcondition(sleep, closed, settlement));
        settlement.business.details.day.can_confirm = false;
        check("day_sleep_can_open_disabled_confirmation", DayActionPolicy.Postcondition(sleep, closed, settlement));
        settlement.business.details.day.can_confirm = true;
        var confirm = Command("confirm_end_of_day", settlement);
        check("day_confirm_fresh_visible_total_admitted", DayActionPolicy.Validate(confirm, settlement) == null && ActionGate.IsSnapshotValid(settlement));
        confirm.arguments.max_spend = 30;
        check("day_confirm_accepts_caller_ceiling_above_default_legal_amount", BusinessActionPolicy.Validate(confirm, settlement) == null &&
            new ActionGate().Accept(confirm, settlement, "broker", 1) == null);
        confirm.arguments.max_spend = 101;
        check("day_confirm_positive_cost_ceiling_cannot_exceed_cash", DayActionPolicy.Validate(confirm, settlement) == "settlement_spend_limit");
        confirm.arguments.max_spend = 6;
        check("day_confirm_ceiling_below_amount_rejected", !DayActionPolicy.ArgumentsValid(confirm.action, confirm.arguments));
        confirm.arguments.max_spend = 7; confirm.arguments.settlement_ref = "old-settlement";
        check("day_confirm_stale_settlement_ref_rejected", DayActionPolicy.Validate(confirm, settlement) == "settlement_changed");
        confirm.arguments.settlement_ref = "settlement-current"; confirm.arguments.amount = 8; confirm.arguments.max_spend = 8;
        check("day_confirm_visible_total_must_match_exactly", DayActionPolicy.Validate(confirm, settlement) == "settlement_changed");
        confirm.arguments.amount = 7; confirm.arguments.max_spend = 7;
        var next = State("MORNING", "next_morning", day: 6, revision: 4, cash: 93); next.business.details.day.report_open = true;
        check("day_confirm_exact_cash_and_next_day_report_proves_success", DayActionPolicy.Postcondition(confirm, settlement, next) &&
            BusinessActionPolicy.Verify(confirm, settlement, next).outcome == "succeeded");
        next.business.details.day.transition_pending = true;
        check("day_confirm_transition_must_finish", !DayActionPolicy.Postcondition(confirm, settlement, next) && DayActionPolicy.Verify(confirm, settlement, next).outcome == "pending");
        next.business.details.day.transition_pending = false; next.cash = 94;
        check("day_confirm_wrong_cash_after_date_advance_is_unknown", DayActionPolicy.Verify(confirm, settlement, next).outcome == "uncertain");
        next.cash = 93; next.business.details.day.day_number = 7;
        check("day_confirm_cannot_skip_two_days", !DayActionPolicy.Postcondition(confirm, settlement, next));
        next.business.details.day.day_number = 6; next.business.details.day.report_open = false;
        check("day_confirm_requires_report_evidence", !DayActionPolicy.Postcondition(confirm, settlement, next));
        next.business.details.day.report_open = true;
        var report = Command("close_day_report", next);
        var startDuringReport = Command("start_business", next);
        check("day_nightly_report_prevents_next_morning_opening", BusinessActionPolicy.Validate(startDuringReport, next) == "day_ui_blocked");
        next.business.details.ui.blockers.Add("nightly_report");
        next.business.details.ui.status = "blocked"; next.interaction.consistency = "blocked"; next.interaction.operable = false;
        check("day_report_own_panel_does_not_block_native_report_close", BusinessActionPolicy.Validate(report, next) == null);
        next.business.details.ui.blockers.Clear(); next.business.details.ui.status = "consistent";
        next.interaction.consistency = "consistent"; next.interaction.operable = true;
        var prepareDuringSettlement = Command("start_business", settlement);
        check("day_settlement_blocks_ordinary_business_actions", BusinessActionPolicy.Validate(prepareDuringSettlement, settlement) == "day_ui_blocked");
        settlement.business.details.ui.blockers.Add("end_of_day"); settlement.business.details.ui.status = "blocked";
        settlement.interaction.consistency = "blocked"; settlement.interaction.operable = false;
        check("day_settlement_own_panel_keeps_confirmation_legal", BusinessActionPolicy.Validate(confirm, settlement) == null);
        settlement.business.details.ui.blockers.Clear(); settlement.business.details.ui.status = "consistent";
        settlement.interaction.consistency = "consistent"; settlement.interaction.operable = true;
        var transition = State("OPEN", "open"); transition.business.details.day.transition_pending = true;
        var receiveDuringTransition = Command("receive_next_customer", transition);
        check("day_transition_blocks_ordinary_receive", BusinessActionPolicy.Validate(receiveDuringTransition, transition) == "day_ui_blocked");
        var morning = State("MORNING", "morning", day: 6, revision: 5, cash: 93);
        check("day_report_close_requires_report_and_preserves_day_cash", DayActionPolicy.Validate(report, next) == null && DayActionPolicy.Postcondition(report, next, morning));
        check("day_report_cannot_close_absent_report", DayActionPolicy.Validate(Command("close_day_report", morning), morning) != null);
        var loadedReport = State("MORNING", "morning", day: 2, revision: 1, cash: 120);
        loadedReport.business.details.day.report_open = true;
        var closeLoadedReport = Command("close_day_report", loadedReport);
        check("day_loaded_morning_report_can_close_without_prior_confirmation", BusinessActionPolicy.Validate(closeLoadedReport, loadedReport) == null);
        check("day_loaded_report_keeps_start_business_blocked", BusinessActionPolicy.Validate(Command("start_business", loadedReport), loadedReport) == "day_ui_blocked");
        loadedReport.business.details.customer.presence = "present";
        bool currentCustomerBlocked = DayActionPolicy.Validate(closeLoadedReport, loadedReport) != null;
        loadedReport.business.details.customer.presence = "absent"; loadedReport.business.details.day.transition_pending = true;
        check("day_loaded_report_keeps_customer_and_transition_gates", currentCustomerBlocked && DayActionPolicy.Validate(closeLoadedReport, loadedReport) == "day_transition_pending");
        loadedReport.business.details.day.transition_pending = false;
        var loadedReportGate = new ActionGate(); int reportCloses = 0;
        loadedReportGate.Accept(closeLoadedReport, loadedReport, "broker", 1);
        loadedReportGate.Advance("broker", loadedReport, 2, 2, _ => reportCloses++, DayActionPolicy.Verify);
        var loadedMorning = State("MORNING", "morning", day: 2, revision: 2, cash: 120);
        check("day_loaded_report_close_proves_same_day_cash_not_day_advance", loadedReportGate.Advance("broker", loadedMorning, 3, 3,
            _ => reportCloses++, DayActionPolicy.Verify).result?.status == "succeeded" && reportCloses == 1);

        var refund = State("CLOSED", "settlement"); SetSettlement(refund, -7);
        var refundCommand = Command("confirm_end_of_day", refund);
        check("day_refund_signed_amount_and_zero_ceiling_admitted_by_gate", refundCommand.arguments.max_spend == 0 &&
            DayActionPolicy.Validate(refundCommand, refund) == null && new ActionGate().Accept(refundCommand, refund, "broker", 1) == null);
        var credited = State("MORNING", "next_morning", day: 6, revision: 2, cash: 107); credited.business.details.day.report_open = true;
        check("day_refund_negative_total_checks_actual_credit", DayActionPolicy.Postcondition(refundCommand, refund, credited));
        var zero = State("CLOSED", "settlement"); SetSettlement(zero, 0);
        var zeroCommand = Command("confirm_end_of_day", zero);
        var zeroNext = State("MORNING", "next_morning", day: 6, revision: 2); zeroNext.business.details.day.report_open = true;
        check("day_zero_total_still_requires_day_and_report_transition", DayActionPolicy.Postcondition(zeroCommand, zero, zeroNext));

        var copied = BusinessWire.Clone(settlement.business.details);
        copied.day.settlement.services[0].label = "mutated";
        check("day_clone_deep_copies_service_rows", settlement.business.details.day.settlement.services[0].label == "Visible service");
        copied.day.stage = "invented";
        check("day_wire_rejects_unknown_stage", !BusinessWire.ValidateDay(copied.day));
        copied = BusinessWire.Clone(settlement.business.details); copied.schema_version = 1;
        var oldBusiness = new BusinessObservation { support = "partial", item_refs = new List<string>(), details = copied };
        check("day_extension_rejected_on_historical_schema1", !BusinessWire.Validate(oldBusiness));
        copied = BusinessWire.Clone(settlement.business.details); copied.day.settlement.services[0].cost_text = new string('x', 257);
        check("day_visible_service_text_bounded", !BusinessWire.ValidateDay(copied.day));
        copied = BusinessWire.Clone(settlement.business.details);
        copied.day.settlement.services = Enumerable.Range(0, 33).Select(_ => new DayServiceState { label = "Visible service", cost_text = "1", cost = 1 }).ToList();
        check("day_visible_service_rows_capped_at_32", !BusinessWire.ValidateDay(copied.day));
        var fingerprint = ActionGate.Fingerprint(confirm); confirm.arguments.settlement_ref = "settlement-new";
        check("day_settlement_ref_enters_command_fingerprint", fingerprint != ActionGate.Fingerprint(confirm));
        confirm.arguments.settlement_ref = "settlement-current";
        var negativeOther = Command("close_business", open); negativeOther.arguments.amount = -1;
        check("day_signed_amount_exception_does_not_expand_other_actions", new ActionGate().Accept(negativeOther, open, "broker", 1)?.reason == "invalid_arguments");
        var inspection = State("CLOSED", "settlement"); SetSettlement(inspection, 7);
        inspection.business.details.inspection_handoff = new InspectionHandoffState { status = "waiting_for_user", requires_manual_inspection = true,
            handoff_ref = "inspection-pending", customer_ref = "historical-customer", reason = "manual_item_inspection",
            items = new List<InspectionHandoffItem> { new InspectionHandoffItem { item_ref = "historical-item", name = "Visible item" } } };
        check("day_actions_cannot_bypass_manual_inspection", DayActionPolicy.Validate(Command("confirm_end_of_day", inspection), inspection) == "inspection_handoff");

        var gate = new ActionGate(); var once = Command("confirm_end_of_day", settlement); once.id = "day-once"; int calls = 0;
        gate.Accept(once, settlement, "broker", 1); gate.Accept(once, settlement, "broker", 1);
        gate.Advance("broker", settlement, 2, 2, _ => calls++, DayActionPolicy.Verify);
        var result = gate.Advance("broker", next, 3, 3, _ => calls++, DayActionPolicy.Verify).result;
        check("day_confirm_invokes_once_and_gate_rechecks_day_postcondition", calls == 1 && result?.status == "succeeded");
        check("day_confirm_duplicate_returns_retained_result", gate.Accept(once, next, "broker", 4)?.status == "succeeded" && calls == 1);
        gate.Acknowledge(once.id);
        check("day_confirm_acknowledged_command_cannot_replay", gate.Accept(once, next, "broker", 5)?.reason == "command_already_acknowledged");

        var projection = new BusinessProjection(); var item = new BusinessItemInput { Identity = new object(), Identifier = "box_tampon", Name = "Visible item", Units = 1 };
        var input = new BusinessInput { Customer = new BusinessCustomerInput { Identity = new object(), Presence = "present", Arrived = true, TradeType = "sell", Phase = "trading", DialogueStage = "absent", MultiBuyDisabled = false },
            Containers = new[] { new BusinessContainerInput { Container = "front", Completeness = "complete" }, new BusinessContainerInput { Container = "back", Completeness = "complete", Items = new[] { item } }, new BusinessContainerInput { Container = "player", Completeness = "complete" } },
            Quote = new BusinessQuoteInput { Status = "complete", Mode = "normal", BindingVerified = true, DisplayedTotal = 10, PurchaseIdentities = new[] { item.Identity } },
            Ui = new BusinessUiInput { Status = "consistent", Coverage = "verified", Complete = true } };
        var previous = projection.Capture("w", true, () => input);
        projection.Invalidate("w", "day_changed");
        var refreshed = projection.Capture("w", true, () => input);
        check("day_change_invalidates_business_refs_without_rotating_world", previous.customer_ref != refreshed.customer_ref && previous.offer_ref != refreshed.offer_ref && previous.item_refs[0] != refreshed.item_refs[0]);
    }

    private static Snapshot State(string phase, string stage, int day = 5, int revision = 1, int cash = 100)
    {
        var business = new BusinessProjection().Capture("w", true, () => new BusinessInput
        {
            Customer = new BusinessCustomerInput { Presence = "absent", Phase = "absent", DialogueStage = "absent" },
            Containers = new[] { new BusinessContainerInput { Container = "front", Completeness = "complete" },
                new BusinessContainerInput { Container = "back", Completeness = "complete" }, new BusinessContainerInput { Container = "player", Completeness = "complete" } },
            Ui = new BusinessUiInput { Status = "consistent", Coverage = "verified", Complete = true }
        });
        business.details.day = new DayState { day_number = day, remaining_customers = 0, stage = stage };
        return new Snapshot { schema_version = 3, world_epoch = "w", revision = revision, version = "w:" + revision,
            scene = "shop", phase = phase, loaded = true, ready = true, cash = cash, business = business,
            interaction = new InteractionObservation { phase = phase, consistency = "consistent", operable = true }, legal_actions = new List<LegalAction>() };
    }

    private static void SetSettlement(Snapshot state, int total)
    {
        state.business.details.day.can_confirm = true;
        state.business.details.day.settlement = new SettlementState { settlement_ref = "settlement-current", total = total,
            services = new List<DayServiceState> { new DayServiceState { label = "Visible service", cost_text = total.ToString(), cost = total, selected = true } } };
    }

    private static Command Command(string action, Snapshot state)
    {
        var args = action == "confirm_end_of_day" ? new ActionArguments { settlement_ref = state.business.details.day.settlement.settlement_ref,
            amount = state.business.details.day.settlement.total, max_spend = Math.Max(0, state.business.details.day.settlement.total) } : new ActionArguments();
        var legalArgs = action == "confirm_end_of_day" ? new ActionArguments { settlement_ref = args.settlement_ref, amount = args.amount, max_spend = args.max_spend } : new ActionArguments();
        if (!state.legal_actions.Any(legal => legal.action == action && BusinessActionPolicy.SameArguments(legal.arguments, legalArgs)))
            state.legal_actions.Add(new LegalAction { action = action, arguments = legalArgs });
        return new Command { schema_version = 3, id = "day-command", broker_epoch = "broker", action = action, arguments = args,
            expected_world_epoch = state.world_epoch, expected_version = state.version, expected_revision = state.revision,
            start_deadline_unix_ms = 100, execution_timeout_ms = 5000 };
    }
}
