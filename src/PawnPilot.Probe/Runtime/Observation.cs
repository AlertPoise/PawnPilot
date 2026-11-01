using System;

namespace PawnPilot
{
    // Lifecycle observation is pure logic. It never discovers or mutates game objects.
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
}
