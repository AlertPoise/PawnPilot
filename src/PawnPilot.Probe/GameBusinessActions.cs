using System;
using System.Collections.Generic;
using System.Linq;

namespace PawnPilot
{
    // Unity-free types and predicates live in BusinessActionPolicy. This adapter is kept out of
    // snapshots/results and rechecks fresh local Unity evidence immediately before any call.
    internal sealed class GameBusinessActions
    {
        private readonly BusinessProjection projection;

        internal GameBusinessActions(BusinessProjection businessProjection)
        {
            projection = businessProjection ?? throw new ArgumentNullException(nameof(businessProjection));
        }

        // These suppliers use the ordinary SELL / OnBuyAllClick route. The tutorial seller's
        // stock is populated by its normal dialogue end action; no barter route is enabled.
        internal static bool IsSupportedOrdinarySeller(StoreClient client) => client != null &&
            client.clientIntent == StoreClient.ClientIntent.SELL &&
            (client.identifier == "scavGeneral" || client.identifier == "shadyPharma" ||
             client.identifier == "food_scav_story");

        internal NativeActionEvidence Check(Command command, Snapshot snapshot)
        {
            var evidence = new NativeActionEvidence
            {
                Action = command?.action,
                Captured = command != null && snapshot != null,
                SceneReady = false,
                UiCoverageVerified = false,
                UiConsistent = false,
                NoBlockers = false,
                NativeEntryVerified = false,
                CapabilityScopeVerified = false,
                Reason = "native_scope_unverified"
            };
            if (command == null || snapshot == null || snapshot.schema_version != Protocol.BusinessSchemaVersion ||
                !snapshot.ready || snapshot.business?.details?.customer == null) return evidence;

            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            var ui = StoreUIManager.Instance;
            var entry = EmporiumEntry.Instance;
            var dialog = DialogUIManager.Instance;
            if (store == null || ui == null) return evidence;

            evidence.SceneReady = true;
            // Start/receive are scoped to the store's own phase/locks and the exact shutter/bell
            // button in the active gameplay scene. Other actions use their own local UI scope.
            evidence.UiCoverageVerified = false;
            evidence.OnlyTargetBlocker = HasExactlyOneConfirmationBlocker(snapshot.business.details.ui?.blockers);
            evidence.UiConsistent = snapshot.business.details.ui?.coverage == "verified" &&
                ((snapshot.business.details.ui.status == "consistent" && snapshot.interaction?.consistency == "consistent") ||
                 (snapshot.business.details.ui.status == "blocked" && snapshot.interaction?.consistency == "blocked" && evidence.OnlyTargetBlocker));
            evidence.NoBlockers = snapshot.business.details.ui?.blockers != null &&
                !HasActionBlockingEvidence(snapshot.business.details.ui.blockers, command.action) && !snapshot.menu_open &&
                !KnownBlockingUi(ui, store);
            evidence.NativeEntryVerified = true; // exact candidate method found in the current DLL
            evidence.CapabilityScopeVerified = false; // computed per action below
            evidence.StoreClosed = store.storeState == PlayerStore.StoreState.MORNING;
            evidence.StoreOpen = store.storeState == PlayerStore.StoreState.OPEN;
            evidence.CanStartDay = store.CanStartDay();
            evidence.StoreLockReleased = store.lockShutterButtonFlag <= 0;
            evidence.NoLocks = (command.action == "start_business"
                    ? store.lockShutterButtonFlag <= 0 : store.lockNextBellFlag <= 0) &&
                !store.isClientBeingArrested && !store.isHandlingShootout && !ui.isShot &&
                ((command.action != "start_business" && command.action != "receive_next_customer") ||
                    (!ui.IsTyping() && !Interactable.isPaused));
            evidence.NoCallNextPending = !store.callNextWhenPossible;
            evidence.AnyCurrentCustomer = store.currentClientInstance != null;
            evidence.NoCurrentCustomer = store.currentClientInstance == null && !store.IsClientPresent() &&
                !store.callNextWhenPossible && (ui.clientGameObject == null || !ui.clientGameObject.gameObject.activeInHierarchy);
            evidence.CanNextClient = store.CanNextClient(); // IL-pure; no queue data is exported

            var customer = snapshot.business.details.customer;
            string observedCustomerRef = snapshot.business.customer_ref;
            var currentInstance = store.currentClientInstance;
            evidence.CurrentCustomerMatches = currentInstance != null && customer != null &&
                customer.presence == "present" && projection.TryResolveCustomer(snapshot.world_epoch,
                    observedCustomerRef, out var customerIdentity) && ReferenceEquals(customerIdentity, currentInstance) &&
                observedCustomerRef == command.arguments?.customer_ref;
            evidence.CustomerArrived = store.isClientArrived;
            var blueprint = currentInstance == null ? null : currentInstance.GetClientBlueprint();
            bool dialogueAction = command.action == "advance_dialogue" || command.action == "choose_dialogue_option";
            bool dismissAction = command.action == "dismiss_customer";
            evidence.OrdinarySeller = IsSupportedOrdinarySeller(blueprint) ||
                (dialogueAction && (GameSaleActions.IsSupportedOrdinaryBuyer(blueprint) ||
                    IsSupportedTutorialMentorDialogue(blueprint) || IsAcknowledgedBargainDeparture(blueprint, snapshot))) ||
                (dismissAction && IsTutorialFoodBuyerDismissal(blueprint));
            // The reader never invokes or exposes customer callbacks. Enabled dialogue, prepare,
            // and dismissal actions are narrowly scoped to this ordinary arrived customer and the
            // currently visible native control; their normal game callbacks run as part of the call.
            evidence.CallbackScopeVerified = false;
            evidence.Dismissable = currentInstance != null && store.IsClientDimissable();
            evidence.DismissInterceptClear = IsPermittedDismissCallback(blueprint);
            evidence.AutoCallNextDisabled = EmporiumOption.Instance != null && !EmporiumOption.Instance.autoCallNext;

            var current = dialog == null ? null : dialog.currentDialog;
            if (command.action == "open_trade_panel")
            {
                var panel = NegociationUIManager.Instance;
                var actor = ui.clientGameObject;
                var interactable = actor == null ? null : actor.GetComponent<Interactable>();
                bool ordinary = IsSupportedOrdinarySeller(blueprint) || GameSaleActions.IsSupportedOrdinaryBuyer(blueprint);
                bool dialogueDone = dialog != null && current == null && !ui.IsTyping() && !ui.isAboutToStartMainDialog;
                bool targetReady = panel?.uiObject != null && actor != null && actor.isActiveAndEnabled &&
                    actor.gameObject.activeInHierarchy && !actor.IsMoving() &&
                    interactable != null && interactable.isActiveAndEnabled && interactable.isAvailable &&
                    !Interactable.isPaused && !ui.isShootingMode && !ui.isMagnifyingMode;
                evidence.NativeTarget = targetReady ? panel : null;
                evidence.NativeTargetAvailable = targetReady;
                evidence.CapabilityScopeVerified = evidence.CurrentCustomerMatches && evidence.CustomerArrived &&
                    ordinary && blueprint != null && !blueprint.isBusinessComplete && dialogueDone && targetReady &&
                    !panel.uiObject.activeSelf && !UnsafeVisibleModal(ui, store) && evidence.NoBlockers;
                evidence.Reason = !ordinary ? "ordinary_trade_customer_required" :
                    !evidence.CurrentCustomerMatches ? "customer_changed" :
                    !evidence.CustomerArrived ? "customer_arriving" : !dialogueDone ? "dialogue_in_progress" :
                    panel?.uiObject != null && panel.uiObject.activeSelf ? "trade_panel_already_open" :
                    !targetReady ? "customer_interaction_unavailable" :
                    !evidence.CapabilityScopeVerified ? "trade_panel_open_blocked" : null;
                return evidence;
            }
            evidence.DialogueMatches = current != null && evidence.CurrentCustomerMatches &&
                projection.TryResolveDialogue(snapshot.world_epoch, observedCustomerRef,
                    customer?.dialogue_ref, out var dialogueIdentity) && ReferenceEquals(dialogueIdentity, current) &&
                customer.dialogue_ref == command.arguments?.dialogue_ref;
            evidence.DialogueTyping = current != null && dialog.IsLineStillDisplaying(current);
            evidence.WaitingForAdvance = dialog != null && dialog.isWaitingForManualAdvance;
            evidence.DialogueChoice = dialog != null && dialog.IsChoice();
            evidence.NextDialogueExists = current != null && current.nextDialogue != null;
            evidence.NoPendingEndAction = false; // private end-pending / scheduled callback state
            evidence.MainDialogueReady = current != null && store.currentClientInstance != null && store.isClientArrived &&
                !ui.isAboutToStartMainDialog && customer != null &&
                projection.TryResolveCustomer(snapshot.world_epoch, observedCustomerRef, out var arrivalCustomer) &&
                ReferenceEquals(arrivalCustomer, store.currentClientInstance) &&
                projection.TryResolveDialogue(snapshot.world_epoch, observedCustomerRef, customer.dialogue_ref,
                    out var verifiedDialogue) && ReferenceEquals(verifiedDialogue, current);

            evidence.ItemSetMatches = ExactFrontSelection(snapshot, command.arguments?.item_refs) &&
                command.arguments?.collection_fingerprint == snapshot.business.details.front_collection_fingerprint;
            evidence.TransferScopeVerified = entry != null && entry.frontInvWindow != null && entry.frontInvWindow.IsVisible() &&
                evidence.ItemSetMatches && CompleteContainer(snapshot, "back");
            // The native whole-set handler attempts the complete front set in the back counter;
            // it can drop a remainder, so verification classifies partial movement as unknown.
            evidence.TransferTargetCapacityVerified = false;

            // Resolve only fixed known control types in the active gameplay scene.
            switch (command.action)
            {
                case "start_business":
                    var shutter = StoreShutterButton.Instance;
                    var shutterInteractable = shutter == null ? null : shutter.GetComponent<Interactable>();
                    evidence.NativeTarget = shutter != null && shutter.isActiveAndEnabled && shutter.gameObject.activeInHierarchy &&
                        (shutterInteractable == null || shutterInteractable.isAvailable) ? shutter : null;
                    break;
                case "receive_next_customer":
                    var bell = FindStoreControl<StoreBell>(ui);
                    var bellInteractable = bell == null ? null : bell.GetComponent<Interactable>();
                    evidence.NativeTarget = bell != null && bell.isActiveAndEnabled && bell.gameObject.activeInHierarchy &&
                        (bellInteractable == null || bellInteractable.isAvailable) ? bell : null;
                    break;
                case "advance_dialogue":
                    evidence.NativeTarget = dialog != null && dialog.isActiveAndEnabled ? dialog : null;
                    break;
                case "choose_dialogue_option":
                    if (projection.TryResolveOption(snapshot.world_epoch, observedCustomerRef,
                        customer?.dialogue_ref, command.arguments?.option_ref, out var optionIdentity))
                    {
                        var option = optionIdentity as DialogChoiceBubble;
                        if (option != null && option.type == 0 && option.gameObject.activeInHierarchy && option.IsInteractable())
                        {
                            var rect = option.GetComponent<UnityEngine.RectTransform>();
                            if (rect != null && option.TMP != null && VisibleUiReader.RectVisible(rect) &&
                                VisibleUiReader.TextVisible(option.TMP, rect))
                            {
                                evidence.NativeTarget = option;
                                evidence.OptionVisibleEnabled = true;
                            }
                        }
                    }
                    break;
                case "prepare_purchase_items":
                    var transfer = FindStoreControl<StoreTransferButton>(ui);
                    evidence.NativeTarget = transfer != null && transfer.isActiveAndEnabled && transfer.gameObject.activeInHierarchy
                        ? transfer : null;
                    break;
                case "cancel_confirmation":
                    var generic = GenericConfirmationUIManager.Instance;
                    if (generic != null && generic.panel != null && generic.panel.activeInHierarchy)
                    {
                        evidence.ConfirmationOpen = true;
                        evidence.ConfirmationKind = "generic_confirmation";
                        evidence.NativeTarget = generic;
                        evidence.ConfirmationCancelSafe = false; // private delegate may have effects
                    }
                    else if (ui.confirmPanelUIManager != null && ui.confirmPanelUIManager.panel != null &&
                        ui.confirmPanelUIManager.panel.activeInHierarchy)
                    {
                        evidence.ConfirmationOpen = true;
                        evidence.ConfirmationKind = "confirmation";
                        evidence.NativeTarget = ui.confirmPanelUIManager;
                        evidence.ConfirmationCancelSafe = false; // private noAction delegate may have effects
                    }
                    break;
                case "dismiss_customer":
                    var dismiss = FindStoreControl<StoreDismissButton>(ui);
                    evidence.NativeTarget = dismiss != null && dismiss.isActiveAndEnabled && dismiss.gameObject.activeInHierarchy
                        ? dismiss : null;
                    break;
            }
            evidence.NativeTargetAvailable = evidence.NativeTarget != null;
            bool scopedAction = command.action == "start_business" || command.action == "receive_next_customer";
            bool transferAction = command.action == "prepare_purchase_items";
            evidence.CapabilityScopeVerified =
                (scopedAction && evidence.NativeTargetAvailable && evidence.NativeEntryVerified && evidence.NoBlockers && evidence.NoLocks &&
                    (command.action == "start_business"
                        ? evidence.StoreClosed && evidence.CanStartDay && evidence.NoCurrentCustomer && evidence.NoCallNextPending
                        : evidence.StoreOpen && evidence.NoCurrentCustomer && evidence.NoCallNextPending && evidence.CanNextClient)) ||
                (dialogueAction && evidence.NativeTargetAvailable && evidence.NativeEntryVerified &&
                    !UnsafeVisibleModal(ui, store) && !HasActionBlockingEvidence(snapshot.business.details.ui?.blockers, command.action) &&
                    evidence.CurrentCustomerMatches && evidence.CustomerArrived &&
                    evidence.OrdinarySeller && evidence.DialogueMatches &&
                    (command.action == "advance_dialogue"
                        ? !evidence.DialogueChoice && (command.arguments?.mode == "skip" ? evidence.DialogueTyping :
                            command.arguments?.mode == "next" && !evidence.DialogueTyping && evidence.WaitingForAdvance)
                        : evidence.DialogueChoice && evidence.OptionVisibleEnabled)) ||
                (transferAction && evidence.NativeTargetAvailable && evidence.NativeEntryVerified &&
                    !UnsafeVisibleModal(ui, store) && !HasActionBlockingEvidence(snapshot.business.details.ui?.blockers, command.action) &&
                    evidence.CurrentCustomerMatches &&
                    evidence.CustomerArrived && evidence.OrdinarySeller && evidence.ItemSetMatches && evidence.TransferScopeVerified) ||
                (dismissAction && evidence.NativeTargetAvailable && evidence.NativeEntryVerified &&
                    !UnsafeVisibleModal(ui, store) && !HasActionBlockingEvidence(snapshot.business.details.ui?.blockers, command.action) &&
                    evidence.CurrentCustomerMatches &&
                    evidence.CustomerArrived && evidence.StoreOpen && evidence.NoLocks &&
                    customer.phase == "trading" && current == null && !ui.IsTyping() && !Interactable.isPaused &&
                    evidence.Dismissable && evidence.DismissInterceptClear);
            evidence.Reason = evidence.CapabilityScopeVerified ? null :
                !evidence.NativeTargetAvailable ? "native_target_unavailable" :
                !evidence.NoBlockers ? "native_ui_blocked" :
                !evidence.NoLocks ? "native_control_locked" :
                scopedAction && !evidence.NoCurrentCustomer ? "customer_or_departure_present" :
                scopedAction && !evidence.NoCallNextPending ? "call_next_pending" :
                command.action == "receive_next_customer" && !evidence.CanNextClient ? "no_customer_ready" :
                "native_action_precondition";
            return evidence;
        }

        // Called from the main-thread snapshot capture. Candidates are backed by current native
        // state, not by displayed future customers or opaque queue data.
        internal List<LegalAction> BuildLegalActions(Snapshot snapshot)
        {
            var result = new List<LegalAction>();
            if (snapshot == null || !snapshot.ready || snapshot.schema_version != Protocol.BusinessSchemaVersion) return result;
            AddIfEligible("start_business", snapshot, result);
            AddIfEligible("receive_next_customer", snapshot, result);
            AddCurrentCustomerActions(snapshot, result);
            return result;
        }

        private void AddIfEligible(string action, Snapshot snapshot, List<LegalAction> result)
        {
            if (!BusinessActionPolicy.IsEnabled(action)) return;
            NativeActionEvidence evidence;
            try
            {
                var command = new Command { schema_version = 3, action = action, arguments = new ActionArguments() };
                evidence = Check(command, snapshot);
            }
            catch { return; }
            if (!evidence.Captured || !evidence.SceneReady || !evidence.CapabilityScopeVerified)
            {
                if (snapshot.business?.details?.candidates != null)
                    snapshot.business.details.candidates.Add(new BusinessCandidate
                    { action = action, status = "blocked", reason = evidence.Reason ?? "native_action_precondition" });
                return;
            }
            if (action == "start_business")
            {
                if (snapshot.phase != "MORNING" || !evidence.StoreClosed || !evidence.CanStartDay || !evidence.NoLocks ||
                    !evidence.NoCurrentCustomer || !evidence.NoCallNextPending) return;
            }
            else if (snapshot.phase != "OPEN" || !evidence.StoreOpen || !evidence.NoLocks ||
                !evidence.NoCurrentCustomer || !evidence.NoCallNextPending || !evidence.CanNextClient) return;
            var customer = snapshot.business?.details?.customer;
            if (snapshot.menu_open || customer == null || snapshot.business?.details?.ui?.blockers == null ||
                HasActionBlockingEvidence(snapshot.business.details.ui.blockers, action)) return;
            if (customer.presence != "absent") return;
            // Day transitions are action-local snapshot gates too. Do not advertise a
            // bell/open candidate that the protocol would reject during shutter motion.
            var candidate = new Command { schema_version = Protocol.BusinessSchemaVersion,
                action = action, arguments = new ActionArguments() };
            if (BusinessActionPolicy.ValidateCandidate(candidate, snapshot) != null) return;
            result.Add(new LegalAction { action = action, arguments = new ActionArguments() });
        }

        private void AddCurrentCustomerActions(Snapshot snapshot, List<LegalAction> result)
        {
            var customer = snapshot.business?.details?.customer;
            var customerRef = snapshot.business?.customer_ref;
            if (customer == null || customer.presence != "present" || customer.arrived != true ||
                customer.phase == "leaving" || string.IsNullOrEmpty(customerRef)) return;

            if (!string.IsNullOrEmpty(customer.dialogue_ref))
            {
                if (customer.dialogue_stage == "typing")
                    AddScopedCandidate("advance_dialogue", new ActionArguments { customer_ref = customerRef,
                        dialogue_ref = customer.dialogue_ref, mode = "skip" }, snapshot, result);
                else if (customer.dialogue_stage == "waiting")
                    AddScopedCandidate("advance_dialogue", new ActionArguments { customer_ref = customerRef,
                        dialogue_ref = customer.dialogue_ref, mode = "next" }, snapshot, result);
                if (customer.dialogue_stage == "choices" && customer.option_refs != null)
                    foreach (var optionRef in customer.option_refs)
                        AddScopedCandidate("choose_dialogue_option", new ActionArguments { customer_ref = customerRef,
                            dialogue_ref = customer.dialogue_ref, option_ref = optionRef }, snapshot, result);
            }

            var containers = snapshot.business.details.containers;
            var front = containers?.Find(c => c != null && c.container == "front" && c.completeness == "complete");
            if (front?.item_refs != null && front.item_refs.Count > 0 && front.item_refs.Count <= 64 &&
                !string.IsNullOrEmpty(snapshot.business.details.front_collection_fingerprint))
                AddScopedCandidate("prepare_purchase_items", new ActionArguments { customer_ref = customerRef,
                    item_refs = new List<string>(front.item_refs),
                    collection_fingerprint = snapshot.business.details.front_collection_fingerprint }, snapshot, result);

            AddScopedCandidate("dismiss_customer", new ActionArguments { customer_ref = customerRef }, snapshot, result);
            AddScopedCandidate("open_trade_panel", new ActionArguments { customer_ref = customerRef }, snapshot, result);
        }

        private void AddScopedCandidate(string action, ActionArguments arguments, Snapshot snapshot, List<LegalAction> result)
        {
            if (!BusinessActionPolicy.IsEnabled(action)) return;
            NativeActionEvidence evidence;
            Command command;
            try
            {
                command = new Command { schema_version = 3, action = action, arguments = arguments };
                evidence = Check(command, snapshot);
            }
            catch { return; }
            var reason = !evidence.Captured || !evidence.SceneReady || !evidence.CapabilityScopeVerified
                ? evidence.Reason ?? "native_action_precondition" : BusinessActionPolicy.ValidateCandidate(command, snapshot);
            if (reason != null)
            {
                var candidate = snapshot.business.details.candidates.Find(c => c.action == action);
                if (candidate != null) { candidate.status = "blocked"; candidate.reason = reason; }
                return;
            }
            result.Add(new LegalAction { action = action, arguments = arguments });
        }

        // Probe invokes this only after its DTO admission policy. It recaptures native facts on
        // the Unity main thread immediately before dispatch; failures here occur before mutation.
        internal void Invoke(Command command, Snapshot before)
        {
            if (!BusinessActionPolicy.IsEnabled(command?.action))
                throw new NativeActionRejectedException("business_action_not_enabled");
            NativeActionEvidence evidence;
            try { evidence = Check(command, before); }
            catch { throw new NativeActionRejectedException("native_preflight_failed"); }
            string reason = BusinessActionPolicy.Validate(command, before, evidence);
            if (reason != null) throw new NativeActionRejectedException(reason);
            if (evidence.NativeTarget == null) throw new NativeActionRejectedException("native_target_missing");

            switch (command.action)
            {
                case "open_trade_panel":
                    // StoreClientMono.OnClick -> ToggleUI -> OpenUI; preflight proves closed.
                    // Call its open-only branch so this semantic action can never close a panel.
                    ((NegociationUIManager)evidence.NativeTarget).OpenUI();
                    return;
                case "start_business":
                    ((StoreShutterButton)evidence.NativeTarget).OnClick();
                    return;
                case "receive_next_customer":
                    ((StoreBell)evidence.NativeTarget).OnClick();
                    return;
                case "advance_dialogue":
                    ((DialogUIManager)evidence.NativeTarget).OnAdvancePressed();
                    return;
                case "choose_dialogue_option":
                    ((DialogChoiceBubble)evidence.NativeTarget).OnDialogChoiceClick();
                    return;
                case "prepare_purchase_items":
                    ((StoreTransferButton)evidence.NativeTarget).OnClick();
                    return;
                case "cancel_confirmation":
                    if (evidence.NativeTarget is GenericConfirmationUIManager generic)
                    { generic.CancelButtonPressed(); return; }
                    if (evidence.NativeTarget is ConfirmPanelUIManager confirm)
                    { confirm.OnNoClick(); return; }
                    throw new InvalidOperationException("confirmation_cancel_target_unverified");
                case "refuse_trade":
                    throw new InvalidOperationException("refuse_native_path_unsupported");
                case "dismiss_customer":
                    ((StoreDismissButton)evidence.NativeTarget).OnClick();
                    return;
                default:
                    throw new InvalidOperationException("unknown_action");
            }
        }

        internal VerificationDecision Verify(Command command, Snapshot before, Snapshot after)
        {
            var decision = BusinessActionPolicy.Verify(command, before, after);
            if (decision.outcome != "succeeded") return decision;
            var evidence = Check(command, after);
            switch (command.action)
            {
                case "open_trade_panel":
                    return evidence.CurrentCustomerMatches && evidence.CustomerArrived &&
                        BusinessCustomerReader.ReadTradePanel() == after.business.details.customer.trade_type
                        ? decision : VerificationDecision.Pending();
                case "start_business":
                    return evidence.StoreOpen && evidence.StoreLockReleased
                        ? decision : VerificationDecision.Pending();
                case "receive_next_customer":
                    return evidence.AnyCurrentCustomer && evidence.CustomerArrived && evidence.StoreOpen &&
                        (after.business?.details?.customer?.phase == "dialogue" ||
                         after.business?.details?.customer?.phase == "trading")
                        ? decision : VerificationDecision.Pending();
                case "dismiss_customer":
                    // The ordinary dismiss path may trigger the game's configured next-customer
                    // flow. The pure postcondition already proves the old customer ref was
                    // invalidated; a new current customer does not undo that result.
                    return decision;
                default:
                    return decision;
            }
        }

        private static bool ExactFrontSelection(Snapshot snapshot, List<string> refs)
        {
            if (snapshot?.business?.details?.containers == null || refs == null || refs.Count == 0) return false;
            ContainerState front = null, back = null;
            foreach (var container in snapshot.business.details.containers)
            {
                if (container == null) return false;
                if (container.container == "front") front = container;
                else if (container.container == "back") back = container;
            }
            if (front == null || back == null || front.completeness != "complete" || back.completeness != "complete" ||
                front.item_refs == null || back.item_refs == null || front.item_refs.Count != refs.Count ||
                front.item_refs.Count > 64 || back.item_refs.Count > 64 ||
                front.item_refs.Distinct(StringComparer.Ordinal).Count() != front.item_refs.Count ||
                back.item_refs.Distinct(StringComparer.Ordinal).Count() != back.item_refs.Count) return false;
            var requested = new HashSet<string>(refs, StringComparer.Ordinal);
            return requested.Count == refs.Count && requested.SetEquals(front.item_refs);
        }

        private static bool CompleteContainer(Snapshot snapshot, string name)
        {
            var containers = snapshot?.business?.details?.containers;
            if (containers == null) return false;
            var found = containers.Find(c => c != null && c.container == name);
            return found != null && found.completeness == "complete" && found.item_refs != null &&
                found.item_refs.Count <= 64 && found.item_refs.Distinct(StringComparer.Ordinal).Count() == found.item_refs.Count;
        }

        private static T FindStoreControl<T>(StoreUIManager ui) where T : UnityEngine.MonoBehaviour
        {
            T match = null;
            foreach (var child in ui.GetComponentsInChildren<T>(true))
            {
                if (!child.isActiveAndEnabled || !child.gameObject.activeInHierarchy) continue;
                if (match != null) return null;
                match = child;
            }
            if (match != null) return match;
            // World-space controls can be siblings of the UI manager. Resolve only this
            // fixed component type in the same active gameplay scene, never a named method.
            foreach (var control in UnityEngine.Object.FindObjectsOfType<T>())
            {
                if (control.gameObject.scene != ui.gameObject.scene || !control.isActiveAndEnabled || !control.gameObject.activeInHierarchy) continue;
                if (match != null) return null;
                match = control;
            }
            return match;
        }

        private static bool HasActionBlockingEvidence(List<string> blockers, string action)
        {
            if (blockers == null) return true;
            foreach (var blocker in blockers)
            {
                if (KnownUnobservableFinding(blocker)) continue;
                if ((action == "advance_dialogue" || action == "choose_dialogue_option") && blocker == "dialogue_active") continue;
                if ((action == "prepare_purchase_items" || action == "dismiss_customer") &&
                    blocker == "dialogue_active") continue;
                return true;
            }
            return false;
        }

        private static bool KnownUnobservableFinding(string blocker) => blocker == "global_ui_stack_unobservable" ||
            blocker == "global_raycast_order_unobservable" || blocker == "quote_rebuild_completion_unobservable" ||
            blocker == "tutorial_group_active_unverified" || blocker == "control_hints_active_unverified";

        private static bool IsSupportedTutorialMentorDialogue(StoreClient client) => client != null &&
            client.identifier == "mentor" && client.clientIntent == StoreClient.ClientIntent.DIALOGUE;

        // Normal failed bargaining changes intent to LEAVE before its visible final dialogue.
        // Permit only that acknowledged ordinary encounter's existing dialogue continuation.
        private static bool IsAcknowledgedBargainDeparture(StoreClient client, Snapshot snapshot) => client != null &&
            (int)client.clientIntent == 5 && snapshot.business.details.bargain?.result == "responding" &&
            snapshot.business.details.bargain.terms_revision > 0 && snapshot.business.details.bargain.last_option_ref != null &&
            (client.identifier == "scavGeneral" || client.identifier == "shadyPharma" || client.identifier == "food_scav_story" ||
             client.identifier == "spacerFood" || client.identifier == "spacerFoodStory");

        // Tutorial-specific callbacks below only advance/close the tutorial UI. Keep dismissal
        // permission tied to the exact current factory delegates; arbitrary customer callbacks
        // remain blocked, as do every tryInterceptDismiss hook.
        private static bool IsPermittedDismissCallback(StoreClient client)
        {
            if (client == null || client.tryInterceptDismiss != null) return false;
            if (client.OnDismiss == null) return true;
            var method = client.OnDismiss.Method;
            if (client.OnDismiss.GetInvocationList().Length != 1 || method == null || method.DeclaringType == null ||
                method.DeclaringType.FullName != "StoreClientListStory+<>c")
                return false;
            return (client.identifier == "food_scav_story" && method.Name == "<CreateFoodSellerStory>b__17_2") ||
                (client.identifier == "spacerFoodStory" && method.Name == "<CreateFoodBuyerStory>b__33_1");
        }

        private static bool IsTutorialFoodBuyerDismissal(StoreClient client) => client != null &&
            client.identifier == "spacerFoodStory" && client.clientIntent == StoreClient.ClientIntent.BUY &&
            client.tryInterceptDismiss == null && client.OnDismiss != null &&
            client.OnDismiss.GetInvocationList().Length == 1 &&
            client.OnDismiss.Method?.DeclaringType?.FullName == "StoreClientListStory+<>c" &&
            client.OnDismiss.Method.Name == "<CreateFoodBuyerStory>b__33_1";

        private static bool UnsafeVisibleModal(StoreUIManager ui, PlayerStore store)
        {
            if (ui == null || store == null) return true;
            if (ui.escapeUIManager != null && ui.escapeUIManager.menuPanel != null && ui.escapeUIManager.menuPanel.activeInHierarchy) return true;
            if (ui.gameOptionUIManager != null && ui.gameOptionUIManager.IsOpen) return true;
            if (ui.confirmPanelUIManager != null && ui.confirmPanelUIManager.panel != null && ui.confirmPanelUIManager.panel.activeInHierarchy) return true;
            if (ui.closeShutterConfirmationPanel != null && ui.closeShutterConfirmationPanel.activeInHierarchy) return true;
            if (ui.isShot || store.isHandlingShootout || store.isClientBeingArrested) return true;
            var generic = GenericConfirmationUIManager.Instance;
            return generic != null && generic.panel != null && generic.panel.activeInHierarchy;
        }

        private static bool HasExactlyOneConfirmationBlocker(List<string> blockers) => blockers != null && blockers.Count == 1 &&
            (blockers[0] == "confirmation" || blockers[0] == "generic_confirmation" ||
             blockers[0] == "shutter_confirmation" || blockers[0] == "trade_confirmation");

        private static bool KnownBlockingUi(StoreUIManager ui, PlayerStore store)
        {
            if (ui == null || store == null) return true;
            if (ui.escapeUIManager != null && ui.escapeUIManager.menuPanel != null && ui.escapeUIManager.menuPanel.activeInHierarchy) return true;
            if (ui.gameOptionUIManager != null && ui.gameOptionUIManager.IsOpen) return true;
            if (ui.confirmPanelUIManager != null && ui.confirmPanelUIManager.panel != null && ui.confirmPanelUIManager.panel.activeInHierarchy) return true;
            if (ui.closeShutterConfirmationPanel != null && ui.closeShutterConfirmationPanel.activeInHierarchy) return true;
            if (ui.isShot || store.isHandlingShootout || store.isClientBeingArrested) return true;
            var generic = GenericConfirmationUIManager.Instance;
            if (generic != null && generic.panel != null && generic.panel.activeInHierarchy) return true;
            var bargain = BargainUIManager.Instance;
            if (bargain != null && bargain.IsOpen) return true;
            return false;
        }
    }
}
