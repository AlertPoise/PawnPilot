using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PawnPilot
{
    // Unity主线程组件适配。签发的引用不含方法名/原生ID；重新捕获后才允许一次输入。
    internal sealed class NativeInteractionAdapter
    {
        private sealed class Binding
        {
            internal SurfaceControl Control;
            internal object Target;
            internal Dialogue Dialogue;
            internal StoreClientInstance Customer;
        }
        private readonly BusinessProjection projection;
        private readonly Action<string> markNativeReturned;
        private readonly Func<long> monotonicClock;
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>();
        private readonly string nonce = Guid.NewGuid().ToString("N");
        private string lastSignature, currentSurface, receiptId;
        private int generation, nullFrame = -1;
        private object nullCustomer;
        private bool receiptReturned;
        private object observedCustomer;
        private int observedDialogueEpoch = -1, drainedEpoch = -1;
        private bool sawDialogue;
        private Dialogue lastObservedDialogue;
        private Command heldCommand;
        private Interactable heldControl;
        private float heldUntil;
        private long heldDeadline;
        private long heldMonotonicDeadline;

        internal NativeInteractionAdapter(BusinessProjection projection, Action<string> markNativeReturned, Func<long> monotonicClock)
        { this.projection = projection; this.markNativeReturned = markNativeReturned; this.monotonicClock = monotonicClock; }

        internal SurfaceObservation Capture(Snapshot snapshot)
        {
            var surface = new SurfaceObservation { schema_version = 1, kind = "unknown", phase = "unknown",
                status = "unknown", completeness = "unavailable", reason = "interaction_not_ready", controls = new List<SurfaceControl>() };
            var targets = new List<Binding>();
            try
            {
                if (!snapshot.ready || !snapshot.loaded) return Bind(snapshot, surface, targets);
                if (!DialogueEffectCatalog.VersionVerified)
                { surface.reason = "interaction_build_unverified"; return Bind(snapshot, surface, targets); }
                var store = PlayerStore.Instance;
                var ui = StoreUIManager.Instance;
                var dialog = DialogUIManager.Instance;
                if (store == null || ui == null || dialog == null) return Bind(snapshot, surface, targets);
                surface.customer_ref = snapshot.business.customer_ref;
                surface.dialogue_ref = snapshot.business.details.customer.dialogue_ref;
                surface.text = snapshot.business.details.customer.dialogue_text;
                if (snapshot.menu_open || BargainUIManager.Instance?.IsOpen == true)
                {
                    surface.kind = "native_panel"; surface.phase = "completed"; surface.completeness = "complete";
                    surface.reason = snapshot.menu_open ? "escape_panel_existing_close_menu" : "bargain_panel_existing_actions";
                    return Bind(snapshot, surface, targets);
                }
                if (WildNetworkUI.IsOpen)
                {
                    surface.kind = "native_panel"; surface.phase = "unsupported"; surface.status = "unsupported";
                    surface.reason = "native_panel_effects_unverified"; return Bind(snapshot, surface, targets);
                }
                var customer = store.currentClientInstance;
                var current = dialog.currentDialog;
                var client = customer?.GetClientBlueprint();
                int epoch = (int)DialogueEffectCatalog.ReadField(dialog, "conversationEpoch");
                if (!ReferenceEquals(observedCustomer, customer))
                { observedCustomer = customer; sawDialogue = false; lastObservedDialogue = null; observedDialogueEpoch = epoch; drainedEpoch = -1; }
                if (current != null) { sawDialogue = true; lastObservedDialogue = current; observedDialogueEpoch = epoch; }
                bool autoPending = Flag(dialog, "isAutoAdvancePending");
                bool endPending = Flag(dialog, "isEndPending");
                bool displayPending = DialogueEffectCatalog.ReadField(dialog, "displayingUncompletedDialog") != null;
                int queued = PendingEndActions(dialog);
                bool typing = current != null && dialog.IsLineStillDisplaying(current);
                bool hasChoices = current?.choices != null && current.choices.Count > 0;
                bool hiddenBubbles = BubblesHidden(dialog);
                bool drained = current == null && !autoPending && !endPending && !displayPending && queued == 0 &&
                    !dialog.isWaitingForManualAdvance && !Flag(dialog, "isSkipCurrentDialog") &&
                    !Flag(dialog, "isSecMonologuePlaying") && !ui.isAboutToStartMainDialog && hiddenBubbles &&
                    sawDialogue && epoch != observedDialogueEpoch && lastObservedDialogue != null &&
                    lastObservedDialogue.nextDialogue == null && lastObservedDialogue.choices?.Count == 0 &&
                    (lastObservedDialogue.endAction == null || lastObservedDialogue.isEndActionExecuted);
                if (drainedEpoch != epoch) { drainedEpoch = epoch; nullFrame = -1; }
                if (!drained || !ReferenceEquals(nullCustomer, customer)) { nullCustomer = customer; nullFrame = drained ? Time.frameCount : -1; }
                if (drained && nullFrame < 0) nullFrame = Time.frameCount;
                bool stableComplete = drained && nullFrame >= 0 && Time.frameCount > nullFrame;

                var visibleChoices = new List<DialogChoiceBubble>();
                var choiceTexts = new List<string>();
                bool choiceComplete = !hasChoices || ReadChoices(dialog, visibleChoices, choiceTexts);
                var signals = new DialogueFlowSignals { Ready = true, HasCustomer = customer != null,
                    Arrived = store.isClientArrived, HasCurrentDialogue = current != null, IsTyping = typing,
                    WaitingForAdvance = dialog.isWaitingForManualAdvance || (endPending && !typing),
                    AutoAdvancePending = autoPending, EndPending = endPending, DisplayPending = displayPending,
                    ScheduledEndActions = queued, HasVisibleChoices = visibleChoices.Count > 0,
                    ChoicesComplete = choiceComplete, HasChoiceDefinition = hasChoices,
                    VisibleDialogue = surface.text != null || !hiddenBubbles,
                    OtherSurface = false, AboutToStart = ui.isAboutToStartMainDialog, CompletedStable = stableComplete };
                var lifecycle = DialogueLifecycle.Evaluate(signals);
                surface.kind = customer == null ? "none" : current == null ? "customer_completion" : "dialogue";
                surface.phase = lifecycle.phase; surface.status = lifecycle.status;
                surface.completeness = lifecycle.evidence_complete ? "complete" : "partial";
                surface.reason = lifecycle.reason;
                if (hasChoices && !choiceComplete)
                { surface.completeness = "partial"; surface.reason = "choice_set_incomplete"; }

                if (current != null && customer != null && client != null)
                {
                    if (typing || (!hasChoices && (dialog.isWaitingForManualAdvance || endPending)))
                    {
                        string contract = DialogueEffectCatalog.Inspect(dialog, client);
                        bool available = dialog.isActiveAndEnabled && surface.text != null && !Interactable.isPaused;
                        Add(targets, "advance_dialogue", typing ? "skip" : "next", null, surface.text,
                            available, contract, available ? ContractReason(contract) : "dialogue_control_unavailable", dialog, current, customer);
                    }
                    for (int i = 0; i < visibleChoices.Count; i++)
                    {
                        var bubble = visibleChoices[i];
                        var option = current.choices[bubble.index];
                        string contract = DialogueEffectCatalog.Inspect(dialog, client, option.Item2, option.Item3);
                        bool available = bubble.IsInteractable() && NativeControlVisible(bubble);
                        // 使用当前业务投影已经签发的选项引用；不完整列表绝不执行。
                        string optionRef = FindOptionRef(snapshot, bubble);
                        if (optionRef == null) available = false;
                        Add(targets, "choose_dialogue_option", null, optionRef, choiceTexts[i], available && choiceComplete,
                            contract, !choiceComplete ? "choice_set_incomplete" : !available ? "choice_control_unavailable" : ContractReason(contract), bubble, current, customer);
                    }
                }
                if (surface.phase == "completed" && customer != null && client != null)
                {
                    var button = FindDismiss(ui);
                    bool safeCallbacks = client.tryInterceptDismiss == null && client.OnDismiss == null;
                    bool cleanupSafe = DismissCleanupSafe(snapshot);
                    bool available = button != null && NativeControlVisible(button) && store.IsClientDimissable() &&
                        store.lockNextBellFlag <= 0 && !Interactable.isPaused && !ui.IsTyping() &&
                        !store.isClientBeingArrested && !store.isHandlingShootout && !ui.isShot;
                    Add(targets, "dismiss_customer", null, null, null, available,
                        safeCallbacks && cleanupSafe ? "customer_departure" : "unknown",
                        !safeCallbacks ? "dismiss_callback_unverified" : !cleanupSafe ? "dismiss_cleanup_unverified" :
                        !available ? "dismiss_control_unavailable" : "native_dismiss_verified",
                        button, null, customer);
                }
                return Bind(snapshot, surface, targets);
            }
            catch
            {
                surface.kind = "unknown"; surface.phase = "unknown"; surface.status = "unknown";
                surface.completeness = "unavailable"; surface.reason = "interaction_capture_failed";
                surface.controls.Clear();
                return Bind(snapshot, surface, new List<Binding>());
            }
        }

        private SurfaceObservation Bind(Snapshot snapshot, SurfaceObservation surface, List<Binding> targets)
        {
            var signature = new StringBuilder(snapshot.world_epoch).Append('|').Append(surface.customer_ref).Append('|')
                .Append(surface.dialogue_ref).Append('|').Append(surface.kind).Append('|').Append(surface.phase).Append('|')
                .Append(surface.completeness).Append('|').Append(surface.reason).Append('|').Append(surface.text);
            foreach (var binding in targets)
            {
                var c = binding.Control;
                signature.Append('|').Append(binding.Target == null ? 0 : RuntimeHelpers.GetHashCode(binding.Target))
                    .Append('|').Append(c.action).Append('|').Append(c.mode).Append('|').Append(c.option_ref)
                    .Append('|').Append(c.text).Append('|').Append(c.enabled).Append('|').Append(c.effect_contract).Append('|').Append(c.reason);
            }
            if (lastSignature != signature.ToString())
            { lastSignature = signature.ToString(); generation++; currentSurface = "surface-" + nonce + "-" + generation; }
            surface.surface_ref = currentSurface;
            bindings.Clear(); surface.controls.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                targets[i].Control.control_ref = "control-" + nonce + "-" + generation + "-" + i;
                // 禁用选项也必须可观察；旧业务投影不签发它的option_ref，故签发只读绑定。
                if (targets[i].Control.action == "choose_dialogue_option" && targets[i].Control.option_ref == null)
                    targets[i].Control.option_ref = "option-" + nonce + "-" + generation + "-" + i;
                surface.controls.Add(targets[i].Control);
                bindings.Add(targets[i].Control.control_ref, targets[i]);
            }
            return surface;
        }

        internal void PublishCandidates(Snapshot state)
        {
            if (state.surface == null) return;
            foreach (var control in state.surface.controls)
            {
                if (!control.enabled || control.effect_contract == "unknown") continue;
                var candidate = new LegalAction { action = control.action, arguments = new ActionArguments {
                    customer_ref = state.surface.customer_ref, surface_ref = state.surface.surface_ref,
                    control_ref = control.control_ref, dialogue_ref = control.action == "dismiss_customer" ? null : state.surface.dialogue_ref,
                    option_ref = control.option_ref, mode = control.mode } };
                if (InteractionPolicy.ValidateCandidate(state, candidate, false) != null) continue;
                // 同一原生输入不同时发布旧/新两条路径；未知新效果不扩大旧适配器范围。
                state.legal_actions.RemoveAll(x => x.action == candidate.action &&
                    (candidate.action != "choose_dialogue_option" || x.arguments.option_ref == candidate.arguments.option_ref));
                CandidateAdmission.TryAdd(state, state.legal_actions, candidate);
            }
        }

        internal static void FinalizeStatus(Snapshot state)
        {
            var s = state.surface;
            if (s == null) return;
            if (BusinessProjection.IsInspectionWaitingIn(state)) s.status = "awaiting_user";
            else if (state.legal_actions.Count > 0) s.status = "awaiting_agent";
            else if (s.phase == "typing" || s.phase == "progressing") s.status = "progressing";
            else if (s.phase == "absent") s.status = "idle";
            else if (s.phase == "unknown") s.status = "unknown";
            else s.status = "unsupported";
        }

        internal void Invoke(Command command, Snapshot current)
        {
            string reason = InteractionPolicy.ValidateCommand(command, current);
            if (reason != null) throw new NativeActionRejectedException(reason);
            if (receiptId == command.id) throw new InvalidOperationException("interaction_input_already_attempted");
            if (current.surface.surface_ref != currentSurface || !bindings.TryGetValue(command.arguments.control_ref, out var binding) ||
                binding.Control.action != command.action || !ReferenceEquals(PlayerStore.Instance.currentClientInstance, binding.Customer))
                throw new NativeActionRejectedException("native_surface_changed");
            var dialog = DialogUIManager.Instance;
            if (command.action != "dismiss_customer" && !ReferenceEquals(dialog?.currentDialog, binding.Dialogue))
                throw new NativeActionRejectedException("native_dialogue_changed");
            // Capture已重新审核可见性、回调及引用；此后不捕获安全拒绝异常，任何异常都是未知。
            receiptId = command.id; receiptReturned = false;
            switch (command.action)
            {
                case "advance_dialogue": ((DialogUIManager)binding.Target).OnAdvancePressed(); break;
                case "choose_dialogue_option": ((DialogChoiceBubble)binding.Target).OnDialogChoiceClick(); break;
                case "dismiss_customer":
                    var button = (StoreDismissButton)binding.Target;
                    var input = button.GetComponent<Interactable>();
                    // 沿原生pointer-down/up握手，尊重当前instant/hold选项，不直接跳过长按。
                    if (button.ShouldRequireHold())
                    {
                        heldCommand = command; heldControl = input;
                        heldUntil = Time.unscaledTime + button.HoldDuration;
                        heldDeadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + command.execution_timeout_ms;
                        heldMonotonicDeadline = monotonicClock() + Math.Max(0, command.execution_timeout_ms - 100);
                        input.OnPointerDown(new PointerEventData(EventSystem.current));
                        return;
                    }
                    input.OnPointerDown(new PointerEventData(EventSystem.current));
                    input.OnPointerUp(new PointerEventData(EventSystem.current));
                    break;
                default: throw new InvalidOperationException("unsupported_interaction_control");
            }
            receiptReturned = true;
            markNativeReturned(command.id);
        }

        internal VerificationDecision Verify(Command command, Snapshot before, Snapshot after)
        {
            if (heldCommand?.id == command.id) return VerificationDecision.Pending();
            var decision = InteractionPolicy.Verify(command, before, after, receiptId == command.id && receiptReturned);
            if (decision.outcome != "succeeded") return decision;
            var control = before.surface.controls.Find(x => x.control_ref == command.arguments.control_ref);
            if (control?.effect_contract == "document_gift")
            {
                foreach (var item in after.business.details.items.Where(x => !before.business.details.items.Any(y => y.item_ref == x.item_ref)))
                    if (!projection.TryResolveItem(after.world_epoch, after.business.customer_ref, "front", item.item_ref, out var identity) ||
                        !(identity is GameItem native) || native.itemTypes == null || !native.itemTypes.Contains("DOCUMENT"))
                        return VerificationDecision.Uncertain("unexpected_document_gift_type");
            }
            return decision;
        }

        // 仅继续已进入ActionGate的单一受限输入。刷新/核验失败先取消pointer，不留下延迟释放。
        internal void ContinueInput(Func<Snapshot> capture, Progress progress, string brokerEpoch)
        {
            if (heldCommand == null) return;
            try
            {
                if (progress?.id != heldCommand.id || brokerEpoch != heldCommand.broker_epoch ||
                    monotonicClock() >= heldMonotonicDeadline ||
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= Math.Min(heldDeadline,
                        progress.execution_deadline_unix_ms ?? heldDeadline) || heldControl == null)
                { CancelInput(); return; }
                var current = capture();
                if (InteractionPolicy.ValidateCommand(heldCommand, current) != null ||
                    !NativeControlVisible(heldControl.GetComponent<StoreDismissButton>()) ||
                    !(bool)DialogueEffectCatalog.ReadField(heldControl, "holdActive"))
                { CancelInput(); return; }
                if (Time.unscaledTime < heldUntil) return;
                var target = heldControl;
                var completedId = heldCommand.id;
                heldCommand = null; heldControl = null; // 释放至多一次，即使原生抛错。
                target.OnPointerUp(new PointerEventData(EventSystem.current));
                receiptReturned = true;
                markNativeReturned(completedId);
            }
            catch { CancelInput(); throw; }
        }

        internal void CancelInput()
        {
            var target = heldControl;
            heldControl = null; heldCommand = null;
            if (target != null)
            {
                target.OnPointerExit(new PointerEventData(EventSystem.current));
                // Exit先清isPointerOver；Up同步清holdActive且不会触发点击。
                target.OnPointerUp(new PointerEventData(EventSystem.current));
            }
        }

        private string FindOptionRef(Snapshot state, DialogChoiceBubble bubble)
        {
            foreach (string reference in state.business.details.customer.option_refs)
                if (projection.TryResolveOption(state.world_epoch, state.business.customer_ref, state.business.details.customer.dialogue_ref,
                    reference, out var identity) && ReferenceEquals(identity, bubble)) return reference;
            return null;
        }

        private static void Add(List<Binding> targets, string action, string mode, string option, string text,
            bool enabled, string contract, string reason, object target, Dialogue dialogue, StoreClientInstance customer)
        {
            if (targets.Count >= 16) throw new InvalidOperationException("surface_control_limit");
            targets.Add(new Binding { Target = target, Dialogue = dialogue, Customer = customer,
                Control = new SurfaceControl { action = action, mode = mode, option_ref = option, text = text,
                    enabled = enabled, effect_contract = contract, reason = reason,
                    effects = contract == "document_gift" ? new List<string> { "dialogue_transition", "inventory_transfer", "story_state" } :
                        contract == "customer_departure" ? new List<string> { "customer_departure", "story_state" } :
                        contract == "presentation" ? new List<string> { "presentation", "dialogue_transition" } : new List<string>() } });
        }

        private static string ContractReason(string contract) => contract == "unknown" ? "callback_effects_unverified" : "audited_native_input";
        private static bool Flag(object target, string name) => (bool)DialogueEffectCatalog.ReadField(target, name);
        private static int PendingEndActions(DialogUIManager manager)
        {
            var lines = DialogueEffectCatalog.ReadField(manager, "scheduledEndActionLines") as IEnumerable;
            if (lines == null) throw new InvalidOperationException("pending_lines_unavailable");
            int count = 0;
            foreach (var value in lines) if (value is Dialogue line && !line.isEndActionExecuted) count++;
            return count;
        }
        private static bool BubblesHidden(DialogUIManager manager) =>
            !new[] { manager.clientBubble, manager.playerBubble, manager.secBubble }.Any(x => x != null && x.gameObject.activeInHierarchy) &&
            manager.dialogChoiceBubbles != null && !manager.dialogChoiceBubbles.Any(x => x != null && x.gameObject.activeInHierarchy);

        private static bool ReadChoices(DialogUIManager dialog, List<DialogChoiceBubble> options, List<string> texts)
        {
            if (dialog.currentDialog?.choices == null || dialog.currentDialog.choices.Count > 16 ||
                dialog.dialogChoiceBubbles == null || dialog.dialogChoiceBubbles.Count > 64) return false;
            var indices = new HashSet<int>(); bool complete = true;
            foreach (var bubble in dialog.dialogChoiceBubbles)
            {
                if (bubble == null || !bubble.gameObject.activeInHierarchy) continue;
                if (bubble.type != 0 || bubble.index < 0 || bubble.index >= dialog.currentDialog.choices.Count || !indices.Add(bubble.index))
                { complete = false; continue; }
                string text = VisibleUiReader.RenderedText(bubble.TMP);
                if (string.IsNullOrWhiteSpace(text) || !VisibleUiReader.RectVisible(bubble.GetComponent<RectTransform>()))
                { complete = false; continue; }
                options.Add(bubble); texts.Add(text);
            }
            // 容量/裁切/延迟缺项不可伪装完整；隐藏predicate不调用、不猜测其结果。
            return complete && indices.Count == dialog.currentDialog.choices.Count && options.Count == indices.Count;
        }

        private static StoreDismissButton FindDismiss(StoreUIManager ui)
        {
            var candidates = UnityEngine.Object.FindObjectsOfType<StoreDismissButton>()
                .Where(x => x.gameObject.scene == ui.gameObject.scene && x.isActiveAndEnabled && x.gameObject.activeInHierarchy).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }

        private static bool DismissCleanupSafe(Snapshot snapshot)
        {
            var details = snapshot.business.details;
            foreach (string container in new[] { "front", "back", "player" })
            {
                var view = details.containers.Find(x => x.container == container);
                if (view == null || view.completeness != "complete") return false;
                if (container != "player" && view.item_refs.Any(reference =>
                    details.items.Find(item => item.item_ref == reference)?.owned != true)) return false;
            }
            // 原生收尾会清除所有容器/嵌套物品的临时feature；只读预检，不序列化隐藏内容。
            var items = EmporiumEntry.Instance?.GetAllItems();
            return items != null && items.All(item => item != null && item.itemFeatures != null &&
                item.itemFeatures.All(feature => feature != null && (int)feature.featureType != 5 && (int)feature.featureType != 6));
        }

        // 固定组件的只读射线命中证明；不接收Agent坐标，也不发鼠标输入。
        private static bool NativeControlVisible(Component target)
        {
            if (target == null || !target.gameObject.activeInHierarchy || EventSystem.current == null) return false;
            var nativeControl = target.GetComponent<Interactable>();
            if (target is StoreDismissButton && (nativeControl == null || !nativeControl.isActiveAndEnabled ||
                !nativeControl.isAvailable || nativeControl.isControlable || Interactable.isPaused)) return false;
            Vector2 point;
            var rect = target.GetComponent<RectTransform>();
            if (rect != null)
            {
                if (!VisibleUiReader.RectVisible(rect)) return false;
                foreach (var group in rect.GetComponentsInParent<CanvasGroup>(false))
                    if (!group.interactable || !group.blocksRaycasts) return false;
                var canvas = rect.GetComponentInParent<Canvas>();
                point = RectTransformUtility.WorldToScreenPoint(canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera,
                    rect.TransformPoint(rect.rect.center));
            }
            else
            {
                var control = target.GetComponent<Interactable>();
                var renderer = target.GetComponentInChildren<Renderer>();
                var camera = Camera.main;
                if (control == null || !control.isActiveAndEnabled || !control.isAvailable || control.isControlable ||
                    renderer == null || !renderer.enabled || camera == null) return false;
                var screen = camera.WorldToScreenPoint(renderer.bounds.center);
                if (screen.z <= 0 || screen.x < 0 || screen.x > Screen.width || screen.y < 0 || screen.y > Screen.height) return false;
                point = screen;
            }
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            if (hits.Count == 0 || hits[0].gameObject == null) return false;
            var hit = hits[0].gameObject.transform;
            if (!(hit == target.transform || hit.IsChildOf(target.transform))) return false;
            if (target is StoreDismissButton)
                return ExecuteEvents.GetEventHandler<IPointerDownHandler>(hits[0].gameObject) == nativeControl.gameObject;
            return true;
        }
    }
}
