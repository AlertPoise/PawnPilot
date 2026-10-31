using PawnPilot;

int count = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name); count++;
}
Snapshot State(string version = "world:1", bool ready = true, bool loaded = true, bool open = true,
    string epoch = "world", int revision = 1) =>
    new Snapshot
    {
        schema_version = Protocol.SchemaVersion, version = version, world_epoch = epoch, revision = revision,
        loaded = loaded, ready = ready, scene = "InventoryScene", phase = "MORNING",
        not_ready_reason = ready ? null : "game_ui_settling", menu_open = open, can_close_menu = ready && open
    };
Command Cmd(string id = "a", string action = "close_menu", Snapshot state = null, string brokerEpoch = "broker",
    long expires = 100)
{
    state ??= State();
    return new Command
    {
        schema_version = Protocol.SchemaVersion, id = id, broker_epoch = brokerEpoch, action = action,
        expected_version = state.version, expected_world_epoch = state.world_epoch,
        expected_revision = state.revision, expires_at_unix_ms = expires
    };
};
int calls = 0;
Snapshot Close() { calls++; return State("world:2", ready:true, open:false, revision:2); }

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

var gate = new ActionGate();
var state = State();
Check("not_ready_write_rejected", gate.Execute(Cmd("unready", state:State(ready:false)), State(ready:false), "broker", 1, Close).reason == "game_ui_settling" && calls == 0);
Check("not_loaded_write_rejected", gate.Execute(Cmd("unloaded", state:State(loaded:false,ready:false)), State(loaded:false,ready:false), "broker", 1, Close).reason == "not_loaded" && calls == 0);
Check("old_world_rejected", gate.Execute(Cmd("old-world", state:State(epoch:"old", version:"old:1")), state, "broker", 1, Close).reason == "stale_state" && calls == 0);
Check("old_revision_rejected", gate.Execute(Cmd("old-revision", state:State(revision:0)), state, "broker", 1, Close).reason == "stale_state" && calls == 0);
Check("wrong_broker_epoch_rejected", gate.Execute(Cmd("wrong-broker", brokerEpoch:"old-broker"), state, "broker", 1, Close).reason == "stale_broker_epoch" && calls == 0);
Check("unsupported_command_schema_rejected", gate.Execute(new Command { schema_version = 2, id = "schema", broker_epoch = "broker", action = "close_menu", expected_version = state.version, expected_world_epoch = state.world_epoch, expected_revision = state.revision, expires_at_unix_ms = 100 }, state, "broker", 1, Close).reason == "unsupported_schema" && calls == 0);
Check("deadline_before_dispatch", gate.Execute(Cmd("expired", state:state, expires:1), state, "broker", 1, Close).reason == "expired" && calls == 0);
Check("unknown_action", gate.Execute(Cmd("unknown", "open_menu", state), state, "broker", 1, Close).reason == "unknown_action" && calls == 0);
Check("menu_precondition", gate.Execute(Cmd("closed", state:State(open:false)), State(open:false), "broker", 1, Close).reason == "precondition" && calls == 0);
var successCommand = Cmd(state:state);
var success = gate.Execute(successCommand, state, "broker", 1, Close);
Check("observed_postcondition", success.status == "succeeded" && success.broker_epoch == "broker" && calls == 1 && !success.after.menu_open);
Check("duplicate_not_reexecuted", ReferenceEquals(success, gate.Execute(successCommand, State(version:"world:2", open:false, revision:2), "broker", 1, Close)) && calls == 1);
var conflictingDuplicate = Cmd(id:"a", action:"close_menu", state:State(open:false, revision:2), expires:101);
Check("duplicate_id_payload_conflict_rejected", gate.Execute(conflictingDuplicate, State(version:"world:2", open:false, revision:2), "broker", 1, Close).reason == "command_id_conflict" && calls == 1);
Check("read_only_observe", gate.Execute(Cmd("observe", "observe", State(loaded:false,ready:false)), State(loaded:false,ready:false), "broker", 1, Close).status == "succeeded" && calls == 1);
Check("exception_is_unknown", gate.Execute(Cmd("throws", state:state), state, "broker", 1, () => throw new Exception()).status == "outcome_unknown");
Check("unchanged_postcondition_is_unknown", gate.Execute(Cmd("unchanged", state:state), state, "broker", 1, () => State()).status == "outcome_unknown");
Check("scene_exit_is_unknown", gate.Execute(Cmd("exit", state:state), state, "broker", 1, () => State(loaded:false,ready:false,open:false)).status == "outcome_unknown");
Check("new_world_postcondition_is_unknown", gate.Execute(Cmd("world-change", state:state), state, "broker", 1, () => State(epoch:"new-world",version:"new-world:1",open:false)).status == "outcome_unknown");
ActionResult nested = null;
gate.Execute(Cmd("outer", state:state), state, "broker", 1, () => { nested = gate.Execute(Cmd("inner", state:state), state, "broker", 1, Close); return State(open:false,revision:2); });
Check("reentrant_write_rejected", nested.reason == "busy" && calls == 1);
Check("null_command_safe", gate.Execute(null, State(), "broker", 1, Close).reason == "invalid_command");
Console.WriteLine($"TOTAL {count} passed, 0 failed (managed lifecycle and gate only; no Unity runtime)");
