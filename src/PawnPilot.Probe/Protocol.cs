using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PawnPilot
{
    // Only a local adapter that has not entered the native action may use this signal.
    // Exceptions raised by/after the native entry must remain outcome_unknown.
    internal sealed class NativeActionRejectedException : Exception
    {
        internal NativeActionRejectedException(string reason) : base(reason) { }
    }
    public static class Protocol
    {
        public const int SchemaVersion = 2;
        public const int BusinessSchemaVersion = 3;
        public static bool Supported(int version) => version == SchemaVersion || version == BusinessSchemaVersion;
        public const int MaxCommands = 4096;
    }

    [Serializable] public sealed class SessionConfig { public string token; public int port; }

    [Serializable] public sealed class BusinessObservation
    {
        public string support = "unsupported";
        public string customer_ref;
        public List<string> item_refs;
        public string offer_ref;
        public string collection_fingerprint;
        public BusinessDetails details;
        // Newtonsoft's opt-in property predicate keeps frozen wire v2 byte shape intact.
        public bool ShouldSerializedetails() => details != null;
    }

    [Serializable] public sealed class InteractionObservation
    {
        public string phase;
        public string consistency;
        public string reason;
        public bool operable;
    }

    [Serializable] public sealed class LegalAction
    {
        public string action;
        public ActionArguments arguments;
    }

    [Serializable] public sealed class Snapshot
    {
        public int schema_version;
        public string version, world_epoch, phase, scene, not_ready_reason;
        public int revision;
        public bool loaded, ready, menu_open, can_close_menu;
        public int cash;
        public InteractionObservation interaction;
        public BusinessObservation business;
        public List<LegalAction> legal_actions;
    }

    // Null means the optional property was absent. The wire parser rejects explicit nulls.
    [Serializable] public sealed class ActionArguments
    {
        public string customer_ref;
        public List<string> item_refs;
        public string offer_ref;
        public string collection_fingerprint;
        public string option_ref;
        public string dialogue_ref, mode, authorization_ref;
        public int? amount, max_spend;
    }

    [Serializable] public sealed class Command
    {
        public int schema_version;
        public string id, broker_epoch, action, expected_version, expected_world_epoch;
        public int expected_revision;
        public ActionArguments arguments;
        public long start_deadline_unix_ms;
        public int execution_timeout_ms;
    }

    [Serializable] public sealed class Progress
    {
        public string id, broker_epoch, stage;
        public int sequence;
        public bool may_have_side_effects;
        public long? started_at_unix_ms;
        public long? execution_deadline_unix_ms;
    }

    [Serializable] public sealed class ActionResult
    {
        public int schema_version;
        public string id, broker_epoch, status, reason;
        public Snapshot before, after;
        public Progress execution;
    }

    [Serializable] public sealed class Exchange
    {
        public int schema_version;
        public string session_id, broker_epoch;
        public Snapshot state;
        public bool has_result;
        public ActionResult result;
        public Progress progress;
    }

    [Serializable] public sealed class Reply
    {
        public int schema_version;
        public string broker_epoch, acknowledged_result_id;
        public bool has_command;
        public Command command;
    }

    public sealed class ExecutionStep
    {
        public Progress progress;
        public ActionResult result;
    }

    public sealed class VerificationDecision
    {
        public string outcome; // pending | succeeded | uncertain
        public string reason;
        public static VerificationDecision Pending() => new VerificationDecision { outcome = "pending", reason = "postcondition_pending" };
        public static VerificationDecision Succeeded() => new VerificationDecision { outcome = "succeeded", reason = "postcondition_observed" };
        public static VerificationDecision Uncertain(string reason) => new VerificationDecision { outcome = "uncertain", reason = reason };
    }

    // This state machine contains no Unity calls. ProbeBehaviour is the only production driver;
    // it calls Accept from Poll's Unity coroutine and Advance from Update on Unity's main thread.
    public sealed class ActionGate
    {
        private sealed class Record
        {
            public string id;
            public string fingerprint;
            public string terminalStatus;
            public Command command;
            public Snapshot before;
            public Progress progress;
            public ActionResult result;
            public bool acknowledged;
            public bool invocationStarted;
            public long startedUnixMs;
            public long deadlineUnixMs;
            public long startedMonotonicMs;
        }

        private readonly Dictionary<string, Record> records = new Dictionary<string, Record>(StringComparer.Ordinal);
        private Record active;
        private Record pendingAcknowledgement;

        public Progress CurrentProgress => active == null || active.acknowledged ? null : Clone(active.progress);
        // Outgoing evidence is copied; ACK clears the stored result and leaves compact identity data.
        public ActionResult PendingResult => Clone(pendingAcknowledgement?.result);
        public bool IsWriteBlocked => active != null;
        public bool HasPendingResult => pendingAcknowledgement != null;
        public int RetainedResultCount
        {
            get { int count = 0; foreach (var record in records.Values) if (record.result != null) count++; return count; }
        }

        // Returns a cached/immediate terminal result, null after accepting a valid command.
        public ActionResult Accept(Command command, Snapshot before, string currentBrokerEpoch, long nowUnixMs)
        {
            if (command == null || string.IsNullOrEmpty(command.id) || command.id.Length > 128)
                return NewResult(command, "rejected", "invalid_command", before, null, null);

            if (!Protocol.Supported(command.schema_version))
                return NewResult(command, "rejected", "schema_version_unsupported", before, null, Completed(command, false, 1));
            string shapeError = ValidateArgumentsShape(command.arguments);
            if (command.schema_version == Protocol.SchemaVersion && HasV3Arguments(command.arguments)) shapeError = "invalid_arguments";
            if (shapeError != null) return NewResult(command, "rejected", shapeError, before, null, Completed(command, false, 1));
            string fingerprint = EnvelopeFingerprint(command);
            if (records.TryGetValue(command.id, out var existing))
            {
                if (!string.Equals(existing.fingerprint, fingerprint, StringComparison.Ordinal))
                    return NewResult(command, "rejected", "command_id_conflict", before, null, Completed(command, false, 1));
                if (existing.acknowledged)
                    return NewResult(command, "rejected", "command_already_acknowledged", before, null, Completed(command, false, 1));
                return Clone(existing.result);
            }
            if (records.Count >= Protocol.MaxCommands)
                return NewResult(command, "rejected", "session_capacity_restart_required", before, null, Completed(command, false, 1));
            if (active != null || pendingAcknowledgement != null)
                return NewResult(command, "rejected", "busy", before, null, Completed(command, false, 1));

            string reason = ValidateForAdmission(command, before, currentBrokerEpoch, nowUnixMs);
            var record = new Record { id = command.id, fingerprint = fingerprint, command = Clone(command), before = Clone(before) };
            records.Add(command.id, record);
            if (reason != null)
            {
                record.progress = Completed(command, false, 1);
                record.result = NewResult(command, "rejected", reason, before, null, record.progress);
                pendingAcknowledgement = record;
                return Clone(record.result);
            }
            record.progress = new Progress
            {
                id = command.id, broker_epoch = command.broker_epoch, stage = "validated", sequence = 1,
                may_have_side_effects = false
            };
            active = record;
            return null;
        }

        // Exactly one driver calls Advance once per Unity Update. invokeNative must only start the
        // supported native action; snapshot is freshly captured after readiness/world checks.
        public ExecutionStep Advance(string currentBrokerEpoch, Snapshot snapshot, long nowUnixMs,
            long monotonicNowMs, Action<Command> invokeNative,
            Func<Command, Snapshot, Snapshot, VerificationDecision> verify)
        {
            if (active == null) return new ExecutionStep();
            if (active.terminalStatus != null) return new ExecutionStep();
            if (active.result != null) return new ExecutionStep { progress = Clone(active.progress), result = Clone(active.result) };

            if (!active.invocationStarted)
            {
                string reason = ValidateBeforeInvoke(active, currentBrokerEpoch, snapshot, nowUnixMs);
                if (reason != null)
                {
                    Finish(active, "rejected", reason, snapshot, Completed(active.command, false, Next(active.progress.sequence)));
                    return CurrentStep();
                }

                // A delivered command that missed its start window is known not to have invoked in
                // this Mod session, so this terminal rejection carries effects=false and no times.
                if (nowUnixMs >= active.command.start_deadline_unix_ms)
                {
                    Finish(active, "rejected", "start_deadline_elapsed", snapshot,
                        Completed(active.command, false, Next(active.progress.sequence)));
                    return CurrentStep();
                }

                active.invocationStarted = true;
                active.startedUnixMs = nowUnixMs;
                active.deadlineUnixMs = checked(nowUnixMs + active.command.execution_timeout_ms);
                active.startedMonotonicMs = monotonicNowMs;
                active.progress = new Progress
                {
                    id = active.command.id, broker_epoch = active.command.broker_epoch,
                    stage = "invoking", sequence = Next(active.progress.sequence), may_have_side_effects = true,
                    started_at_unix_ms = active.startedUnixMs, execution_deadline_unix_ms = active.deadlineUnixMs
                };
                try { invokeNative(Clone(active.command)); }
                catch (NativeActionRejectedException ex)
                {
                    active.invocationStarted = false;
                    Finish(active, "rejected", ValidText(ex.Message, 256) ? ex.Message : "native_precondition_changed",
                        snapshot, Completed(active.command, false, Next(active.progress.sequence)));
                    return CurrentStep();
                }
                catch
                {
                    FinishUnknown(active, "execution_exception", snapshot);
                    return CurrentStep();
                }
                return CurrentStep();
            }

            if (!string.Equals(active.command.broker_epoch, currentBrokerEpoch, StringComparison.Ordinal))
            {
                FinishUnknown(active, "broker_epoch_changed_after_invocation", snapshot);
                return CurrentStep();
            }
            if (snapshot == null || !string.Equals(snapshot.world_epoch, active.before.world_epoch, StringComparison.Ordinal) ||
                !string.Equals(snapshot.scene, active.before.scene, StringComparison.Ordinal))
            {
                FinishUnknown(active, "world_changed_after_invocation", snapshot);
                return CurrentStep();
            }
            if (snapshot.schema_version != active.command.schema_version || !IsSnapshotValid(snapshot))
            {
                // Retain the valid original evidence; never serialize an invalid post sample as
                // evidence of success (nor let a test verifier waive the wire invariant).
                FinishUnknown(active, "invalid_state_after_invocation", null);
                return CurrentStep();
            }
            if (monotonicNowMs - active.startedMonotonicMs >= active.command.execution_timeout_ms)
            {
                FinishUnknown(active, "execution_timeout", snapshot);
                return CurrentStep();
            }

            if (active.progress.stage == "invoking")
                active.progress = new Progress
                {
                    id = active.command.id, broker_epoch = active.command.broker_epoch,
                    stage = "verifying", sequence = Next(active.progress.sequence), may_have_side_effects = true,
                    started_at_unix_ms = active.startedUnixMs, execution_deadline_unix_ms = active.deadlineUnixMs
                };

            VerificationDecision decision;
            try { decision = verify(Clone(active.command), Clone(active.before), Clone(snapshot)) ?? VerificationDecision.Pending(); }
            catch
            {
                FinishUnknown(active, "verification_exception", snapshot);
                return CurrentStep();
            }
            if (decision.outcome == "uncertain")
                FinishUnknown(active, decision.reason ?? "verification_uncertain", snapshot);
            else if (decision.outcome == "succeeded")
            {
                // The test injection can delay/drive observation but cannot weaken the production
                // action's close-menu evidence predicate.
                if (!(active.command.action == "close_menu" ? CloseMenuPostcondition(active.before, snapshot) :
                    BusinessActionPolicy.Postcondition(active.command, active.before, snapshot)))
                {
                    FinishUnknown(active, "postcondition_not_observed", snapshot);
                    return CurrentStep();
                }
                var completed = new Progress
                {
                    id = active.command.id, broker_epoch = active.command.broker_epoch,
                    stage = "completed", sequence = Next(active.progress.sequence), may_have_side_effects = true,
                    started_at_unix_ms = active.startedUnixMs, execution_deadline_unix_ms = active.deadlineUnixMs
                };
                Finish(active, "succeeded", decision.reason ?? "postcondition_observed", snapshot, completed);
            }
            return CurrentStep();
        }

        // A broker restart after accepted validation is a safe rejection; after the native-call
        // boundary it is uncertain and keeps the local write lease until Mod restart.
        public ActionResult OnBrokerEpochChanged(string newBrokerEpoch, Snapshot snapshot)
        {
            if (active == null || active.terminalStatus != null || active.result != null || string.Equals(active.command.broker_epoch, newBrokerEpoch, StringComparison.Ordinal)) return null;
            if (active.invocationStarted) FinishUnknown(active, "broker_epoch_changed_after_invocation", snapshot);
            else Finish(active, "rejected", "stale_broker_epoch", snapshot, Completed(active.command, false, Next(active.progress.sequence)));
            return Clone(active.result);
        }

        // Local lifecycle cancellation only. There is deliberately no wire/MCP cancel action.
        // Before native invocation it safely rejects; after the conservative invocation boundary
        // it records uncertainty and keeps the write lease, preventing any late completion/replay.
        public ActionResult Cancel(string reason, Snapshot snapshot)
        {
            if (active == null || active.terminalStatus != null || active.result != null) return null;
            string safeReason = string.IsNullOrEmpty(reason) ? "cancelled" : reason.Substring(0, Math.Min(reason.Length, 512));
            if (active.invocationStarted) FinishUnknown(active, safeReason, snapshot);
            else Finish(active, "rejected", safeReason, snapshot, Completed(active.command, false, Next(active.progress.sequence)));
            return Clone(active.result);
        }

        // ACK frees snapshots for terminal success/rejection but leaves a compact tombstone.
        // Unknown outcomes retain the active write block after their result has been ACKed.
        public bool Acknowledge(string id)
        {
            if (pendingAcknowledgement == null || !string.Equals(pendingAcknowledgement.id, id, StringComparison.Ordinal)) return false;
            var record = pendingAcknowledgement;
            record.acknowledged = true;
            record.terminalStatus = record.result?.status;
            record.result = null;
            record.command = null;
            record.before = null;
            pendingAcknowledgement = null;
            if (record.terminalStatus != "outcome_unknown" && ReferenceEquals(active, record)) active = null;
            return true;
        }

        private ExecutionStep CurrentStep() => new ExecutionStep
        {
            progress = active == null || active.acknowledged ? null : Clone(active.progress),
            result = active == null ? null : Clone(active.result)
        };

        private void FinishUnknown(Record record, string reason, Snapshot after)
        {
            var progress = new Progress
            {
                id = record.command.id, broker_epoch = record.command.broker_epoch,
                stage = "uncertain", sequence = Next(record.progress.sequence), may_have_side_effects = true,
                started_at_unix_ms = record.startedUnixMs, execution_deadline_unix_ms = record.deadlineUnixMs
            };
            Finish(record, "outcome_unknown", reason, after, progress);
        }

        private void Finish(Record record, string status, string reason, Snapshot after, Progress progress)
        {
            record.progress = progress;
            record.result = NewResult(record.command, status, reason, record.before, after, progress);
            record.terminalStatus = status;
            pendingAcknowledgement = record;
        }

        private static string ValidateForAdmission(Command c, Snapshot before, string brokerEpoch, long now)
        {
            if (!Protocol.Supported(c.schema_version)) return "schema_version_unsupported";
            if (!ValidText(c.id, 128) || !ValidText(c.broker_epoch, 128) || !ValidText(c.action, 128) ||
                !ValidText(c.expected_version, 256) || !ValidText(c.expected_world_epoch, 128)) return "invalid_command";
            if (c.expected_revision < 0 || c.start_deadline_unix_ms < 1 || c.start_deadline_unix_ms > 9007199254740991L ||
                c.execution_timeout_ms < 1 || c.execution_timeout_ms > 30000)
                return "invalid_command";
            if (string.IsNullOrEmpty(brokerEpoch) || !string.Equals(c.broker_epoch, brokerEpoch, StringComparison.Ordinal)) return "stale_broker_epoch";
            if (!IsSnapshotValid(before)) return "invalid_state";
            if (c.schema_version != before.schema_version) return "schema_version_mismatch";
            if (c.expected_version != before.version || c.expected_world_epoch != before.world_epoch || c.expected_revision != before.revision)
                return "stale_state";
            if (c.action != "close_menu" && (c.schema_version != Protocol.BusinessSchemaVersion || !BusinessActionPolicy.IsRegistered(c.action))) return "unknown_action";
            if (c.action == "close_menu" && !ArgumentsEmpty(c.arguments)) return "invalid_arguments";
            if (!before.ready)
                return !before.loaded ? "not_loaded" : (before.not_ready_reason ?? "not_ready");
            if (c.action == "close_menu")
            { if (!before.can_close_menu || !HasLegalCloseMenu(before)) return "precondition"; }
            else { string reason = BusinessActionPolicy.Validate(c, before); if (reason != null) return reason; }
            if (now >= c.start_deadline_unix_ms) return "start_deadline_elapsed";
            return null;
        }

        private static string ValidateBeforeInvoke(Record record, string brokerEpoch, Snapshot snapshot, long now)
        {
            if (!string.Equals(record.command.broker_epoch, brokerEpoch, StringComparison.Ordinal)) return "stale_broker_epoch";
            if (!IsSnapshotValid(snapshot)) return "invalid_state";
            if (snapshot == null || snapshot.world_epoch != record.before.world_epoch || snapshot.scene != record.before.scene ||
                snapshot.version != record.command.expected_version || snapshot.revision != record.command.expected_revision)
                return "stale_state";
            if (!snapshot.ready)
                return !snapshot.loaded ? "not_loaded" : (snapshot.not_ready_reason ?? "not_ready");
            if (snapshot.schema_version != record.command.schema_version) return "schema_version_mismatch";
            if (record.command.action == "close_menu")
            { if (!snapshot.can_close_menu || !HasLegalCloseMenu(snapshot)) return "precondition"; }
            else { string reason = BusinessActionPolicy.Validate(record.command, snapshot); if (reason != null) return reason; }
            if (now >= record.command.start_deadline_unix_ms) return "start_deadline_elapsed";
            if (now > 9007199254740991L - record.command.execution_timeout_ms) return "execution_deadline_out_of_range";
            return null;
        }

        public static bool IsSnapshotValid(Snapshot snapshot)
        {
            if (snapshot == null || !Protocol.Supported(snapshot.schema_version) || !ValidText(snapshot.world_epoch, 128) ||
                snapshot.revision < 0 || snapshot.version != snapshot.world_epoch + ":" + snapshot.revision.ToString(CultureInfo.InvariantCulture) ||
                snapshot.scene == null || snapshot.scene.Length > 128 || snapshot.phase == null || snapshot.phase.Length > 128 ||
                (snapshot.not_ready_reason != null && (snapshot.not_ready_reason.Length < 1 || snapshot.not_ready_reason.Length > 256)) ||
                snapshot.interaction == null || snapshot.business == null) return false;
            if ((snapshot.ready && (!snapshot.loaded || snapshot.not_ready_reason != null)) ||
                (!snapshot.ready && snapshot.not_ready_reason == null)) return false;
            if (!ValidText(snapshot.interaction.phase, 128) || (snapshot.interaction.consistency != "consistent" &&
                snapshot.interaction.consistency != "updating" && snapshot.interaction.consistency != "unverified" &&
                snapshot.interaction.consistency != "blocked") ||
                (snapshot.interaction.reason != null && (snapshot.interaction.reason.Length < 1 || snapshot.interaction.reason.Length > 256)) ||
                (snapshot.interaction.operable && (!snapshot.ready || snapshot.interaction.consistency == "blocked"))) return false;
            if (snapshot.schema_version == Protocol.SchemaVersion)
            {
                if (snapshot.business.support != "unsupported" || snapshot.business.customer_ref != null || snapshot.business.item_refs != null ||
                    snapshot.business.offer_ref != null || snapshot.business.collection_fingerprint != null || snapshot.business.details != null) return false;
            }
            else if (!BusinessWire.Validate(snapshot.business)) return false;
            if (snapshot.legal_actions == null || snapshot.legal_actions.Count > (snapshot.schema_version == 2 ? 1 : 9)) return false;
            bool close = false;
            var actions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var legal in snapshot.legal_actions)
            {
                if (legal == null || !actions.Add(legal.action) || !snapshot.ready) return false;
                if (legal.action == "close_menu")
                {
                    if (!ArgumentsEmpty(legal.arguments) || !snapshot.loaded || !snapshot.menu_open) return false;
                    close = true;
                }
                else if (snapshot.schema_version != 3 || !BusinessActionPolicy.IsEnabled(legal.action) || !BusinessActionPolicy.IsRegistered(legal.action) ||
                    !BusinessActionPolicy.ArgumentsValid(legal.action, legal.arguments) ||
                    snapshot.menu_open) return false;
            }
            if (snapshot.can_close_menu != close) return false;
            return true;
        }

        private static bool CloseMenuPostcondition(Snapshot before, Snapshot after) => before != null && after != null &&
            before.ready && after.ready && before.can_close_menu && !after.menu_open &&
            string.Equals(before.world_epoch, after.world_epoch, StringComparison.Ordinal) &&
            string.Equals(before.scene, after.scene, StringComparison.Ordinal) &&
            after.revision > before.revision && before.cash == after.cash;

        public static VerificationDecision VerifyCloseMenu(Command command, Snapshot before, Snapshot after)
        {
            if (command == null || command.action != "close_menu") return VerificationDecision.Uncertain("unsupported_verifier_action");
            if (after == null || !after.ready || after.menu_open) return VerificationDecision.Pending();
            if (after.cash != before.cash) return VerificationDecision.Uncertain("cash_changed_during_close_menu");
            if (after.revision > before.revision) return VerificationDecision.Succeeded();
            return VerificationDecision.Pending();
        }

        private static bool HasLegalCloseMenu(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.legal_actions == null || !snapshot.can_close_menu) return false;
            foreach (var legal in snapshot.legal_actions)
                if (legal != null && legal.action == "close_menu" && ArgumentsEmpty(legal.arguments)) return true;
            return false;
        }

        private static string ValidateArgumentsShape(ActionArguments a)
        {
            if (a == null) return "invalid_arguments";
            if (!ValidRef(a.customer_ref) || !ValidRef(a.offer_ref) || !ValidRef(a.option_ref) || !ValidRef(a.collection_fingerprint) ||
                !ValidRef(a.dialogue_ref) || !ValidRef(a.mode) || !ValidRef(a.authorization_ref) || a.amount < 0 || a.max_spend < 0)
                return "invalid_arguments";
            if (a.item_refs != null)
            {
                if (a.item_refs.Count < 1 || a.item_refs.Count > 64) return "invalid_arguments";
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string item in a.item_refs) if (!ValidRef(item) || !seen.Add(item)) return "invalid_arguments";
            }
            return null;
        }

        private static bool ValidRef(string value)
        {
            if (value == null) return true;
            if (value.Length < 1 || value.Length > 128) return false;
            foreach (char c in value)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') &&
                    c != '_' && c != '.' && c != ':' && c != '-') return false;
            return true;
        }

        private static bool ArgumentsEmpty(ActionArguments a) => a != null && a.customer_ref == null && a.item_refs == null &&
            a.offer_ref == null && a.collection_fingerprint == null && a.option_ref == null && !HasV3Arguments(a);

        private static bool HasV3Arguments(ActionArguments a) => a != null && (a.dialogue_ref != null || a.mode != null ||
            a.authorization_ref != null || a.amount.HasValue || a.max_spend.HasValue);

        private static bool ValidText(string value, int max) => !string.IsNullOrEmpty(value) && value.Length <= max;
        private static int Next(int value) => value == int.MaxValue ? int.MaxValue : value + 1;

        private static Progress Completed(Command c, bool effects, int sequence) => new Progress
        {
            id = c?.id, broker_epoch = c?.broker_epoch, stage = "completed", sequence = Math.Max(1, sequence),
            may_have_side_effects = effects
        };

        private static ActionResult NewResult(Command c, string status, string reason, Snapshot before, Snapshot after, Progress progress) => new ActionResult
        {
            schema_version = c != null && Protocol.Supported(c.schema_version) ? c.schema_version : Protocol.SchemaVersion, id = c?.id, broker_epoch = c?.broker_epoch,
            status = status, reason = reason,
            before = Clone(before != null && before.schema_version == ResultVersion(c) ? before : null),
            after = Clone(after != null && after.schema_version == ResultVersion(c) ? after : null), execution = Clone(progress)
        };

        private static int ResultVersion(Command c) => c != null && Protocol.Supported(c.schema_version) ? c.schema_version : Protocol.SchemaVersion;

        // Canonical /commands request object: sorted keys, compact JSON, ASCII escaping, ordered arrays.
        public static string Fingerprint(Command c)
        {
            return Sha256(CanonicalRequestJson(c));
        }

        public static string EnvelopeFingerprint(Command c)
        {
            string canonical = CanonicalRequestJson(c);
            string start = c.start_deadline_unix_ms.ToString(CultureInfo.InvariantCulture);
            string timeout = c.execution_timeout_ms.ToString(CultureInfo.InvariantCulture);
            return Sha256(canonical.Length.ToString(CultureInfo.InvariantCulture) + ":" + canonical +
                start.Length.ToString(CultureInfo.InvariantCulture) + ":" + start +
                timeout.Length.ToString(CultureInfo.InvariantCulture) + ":" + timeout);
        }

        private static string Sha256(string value)
        {
            byte[] bytes;
            using (var sha = SHA256.Create()) bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }

        public static string CanonicalRequestJson(Command c)
        {
            var s = new StringBuilder(512);
            s.Append('{');
            bool first = true;
            Property(s, ref first, "action", Quote(c.action));
            var args = new StringBuilder("{");
            bool argFirst = true;
            if (c.arguments.amount.HasValue) Property(args, ref argFirst, "amount", c.arguments.amount.Value.ToString(CultureInfo.InvariantCulture));
            if (c.arguments.authorization_ref != null) Property(args, ref argFirst, "authorization_ref", Quote(c.arguments.authorization_ref));
            if (c.arguments.collection_fingerprint != null) Property(args, ref argFirst, "collection_fingerprint", Quote(c.arguments.collection_fingerprint));
            if (c.arguments.customer_ref != null) Property(args, ref argFirst, "customer_ref", Quote(c.arguments.customer_ref));
            if (c.arguments.dialogue_ref != null) Property(args, ref argFirst, "dialogue_ref", Quote(c.arguments.dialogue_ref));
            if (c.arguments.item_refs != null)
            {
                var arr = new StringBuilder("[");
                for (int i = 0; i < c.arguments.item_refs.Count; i++) { if (i > 0) arr.Append(','); arr.Append(Quote(c.arguments.item_refs[i])); }
                arr.Append(']'); Property(args, ref argFirst, "item_refs", arr.ToString());
            }
            if (c.arguments.max_spend.HasValue) Property(args, ref argFirst, "max_spend", c.arguments.max_spend.Value.ToString(CultureInfo.InvariantCulture));
            if (c.arguments.mode != null) Property(args, ref argFirst, "mode", Quote(c.arguments.mode));
            if (c.arguments.offer_ref != null) Property(args, ref argFirst, "offer_ref", Quote(c.arguments.offer_ref));
            if (c.arguments.option_ref != null) Property(args, ref argFirst, "option_ref", Quote(c.arguments.option_ref));
            args.Append('}');
            Property(s, ref first, "arguments", args.ToString());
            Property(s, ref first, "broker_epoch", Quote(c.broker_epoch));
            Property(s, ref first, "expected_revision", c.expected_revision.ToString(CultureInfo.InvariantCulture));
            Property(s, ref first, "expected_version", Quote(c.expected_version));
            Property(s, ref first, "expected_world_epoch", Quote(c.expected_world_epoch));
            Property(s, ref first, "id", Quote(c.id));
            Property(s, ref first, "schema_version", c.schema_version.ToString(CultureInfo.InvariantCulture));
            s.Append('}');
            return s.ToString();
        }

        private static void Property(StringBuilder s, ref bool first, string key, string value)
        {
            if (!first) s.Append(','); first = false;
            s.Append(Quote(key)).Append(':').Append(value);
        }

        private static string Quote(string value)
        {
            var s = new StringBuilder("\"");
            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': s.Append("\\\""); break;
                    case '\\': s.Append("\\\\"); break;
                    case '\b': s.Append("\\b"); break;
                    case '\f': s.Append("\\f"); break;
                    case '\n': s.Append("\\n"); break;
                    case '\r': s.Append("\\r"); break;
                    case '\t': s.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7e) s.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else s.Append(c);
                        break;
                }
            }
            return s.Append('"').ToString();
        }

        private static Snapshot Clone(Snapshot s)
        {
            if (s == null) return null;
            var copy = new Snapshot
            {
                schema_version = s.schema_version, version = s.version, world_epoch = s.world_epoch, phase = s.phase,
                scene = s.scene, not_ready_reason = s.not_ready_reason, revision = s.revision, loaded = s.loaded,
                ready = s.ready, menu_open = s.menu_open, can_close_menu = s.can_close_menu, cash = s.cash,
                interaction = s.interaction == null ? null : new InteractionObservation
                { phase = s.interaction.phase, consistency = s.interaction.consistency, reason = s.interaction.reason, operable = s.interaction.operable },
                business = s.business == null ? null : new BusinessObservation
                { support = s.business.support, customer_ref = s.business.customer_ref, item_refs = s.business.item_refs == null ? null : new List<string>(s.business.item_refs), offer_ref = s.business.offer_ref, collection_fingerprint = s.business.collection_fingerprint, details = BusinessWire.Clone(s.business.details) },
                legal_actions = new List<LegalAction>()
            };
            if (s.legal_actions != null) foreach (var action in s.legal_actions)
                copy.legal_actions.Add(action == null ? null : new LegalAction { action = action.action, arguments = Clone(action.arguments) });
            return copy;
        }

        private static ActionArguments Clone(ActionArguments a) => a == null ? null : new ActionArguments
        {
            customer_ref = a.customer_ref, item_refs = a.item_refs == null ? null : new List<string>(a.item_refs),
            offer_ref = a.offer_ref, collection_fingerprint = a.collection_fingerprint, option_ref = a.option_ref,
            dialogue_ref = a.dialogue_ref, mode = a.mode, authorization_ref = a.authorization_ref, amount = a.amount, max_spend = a.max_spend
        };

        private static Progress Clone(Progress p) => p == null ? null : new Progress
        {
            id = p.id, broker_epoch = p.broker_epoch, stage = p.stage, sequence = p.sequence,
            may_have_side_effects = p.may_have_side_effects, started_at_unix_ms = p.started_at_unix_ms,
            execution_deadline_unix_ms = p.execution_deadline_unix_ms
        };

        private static ActionResult Clone(ActionResult r) => r == null ? null : new ActionResult
        {
            schema_version = r.schema_version, id = r.id, broker_epoch = r.broker_epoch, status = r.status, reason = r.reason,
            before = Clone(r.before), after = Clone(r.after), execution = Clone(r.execution)
        };

        private static Command Clone(Command c) => c == null ? null : new Command
        {
            schema_version = c.schema_version, id = c.id, broker_epoch = c.broker_epoch, action = c.action,
            expected_version = c.expected_version, expected_world_epoch = c.expected_world_epoch,
            expected_revision = c.expected_revision, arguments = Clone(c.arguments),
            start_deadline_unix_ms = c.start_deadline_unix_ms, execution_timeout_ms = c.execution_timeout_ms
        };
    }
}
