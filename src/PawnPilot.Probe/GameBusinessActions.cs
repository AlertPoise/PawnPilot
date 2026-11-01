using System;
using System.Collections.Generic;

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
                !snapshot.ready || snapshot.business?.details == null) return evidence;

            var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            var ui = StoreUIManager.Instance;
            var entry = EmporiumEntry.Instance;
            var dialog = DialogUIManager.Instance;
            if (store == null || ui == null || entry == null) return evidence;

            evidence.SceneReady = true;
            // Known roots are not complete scene coverage: CustomUI, phone/overlay ordering and
            // arbitrary confirmation/dialogue callbacks remain unresolved. This is deliberately false.
            evidence.UiCoverageVerified = false;
            evidence.OnlyTargetBlocker = HasExactlyOneConfirmationBlocker(snapshot.business.details.ui?.blockers);
            evidence.UiConsistent = snapshot.business.details.ui?.coverage == "verified" &&
                ((snapshot.business.details.ui.status == "consistent" && snapshot.interaction?.consistency == "consistent") ||
                 (snapshot.business.details.ui.status == "blocked" && snapshot.interaction?.consistency == "blocked" && evidence.OnlyTargetBlocker));
            evidence.NoBlockers = snapshot.business.details.ui?.blockers != null &&
                snapshot.business.details.ui.blockers.Count == 0 && !KnownBlocker(ui, store);
            evidence.NativeEntryVerified = true; // exact candidate method found in the current DLL
            evidence.CapabilityScopeVerified = false; // no complete live callback/scope review yet
            evidence.StoreClosed = store.storeState == PlayerStore.StoreState.MORNING;
            evidence.StoreOpen = store.storeState == PlayerStore.StoreState.OPEN;
            evidence.CanStartDay = store.CanStartDay();
            evidence.StoreLockReleased = store.lockShutterButtonFlag <= 0;
            evidence.NoLocks = store.lockShutterButtonFlag <= 0 && store.lockNextBellFlag <= 0 &&
                !store.isClientBeingArrested && !store.isHandlingShootout && !ui.isShot;
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
            evidence.OrdinaryScavGeneral = blueprint != null && blueprint.identifier == "scavGeneral" &&
                blueprint.clientIntent == StoreClient.ClientIntent.SELL;
            // Name/intent recognition is not enough to certify OnArrival, OnItemSold, OnDealAccepted,
            // start/end action delegates or dismissal interception. Keep the callback closure closed.
            evidence.CallbackScopeVerified = false;
            evidence.Dismissable = currentInstance != null && store.IsClientDimissable();
            evidence.DismissInterceptClear = blueprint != null && blueprint.tryInterceptDismiss == null;
            evidence.AutoCallNextDisabled = EmporiumOption.Instance != null && !EmporiumOption.Instance.autoCallNext;

            var current = dialog == null ? null : dialog.currentDialog;
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
                command.arguments?.collection_fingerprint == snapshot.business.collection_fingerprint;
            evidence.TransferScopeVerified = entry.frontInvWindow != null && entry.frontInvWindow.IsVisible() &&
                snapshot.business.details.containers.Find(c => c.container == "front")?.item_refs?.Count > 0 &&
                evidence.ItemSetMatches;
            // CapacityAgent's proof currently targets the final player inventory, not the back
            // counter used by this button. Do not reuse a proof with the wrong target/scope.
            evidence.TransferTargetCapacityVerified = false;

            // Local target discovery is limited to known active roots; no whole-scene object scan.
            switch (command.action)
            {
                case "start_business":
                    evidence.NativeTarget = StoreShutterButton.Instance;
                    break;
                case "receive_next_customer":
                    var bell = ui.GetComponentInChildren<StoreBell>(true);
                    evidence.NativeTarget = bell != null && bell.isActiveAndEnabled && bell.gameObject.activeInHierarchy
                        ? bell : null;
                    break;
                case "advance_dialogue":
                    evidence.NativeTarget = dialog;
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
                    evidence.NativeTarget = entry.frontInvWindow == null ? null :
                        entry.frontInvWindow.rectTransform.gameObject.GetComponentInChildren<StoreTransferButton>(true);
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
                    evidence.NativeTarget = ui.GetComponentInChildren<StoreDismissButton>(true);
                    break;
            }
            evidence.NativeTargetAvailable = evidence.NativeTarget != null;
            evidence.Reason = "native_scope_unverified";
            return evidence;
        }

        // Probe invokes this only after its pure admission policy. It recaptures all native facts
        // on the Unity main thread and keeps the disabled production gate before dispatch.
        internal void Invoke(Command command, Snapshot before)
        {
            if (!BusinessActionPolicy.ProductionDispatchEnabled)
                throw new InvalidOperationException("business_actions_disabled_pending_live_scope_review");
            var evidence = Check(command, before);
            string reason = BusinessActionPolicy.Validate(command, before, evidence);
            if (reason != null) throw new InvalidOperationException(reason);
            if (evidence.NativeTarget == null) throw new InvalidOperationException("native_target_missing");

            switch (command.action)
            {
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
                case "start_business":
                    return evidence.StoreOpen && evidence.StoreLockReleased
                        ? decision : VerificationDecision.Pending();
                case "receive_next_customer":
                    return evidence.AnyCurrentCustomer && evidence.CustomerArrived && evidence.MainDialogueReady
                        ? decision : VerificationDecision.Pending();
                case "dismiss_customer":
                    return evidence.NoCurrentCustomer && evidence.NoCallNextPending
                        ? decision : VerificationDecision.Pending();
                default:
                    return decision;
            }
        }

        private static bool ExactFrontSelection(Snapshot snapshot, List<string> refs)
        {
            if (snapshot?.business?.details?.containers == null || refs == null || refs.Count == 0) return false;
            ContainerState front = null, back = null, player = null;
            foreach (var container in snapshot.business.details.containers)
            {
                if (container == null || container.completeness != "complete" || container.item_refs == null) return false;
                if (container.container == "front") front = container;
                else if (container.container == "back") back = container;
                else if (container.container == "player") player = container;
            }
            if (front == null || back == null || player == null || front.item_refs.Count != refs.Count) return false;
            var requested = new HashSet<string>(refs, StringComparer.Ordinal);
            return requested.Count == refs.Count && requested.SetEquals(front.item_refs);
        }

        private static bool HasExactlyOneConfirmationBlocker(List<string> blockers) => blockers != null && blockers.Count == 1 &&
            (blockers[0] == "confirmation" || blockers[0] == "generic_confirmation" ||
             blockers[0] == "shutter_confirmation" || blockers[0] == "trade_confirmation");

        private static bool KnownBlocker(StoreUIManager ui, PlayerStore store)
        {
            if (ui == null || store == null) return true;
            if (ui.escapeUIManager == null || ui.gameOptionUIManager == null || ui.confirmPanelUIManager == null) return true;
            if (ui.escapeUIManager.menuPanel != null && ui.escapeUIManager.menuPanel.activeInHierarchy) return true;
            if (ui.gameOptionUIManager.IsOpen) return true;
            if (ui.confirmPanelUIManager.panel != null && ui.confirmPanelUIManager.panel.activeInHierarchy) return true;
            if (ui.closeShutterConfirmationPanel != null && ui.closeShutterConfirmationPanel.activeInHierarchy) return true;
            if (ui.isShot || store.isHandlingShootout || store.isClientBeingArrested) return true;
            var generic = GenericConfirmationUIManager.Instance;
            if (generic == null || generic.panel == null || generic.panel.activeInHierarchy) return true;
            var bargain = BargainUIManager.Instance;
            if (bargain == null || bargain.IsOpen) return true;
            return false;
        }
    }
}
