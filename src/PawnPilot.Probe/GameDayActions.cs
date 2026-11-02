using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace PawnPilot
{
    // One-day native UI adapter. It never changes a date, cash, service toggle or queue.
    // The normal shutter, bedroll, settlement and report callbacks own those effects.
    internal sealed class GameDayActions
    {
        private sealed class Execution
        {
            internal string Action, World, Scene;
            internal int Day, Cash, Amount;
            internal PlayerStore Store;
            internal StoreStation Station;
            internal object Target;
            internal bool Returned, Completed, CallbackFailed, OutcomeUncertain;
        }

        private static readonly FieldInfo ShutterClosed = typeof(StoreShutterButton).GetField("isClosed", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Execution> executions = new Dictionary<string, Execution>(StringComparer.Ordinal);
        private readonly string referenceNamespace = Guid.NewGuid().ToString("N");
        private string world, settlementRef, settlementTerms;
        private object settlementIdentity;
        private int? settlementDay;
        private long nextReference;
        private Execution confirmation;
        private bool watchingCallbackErrors;

        internal static bool Handles(string action) => DayActionPolicy.Handles(action);

        internal DayState Read(string worldEpoch)
        {
            if (world != worldEpoch)
            {
                StopWatchingCallbackErrors();
                world = worldEpoch;
                RevokeSettlement();
                confirmation = null;
            }
            var state = new DayState();
            try
            {
                var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
                var ui = StoreUIManager.Instance;
                if (string.IsNullOrEmpty(worldEpoch) || store == null || ui == null)
                { state.reason = "day_native_roots_unavailable"; return state; }
                state.day_number = GameSettlementReader.DayNumber();
                if (!state.day_number.HasValue) { state.reason = "day_number_unavailable"; return state; }
                state.report_open = GameSettlementReader.ReportOpen();
                state.transition_pending = GameSettlementReader.TransitionPending() ||
                    (store.storeState == PlayerStore.StoreState.OPEN && !ShutterSettled(false)) ||
                    (store.storeState == PlayerStore.StoreState.CLOSED && (!ShutterSettled(true) || !FrontHidden()));
                bool settlementOpen = SettlementOpen();
                state.stage = settlementOpen ? "settlement" : store.storeState == PlayerStore.StoreState.OPEN ? "open" :
                    store.storeState == PlayerStore.StoreState.CLOSED ? "closed" : store.storeState == PlayerStore.StoreState.MORNING ? "morning" : "unavailable";
                if (store.storeState == PlayerStore.StoreState.OPEN)
                {
                    state.remaining_customers = VisibleRemainingCustomers(ui);
                    if (!state.remaining_customers.HasValue) state.reason = "day_counter_text_unverified";
                }

                if (confirmation != null && confirmation.Returned && ConfirmationComplete(confirmation, state))
                { confirmation.Completed = true; StopWatchingCallbackErrors(); }
                if (confirmation?.Completed == true && state.day_number.Value == (long)confirmation.Day + 1 &&
                    store.storeState == PlayerStore.StoreState.MORNING && !settlementOpen && !state.transition_pending)
                    state.stage = "next_morning";

                if (settlementOpen && GameSettlementReader.TryRead(out var settlement, out var identity, out var terms, out var reason))
                {
                    if (identity == null || string.IsNullOrEmpty(terms))
                    { RevokeSettlement(); state.reason = "settlement_binding_unavailable"; }
                    else
                    {
                        if (settlementRef == null || settlementDay != state.day_number || !ReferenceEquals(identity, settlementIdentity) || settlementTerms != terms)
                        {
                            settlementIdentity = identity; settlementTerms = terms; settlementDay = state.day_number;
                            settlementRef = "settlement-" + referenceNamespace + "-" + (++nextReference).ToString(CultureInfo.InvariantCulture);
                        }
                        settlement.settlement_ref = settlementRef;
                        state.settlement = settlement;
                    }
                }
                else
                {
                    RevokeSettlement();
                    if (settlementOpen) state.reason = "settlement_visible_terms_incomplete";
                }

                // Once confirm crosses the callback boundary, only its verification/report
                // closing path may continue. A failed callback is never offered a retry.
                bool pendingConfirmation = confirmation != null && !confirmation.Completed;
                bool clear = !state.transition_pending && !GameOverOpen() && !pendingConfirmation;
                state.can_close = clear && state.stage == "open" && state.remaining_customers == 0 &&
                    !state.report_open && !settlementOpen && CanCloseNative(store, ui);
                state.can_sleep = clear && state.stage == "closed" && !state.report_open && !settlementOpen && CanSleepNative(store, ui);
                state.can_confirm = clear && state.stage == "settlement" && state.settlement != null && !state.report_open &&
                    store.storeState == PlayerStore.StoreState.CLOSED && NoCurrentCustomer(store, ui) &&
                    NoOtherModal(ui, "confirm_end_of_day") && GameSettlementReader.ConfirmationReady() &&
                    (state.settlement.total <= 0 || state.settlement.total <= store.playerCash);
                if (pendingConfirmation && state.reason == null) state.reason = "day_confirmation_in_progress";
                if (confirmation?.CallbackFailed == true) state.reason = "day_native_callback_failed";
                else if (confirmation?.OutcomeUncertain == true) state.reason = "day_confirmation_unverified";
                if (GameOverOpen()) state.reason = "game_over_visible";
                return state;
            }
            catch
            {
                RevokeSettlement();
                return new DayState { reason = "day_native_read_failed" };
            }
        }

        internal IEnumerable<LegalAction> BuildLegalActions(Snapshot snapshot)
        {
            var actions = new List<LegalAction>();
            var day = snapshot?.business?.details?.day;
            if (day == null || !snapshot.ready) return actions;
            Add("close_business", new ActionArguments());
            Add("open_end_of_day", new ActionArguments());
            if (day.settlement != null)
                Add("confirm_end_of_day", new ActionArguments { settlement_ref = day.settlement.settlement_ref,
                    amount = day.settlement.total, max_spend = Math.Max(0, day.settlement.total) });
            Add("close_day_report", new ActionArguments());
            return actions;

            void Add(string action, ActionArguments args)
            {
                var command = new Command { schema_version = 3, action = action, arguments = args };
                string reason = Check(command, snapshot, candidate: true, out _);
                snapshot.business.details.candidates.Add(new BusinessCandidate { action = action,
                    status = reason == null ? "candidate" : "blocked", reason = reason ?? "native_day_preflight_verified" });
                if (reason == null) CandidateAdmission.TryAdd(snapshot, actions, new LegalAction { action = action, arguments = args });
            }
        }

        private string Check(Command command, Snapshot snapshot, bool candidate, out object target)
        {
            target = null;
            string reason = candidate ? DayActionPolicy.Validate(command, snapshot) : BusinessActionPolicy.Validate(command, snapshot);
            if (reason != null) return reason;
            if (snapshot.world_epoch != world || BusinessProjection.IsInspectionWaitingIn(snapshot)) return "day_world_or_handoff_changed";
            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            var ui = StoreUIManager.Instance;
            if (store == null || ui == null || snapshot.cash != store.playerCash || !NoOtherModal(ui, command.action)) return "day_native_scope_changed";
            var fresh = Read(snapshot.world_epoch);
            if (fresh.day_number != snapshot.business.details.day.day_number || !fresh.day_number.HasValue || fresh.transition_pending || GameOverOpen())
                return "day_number_or_transition_changed";
            switch (command.action)
            {
                case "close_business":
                    target = StoreShutterButton.Instance;
                    return fresh.can_close && CanCloseNative(store, ui) ? null : "close_business_native_precondition";
                case "open_end_of_day":
                    target = FindStoreControl<StoreBedroll>(ui);
                    return fresh.can_sleep && target != null && CanSleepNative(store, ui) ? null : "sleep_native_precondition";
                case "confirm_end_of_day":
                    target = settlementIdentity;
                    return fresh.can_confirm && fresh.settlement != null && fresh.settlement.settlement_ref == command.arguments.settlement_ref &&
                        fresh.settlement.total == command.arguments.amount && command.arguments.max_spend.HasValue &&
                        command.arguments.max_spend.Value >= Math.Max(0, fresh.settlement.total) &&
                        (fresh.settlement.total <= 0 || fresh.settlement.total <= store.playerCash) && GameSettlementReader.ConfirmationReady()
                        ? null : "settlement_native_terms_changed";
                case "close_day_report":
                    target = AdvCalendarUIManager.Instance;
                    // A loaded morning may reopen the native report. Closing that
                    // visible report does not need this Mod instance to have settled it.
                    return (fresh.stage == "morning" || fresh.stage == "next_morning") && fresh.report_open &&
                        GameSettlementReader.CloseReportReady() ? null : "day_report_native_precondition";
                default:
                    return "unsupported_day_action";
            }
        }

        internal void Invoke(Command command, Snapshot before)
        {
            if (command?.id == null || consumed.Contains(command.id) || consumed.Count >= Protocol.MaxCommands)
                throw new NativeActionRejectedException("day_command_already_consumed");
            string reason = Check(command, before, candidate: false, out var target);
            if (reason != null) throw new NativeActionRejectedException(reason);
            var execution = new Execution { Action = command.action, World = before.world_epoch, Scene = before.scene,
                Day = before.business.details.day.day_number.Value, Cash = before.cash, Amount = command.arguments.amount ?? 0,
                Store = PlayerStore.Instance, Station = StoreStation.Instance, Target = target };
            consumed.Add(command.id);
            executions[command.id] = execution;
            switch (command.action)
            {
                case "close_business":
                    ((StoreShutterButton)target).OnClick();
                    break;
                case "open_end_of_day":
                    ((StoreBedroll)target).OnClick();
                    break;
                case "confirm_end_of_day":
                    confirmation = execution;
                    Application.logMessageReceived += ObserveCallbackError;
                    watchingCallbackErrors = true;
                    EndOfDayUIManager.Instance.OnEndDayButtonClick();
                    break;
                case "close_day_report":
                    GameSettlementReader.CloseReport();
                    break;
                default:
                    throw new NativeActionRejectedException("unsupported_day_action");
            }
            execution.Returned = true;
        }

        internal VerificationDecision Verify(Command command, Snapshot before, Snapshot after)
        {
            if (command?.id == null || !executions.TryGetValue(command.id, out var execution) || !execution.Returned)
                return VerificationDecision.Uncertain("day_invocation_evidence_missing");
            if (execution.CallbackFailed)
                return Uncertain(execution, "day_native_callback_failed");
            if (after == null || !after.ready) return VerificationDecision.Pending();
            if (execution.World != after.world_epoch || execution.Scene != after.scene || !ReferenceEquals(execution.Store, PlayerStore.Instance) ||
                !ReferenceEquals(execution.Station, StoreStation.Instance)) return Uncertain(execution, "day_native_world_changed");
            if (GameOverOpen()) return Uncertain(execution, "game_over_after_day_action");
            var decision = DayActionPolicy.Verify(command, before, after);
            if (decision.outcome == "uncertain") return Uncertain(execution, decision.reason);
            if (decision.outcome != "succeeded") return decision;
            var ui = StoreUIManager.Instance;
            switch (command.action)
            {
                case "close_business":
                    return ReferenceEquals(execution.Target, StoreShutterButton.Instance) && PlayerStore.Instance.storeState == PlayerStore.StoreState.CLOSED &&
                        ShutterSettled(true) && NoCurrentCustomer(PlayerStore.Instance, ui) && FrontHidden() && !SettlementOpen() && !GameSettlementReader.ReportOpen() &&
                        !GameSettlementReader.TransitionPending() && !Active(ui.closeShutterConfirmationPanel)
                        ? decision : VerificationDecision.Pending();
                case "open_end_of_day":
                    return SettlementOpen() && after.business.details.day.settlement != null && NoCurrentCustomer(PlayerStore.Instance, ui) &&
                        !GameSettlementReader.TransitionPending() ? decision : VerificationDecision.Pending();
                case "confirm_end_of_day":
                    return ReferenceEquals(confirmation, execution) && execution.Completed && ConfirmationComplete(execution, after.business.details.day)
                        ? decision : VerificationDecision.Pending();
                case "close_day_report":
                    return !GameSettlementReader.ReportOpen() && !GameSettlementReader.TransitionPending() && !SettlementOpen()
                        ? decision : VerificationDecision.Pending();
                default:
                    return VerificationDecision.Uncertain("unsupported_day_verifier_action");
            }
        }

        private static bool ConfirmationComplete(Execution execution, DayState state) => execution != null && !execution.CallbackFailed && !execution.OutcomeUncertain && state != null &&
            state.day_number.HasValue && (long)state.day_number.Value == (long)execution.Day + 1 &&
            ReferenceEquals(execution.Store, PlayerStore.Instance) && ReferenceEquals(execution.Station, StoreStation.Instance) &&
            PlayerStore.Instance.storeState == PlayerStore.StoreState.MORNING &&
            (long)execution.Cash - PlayerStore.Instance.playerCash == execution.Amount && !state.transition_pending && state.report_open &&
            !SettlementOpen() && !GameOverOpen();

        private static bool CanCloseNative(PlayerStore store, StoreUIManager ui)
        {
            var shutter = StoreShutterButton.Instance;
            return store.storeState == PlayerStore.StoreState.OPEN && !store.devMode && VisibleRemainingCustomers(ui) == 0 &&
                NativeIdle(store, ui) && NoOtherModal(ui, "close_business") && store.lockShutterButtonFlag <= 0 &&
                shutter != null && shutter.isActiveAndEnabled && shutter.gameObject.scene == ui.gameObject.scene &&
                shutter.buttonRenderer != null && shutter.buttonRenderer.enabled && shutter.buttonRenderer.gameObject.activeInHierarchy &&
                ShutterSettled(false) && store.CanEndDay(); // UI's pure capability bool; never enumerate future customers.
        }

        private static bool CanSleepNative(PlayerStore store, StoreUIManager ui) => store.storeState == PlayerStore.StoreState.CLOSED &&
            NativeIdle(store, ui) && NoOtherModal(ui, "open_end_of_day") && ShutterSettled(true) && FrontHidden() &&
            FindStoreControl<StoreBedroll>(ui) != null;

        private static bool NativeIdle(PlayerStore store, StoreUIManager ui) => store != null && ui != null &&
            store.lockAllFlag <= 0 && store.lockStorePhysicalFlag <= 0 && store.lockNextBellFlag <= 0 &&
            !store.cinematicMode && !store.isClientBeingArrested && !store.isHandlingShootout && !ui.isShot && !ui.isShootingMode &&
            !ui.isAboutToStartMainDialog && !ui.IsTyping() && !store.callNextWhenPossible && !Interactable.isPaused && NoCurrentCustomer(store, ui);

        private static bool NoCurrentCustomer(PlayerStore store, StoreUIManager ui) => store != null && ui != null &&
            store.currentClientInstance == null && !store.IsClientPresent() && !store.callNextWhenPossible &&
            (ui.clientGameObject == null || (!ui.clientGameObject.gameObject.activeInHierarchy && !ui.clientGameObject.IsMoving())) &&
            !ui.isAboutToStartMainDialog && DialogUIManager.Instance != null && DialogUIManager.Instance.currentDialog == null && !ui.IsTyping();

        private static int? VisibleRemainingCustomers(StoreUIManager ui)
        {
            // The scene field points at clientCounterTMP itself, not either of the
            // StoreClientCounter controllers that update this shared visible label.
            var text = ui?.clientCounter == null ? null : ui.clientCounter.GetComponent<TMPro.TextMeshProUGUI>();
            if (!VisibleUiReader.TextVisible(text)) return null;
            string value = VisibleUiReader.RenderedText(text)?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > 10) return null;
            foreach (char digit in value) if (digit < '0' || digit > '9') return null;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count >= 0 ? count : (int?)null;
        }

        private static bool ShutterSettled(bool closed)
        {
            var shutter = StoreShutterButton.Instance;
            if (shutter == null || ShutterClosed == null || !(ShutterClosed.GetValue(shutter) is bool actual) || actual != closed ||
                shutter.shutterGameObject == null || shutter.blockerGameObject == null) return false;
            Vector3 door = closed ? shutter.startPosition : shutter.endPosition;
            Vector3 blocker = closed ? shutter.blockerStartPosition : shutter.blockerEndPosition;
            return Vector3.SqrMagnitude(shutter.shutterGameObject.transform.position - door) <= 0.0001f &&
                Vector3.SqrMagnitude(shutter.blockerGameObject.transform.localPosition - blocker) <= 0.01f;
        }

        private static bool FrontHidden() => EmporiumEntry.Instance?.frontInvWindow != null && !EmporiumEntry.Instance.frontInvWindow.IsVisible();
        private static bool SettlementOpen() => EndOfDayUIManager.Instance?.UI != null && EndOfDayUIManager.Instance.UI.activeInHierarchy;
        private static bool GameOverOpen() => GameOverUIManager.Instance?.uiPanel != null && GameOverUIManager.Instance.uiPanel.activeInHierarchy;
        private static bool Active(GameObject root) => root != null && root.activeInHierarchy;

        private static bool NoOtherModal(StoreUIManager ui, string action)
        {
            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            if (store == null || store.cinematicMode || store.isHandlingShootout || store.isClientBeingArrested ||
                ui == null || ui.isShot || ui.isShootingMode || ui.isAboutToStartMainDialog) return false;
            if (ui == null || Active(ui.escapeUIManager?.menuPanel) || Active(ui.gameOptionUIManager?.optionPanel) ||
                Active(ui.confirmPanelUIManager?.panel) || Active(ui.closeShutterConfirmationPanel) ||
                Active(GenericConfirmationUIManager.Instance?.panel) || Active(BargainUIManager.Instance?.uiObject) ||
                UIDragHandler.isDraggingAnyUI || GameSettlementReader.TransitionPending() || GameOverOpen()) return false;
            var negotiation = NegociationUIManager.Instance;
            if (Active(negotiation?.uiObject) && Active(negotiation.uiObject.GetComponent<NegotiationTabletFrame>()?.confirmRoot)) return false;
            if (SettlementOpen() && action != "confirm_end_of_day") return false;
            if (GameSettlementReader.ReportOpen() && action != "close_day_report") return false;
            foreach (string blocker in BusinessUiReader.Read().blockers)
            {
                if (blocker == "global_ui_stack_unobservable" || blocker == "global_raycast_order_unobservable" ||
                    blocker == "quote_rebuild_completion_unobservable" || blocker == "tutorial_group_active_unverified" ||
                    blocker == "control_hints_active_unverified") continue;
                if (blocker == "background_blocker_active_unverified" &&
                    ((action == "confirm_end_of_day" && SettlementOpen()) || (action == "close_day_report" && GameSettlementReader.ReportOpen()))) continue;
                if (blocker == "end_of_day" && action == "confirm_end_of_day" && SettlementOpen()) continue;
                if (blocker == "nightly_report" && action == "close_day_report" && GameSettlementReader.ReportOpen()) continue;
                return false;
            }
            return true;
        }

        private static T FindStoreControl<T>(StoreUIManager ui) where T : MonoBehaviour
        {
            if (ui == null) return null;
            T found = null;
            foreach (var control in UnityEngine.Object.FindObjectsOfType<T>())
            {
                if (control == null || control.gameObject.scene != ui.gameObject.scene || !control.isActiveAndEnabled || !control.gameObject.activeInHierarchy) continue;
                if (found != null) return null;
                found = control;
            }
            return found;
        }

        private void RevokeSettlement()
        { settlementRef = null; settlementIdentity = null; settlementTerms = null; settlementDay = null; }

        private void ObserveCallbackError(string message, string stackTrace, LogType type)
        {
            if (confirmation == null || confirmation.Completed || (type != LogType.Error && type != LogType.Exception)) return;
            // These exact native catch blocks continue the sequence despite a failed callback.
            // Retain only a fixed failure flag; never publish log/exception contents.
            if (message != null && (message.StartsWith("[BlackScreenSequence] actionDuringBlack threw, continuing sequence: ", StringComparison.Ordinal) ||
                message.StartsWith("[BlackScreenSequence] actionAfterBlack threw: ", StringComparison.Ordinal))) confirmation.CallbackFailed = true;
        }

        private void StopWatchingCallbackErrors()
        {
            if (!watchingCallbackErrors) return;
            Application.logMessageReceived -= ObserveCallbackError;
            watchingCallbackErrors = false;
        }

        internal void Dispose() => StopWatchingCallbackErrors();

        internal void ObserveResult(ActionResult result)
        {
            if (result?.status != "outcome_unknown" || result.id == null || !executions.TryGetValue(result.id, out var execution)) return;
            Uncertain(execution, "day_confirmation_unverified");
        }

        private VerificationDecision Uncertain(Execution execution, string reason)
        {
            if (ReferenceEquals(execution, confirmation))
            {
                execution.OutcomeUncertain = true;
                StopWatchingCallbackErrors();
            }
            return VerificationDecision.Uncertain(reason);
        }
    }
}
