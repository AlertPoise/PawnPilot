using System;

namespace PawnPilot
{
    public sealed class ActionDriverStep
    {
        public bool failed;
        public bool newlyFailed;
        public string failureType;
        public Progress progress;
    }

    // Fail-closed boundary around all observation and per-frame executor calls. ProbeBehaviour
    // supplies Unity Update/Capture delegates; after any driver exception they are never called
    // again for this session. This class has no Unity dependency and is exercised by managed tests.
    public sealed class ActionDriver
    {
        private readonly ActionGate gate;
        private bool failed;
        private string failureType;

        public ActionDriver(ActionGate gate) { this.gate = gate ?? throw new ArgumentNullException(nameof(gate)); }
        public bool IsFailed => failed;

        public ActionDriverStep Tick(Action refresh, Func<Snapshot> capture, string brokerEpoch,
            long nowUnixMs, long monotonicNowMs, Action<Command> invokeNative,
            Func<Command, Snapshot, Snapshot, VerificationDecision> verify)
        {
            if (failed) return new ActionDriverStep { failed = true, failureType = failureType };
            try
            {
                refresh();
                if (!gate.IsWriteBlocked) return new ActionDriverStep();
                var snapshot = capture();
                var step = gate.Advance(brokerEpoch, snapshot, nowUnixMs, monotonicNowMs, invokeNative, verify);
                return new ActionDriverStep { progress = step.progress };
            }
            catch (Exception ex)
            {
                failed = true;
                failureType = ex.GetType().Name;
                // If execution crossed the invocation boundary this becomes unknown; otherwise it
                // is a safe rejection. Cancel is idempotent and prevents later frame completion.
                gate.Cancel("observation_exception", null);
                return new ActionDriverStep { failed = true, newlyFailed = true, failureType = failureType,
                    progress = gate.CurrentProgress };
            }
        }
    }
}
