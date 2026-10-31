using System;
using System.Collections.Generic;

namespace PawnPilot
{
    public static class Protocol
    {
        public const int SchemaVersion = 1;
    }

    [Serializable] public sealed class SessionConfig { public string token; public int port; }
    [Serializable] public sealed class Snapshot
    {
        public int schema_version;
        public string version, world_epoch, phase, scene, not_ready_reason;
        public int revision;
        public bool loaded, ready, menu_open, can_close_menu;
        public int cash;
    }
    [Serializable] public sealed class Command
    {
        public int schema_version;
        public string id, broker_epoch, action, expected_version, expected_world_epoch;
        public int expected_revision;
        public long expires_at_unix_ms;
    }
    [Serializable] public sealed class ActionResult
    {
        public int schema_version;
        public string id, broker_epoch, status, reason;
        public Snapshot before, after;
    }
    [Serializable] public sealed class Exchange
    {
        public int schema_version;
        public string session_id, broker_epoch;
        public Snapshot state;
        public bool has_result;
        public ActionResult result;
    }
    [Serializable] public sealed class Reply
    {
        public int schema_version;
        public string broker_epoch, acknowledged_result_id;
        public bool has_command;
        public Command command;
    }

    // Pure lifecycle rules are kept separate from Unity so state transitions can be covered
    // by the managed gate tests. A store singleton alone is deliberately insufficient.
    public sealed class ReadinessSignals
    {
        public bool gameplay_scene, store_present, store_initialized, ui_initialized;
        public int stable_ready_frames;
    }
    public sealed class Readiness
    {
        public bool loaded, ready;
        public string phase, reason;
    }
    public static class GameReadiness
    {
        public static Readiness Evaluate(ReadinessSignals signals, string storePhase)
        {
            if (signals == null) signals = new ReadinessSignals();
            bool loaded = signals.gameplay_scene && signals.store_present && signals.store_initialized;
            if (!signals.gameplay_scene) return new Readiness { loaded = false, ready = false, phase = "not_loaded", reason = "not_gameplay_scene" };
            if (!signals.store_present) return new Readiness { loaded = false, ready = false, phase = "loading", reason = "player_store_missing" };
            if (!signals.store_initialized) return new Readiness { loaded = false, ready = false, phase = "loading", reason = "player_store_initializing" };
            if (!signals.ui_initialized) return new Readiness { loaded = loaded, ready = false, phase = "loading", reason = "game_ui_initializing" };
            if (signals.stable_ready_frames < 2) return new Readiness { loaded = loaded, ready = false, phase = "loading", reason = "game_ui_settling" };
            return new Readiness { loaded = true, ready = true, phase = storePhase ?? "unknown", reason = null };
        }
    }

    public sealed class WorldVersion
    {
        private string signature;
        private int revision;
        public string Epoch { get; private set; } = Guid.NewGuid().ToString("N");
        public int Revision => revision;

        public void Rotate()
        {
            Epoch = Guid.NewGuid().ToString("N");
            revision = 0;
            signature = null;
        }

        public string Observe(string nextSignature)
        {
            if (!string.Equals(signature, nextSignature, StringComparison.Ordinal))
            {
                revision++;
                signature = nextSignature;
            }
            return Epoch + ":" + revision;
        }
    }

    public sealed class WorldIdentityTracker
    {
        private string identity;

        public bool Ensure(string currentIdentity, WorldVersion version)
        {
            if (string.Equals(identity, currentIdentity, StringComparison.Ordinal)) return false;
            identity = currentIdentity;
            version.Rotate();
            return true;
        }

        public void OnGameLoaded(string currentIdentity, WorldVersion version)
        {
            identity = currentIdentity;
            version.Rotate();
        }

        public void OnSceneChanged(WorldVersion version)
        {
            identity = null;
            version.Rotate();
        }
    }

    public sealed class ReadyFrameTracker
    {
        private int lastFrame = -1;
        public int Count { get; private set; }

        public void Observe(bool candidateReady, int frame, bool advance)
        {
            if (!candidateReady)
            {
                Count = 0;
                lastFrame = frame;
                return;
            }
            if (advance && frame != lastFrame) Count = Math.Min(2, Count + 1);
            lastFrame = frame;
        }

        public void Reset(int frame)
        {
            Count = 0;
            lastFrame = frame;
        }
    }

    // All calls originate on Unity's main thread. Duplicate IDs are idempotent only when the
    // entire request envelope matches; reusing an ID for a different payload is rejected.
    public sealed class ActionGate
    {
        private readonly Dictionary<string, ActionResult> results = new Dictionary<string, ActionResult>();
        private readonly Dictionary<string, string> requestFingerprints = new Dictionary<string, string>();
        private bool executing;
        public ActionResult Execute(Command command, Snapshot before, string currentBrokerEpoch, long now, Func<Snapshot> invoke)
        {
            if (command == null || string.IsNullOrEmpty(command.id))
                return NewResult(command, "rejected", "invalid_command", before);
            string fingerprint = Fingerprint(command);
            if (results.TryGetValue(command.id, out var cached))
                return requestFingerprints[command.id] == fingerprint
                    ? cached
                    : NewResult(command, "rejected", "command_id_conflict", before);
            var result = NewResult(command, "rejected", null, before);
            if (executing) result.reason = "busy";
            else if (results.Count >= 1024) result.reason = "session_capacity_restart_required";
            else if (command.schema_version != Protocol.SchemaVersion) result.reason = "unsupported_schema";
            else if (string.IsNullOrEmpty(currentBrokerEpoch) || command.broker_epoch != currentBrokerEpoch) result.reason = "stale_broker_epoch";
            else if (command.expires_at_unix_ms <= now) result.reason = "expired";
            else if (before == null || command.expected_version != before.version ||
                command.expected_world_epoch != before.world_epoch || command.expected_revision != before.revision) result.reason = "stale_state";
            else if (command.action != "observe" && command.action != "close_menu") result.reason = "unknown_action";
            else if (command.action != "observe" && !before.ready) result.reason = !before.loaded ? "not_loaded" : (before.not_ready_reason ?? "not_ready");
            else if (command.action == "close_menu" && !before.can_close_menu) result.reason = "precondition";
            else
            {
                executing = true;
                try
                {
                    result.after = command.action == "observe" ? before : invoke();
                    bool verified = result.after != null && (command.action == "observe" ||
                        (result.after.ready && result.after.world_epoch == before.world_epoch && result.after.scene == before.scene && !result.after.menu_open));
                    result.status = verified ? "succeeded" : "outcome_unknown";
                    result.reason = verified ? "observed" : "postcondition_not_observed";
                }
                catch (Exception) { result.status = "outcome_unknown"; result.reason = "execution_exception"; }
                finally { executing = false; }
            }
            if (results.Count < 1024)
            {
                results[command.id] = result;
                requestFingerprints[command.id] = fingerprint;
            }
            return result;
        }

        private static ActionResult NewResult(Command command, string status, string reason, Snapshot before) => new ActionResult
        {
            schema_version = Protocol.SchemaVersion,
            id = command?.id,
            broker_epoch = command?.broker_epoch,
            status = status,
            reason = reason,
            before = before
        };

        private static string Fingerprint(Command command) => string.Join("\n", new[]
        {
            command.schema_version.ToString(), command.broker_epoch ?? "", command.action ?? "",
            command.expected_version ?? "", command.expected_world_epoch ?? "",
            command.expected_revision.ToString(), command.expires_at_unix_ms.ToString()
        });
    }
}
