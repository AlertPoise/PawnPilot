using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PawnPilot
{
    // Adapter for the existing native bargaining UI. No RNG, budget, hidden feature,
    // offer-chance getter or strategy is consulted. Native buttons own the business logic.
    internal sealed class GameBargainActions
    {
        private static readonly FieldInfo Groups = typeof(BargainUIManager).GetField("groups", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TrackedClient = typeof(BargainUIManager).GetField("trackedClient", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RepLine = typeof(BargainUIManager).GetField("repLine", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Selected = typeof(TabletButton).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TooltipOwner = typeof(TooltipManager).GetField("tooltipOwner", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly BusinessProjection projection;
        private string world, lastOptionRef, lastDirection, result = "none", resultText, beforeCollection;
        private object encounter;
        private int termsRevision;
        private int? beforeAmount;
        private bool acknowledged;

        internal GameBargainActions(BusinessProjection projection) { this.projection = projection; }
        internal static bool Handles(string action) => action == "open_bargain_panel" ||
            action == "choose_bargain_option" || action == "close_bargain_panel";

        internal BusinessInput Read(string worldEpoch)
        {
            var input = BusinessReader.Read();
            if (world != worldEpoch || (input.Customer.Identity != null && !ReferenceEquals(encounter, input.Customer.Identity)))
            {
                world = worldEpoch; encounter = input.Customer.Identity;
                termsRevision = 0; lastOptionRef = null; lastDirection = null; result = "none"; resultText = null;
                beforeAmount = null; beforeCollection = null; acknowledged = false;
            }
            var state = new BusinessBargainInput { TermsRevision = termsRevision, LastOptionRef = lastOptionRef,
                Result = result, ResultText = resultText, Direction = acknowledged ? lastDirection : Direction(input.Customer.TradeType) };
            input.Bargain = state;
            try
            {
                var manager = BargainUIManager.Instance;
                bool open = manager != null && manager.uiObject != null && manager.uiObject.activeInHierarchy;
                bool dialogue = input.Customer.DialogueIdentity != null || StoreUIManager.Instance?.isAboutToStartMainDialog == true;
                bool ordinary = OrdinaryCurrent();
                state.Phase = open ? (dialogue ? "responding" : "choosing") : "closed";
                if (input.Customer.Presence != "present") state.Phase = "unavailable";
                state.Available = ordinary && !dialogue && EntryReady();
                if (open && ordinary && ReferenceEquals(TrackedClient?.GetValue(manager), CurrentClient()))
                {
                    state.Options = ReadOptions(manager, state.Direction, dialogue).ToArray();
                    state.Available = state.Options.Any(option => option.Enabled && !option.Selected);
                    state.ReputationText = VisibleText(RepLine?.GetValue(manager) as TMP_Text);
                    var tooltip = TooltipManager._instance;
                    var owner = tooltip == null ? null : TooltipOwner?.GetValue(tooltip) as Component;
                    if (owner != null && state.Options.Any(option => ReferenceEquals(option.Identity, owner.GetComponent<TabletButton>())))
                        state.HintText = VisibleText(tooltip.content);
                }
                if (acknowledged)
                {
                    if (result == "responding" && !string.IsNullOrWhiteSpace(input.Customer.DialogueText)) resultText = input.Customer.DialogueText;
                    if (result == "responding" && input.Customer.Presence == "absent") result = "customer_left";
                    else if (!dialogue && input.Quote.DisplayedTotal.HasValue && beforeAmount.HasValue &&
                        Collection(input) == beforeCollection && result != "customer_left")
                        result = input.Quote.DisplayedTotal.Value == beforeAmount.Value ? "unchanged" : "price_changed";
                    state.Result = result; state.ResultText = resultText;
                }
            }
            catch
            {
                state.Available = false; state.Phase = "unavailable";
                state.Options = Array.Empty<BusinessBargainOptionInput>();
                state.ReputationText = null; state.HintText = null;
            }
            return input;
        }

        private static IEnumerable<BusinessBargainOptionInput> ReadOptions(BargainUIManager manager, string direction, bool dialogue)
        {
            var frame = manager.uiObject.GetComponent<BargainPanelFrame>();
            var groups = Groups?.GetValue(manager) as IEnumerable;
            if (frame?.groupsScroll?.viewport == null || groups == null) yield break;
            int inspected = 0;
            foreach (object group in groups)
            {
                if (++inspected > 6 || group == null) yield break;
                Type type = group.GetType();
                var root = type.GetField("root")?.GetValue(group) as GameObject;
                object nativeType = type.GetField("type")?.GetValue(group);
                if (root == null || !root.activeInHierarchy || nativeType == null) continue;
                int kind = (int)(BargainType)nativeType;
                if ((direction == "buy" && kind != 0 && kind != 1) ||
                    (direction == "sell" && kind != 2 && kind != 3) || direction == "unknown") continue;
                var buttons = type.GetField("buttons")?.GetValue(group) as IList;
                var percents = type.GetField("percents")?.GetValue(group) as IList;
                if (buttons == null || percents == null || buttons.Count != percents.Count || buttons.Count > 4) continue;
                string header = VisibleText(root.transform.Find("Header")?.GetComponent<TMP_Text>());
                for (int i = 0; i < buttons.Count; i++)
                {
                    var button = buttons[i] as TabletButton;
                    if (button?.button == null || !button.gameObject.activeInHierarchy ||
                        !VisibleUiReader.TextVisible(button.label, frame.groupsScroll.viewport)) continue;
                    string label = VisibleUiReader.RenderedText(button.label);
                    int percent = (int)percents[i];
                    if (string.IsNullOrWhiteSpace(label) || percent <= 0 || percent > 100) continue;
                    // The percentage comes from this actually built button, not the global table.
                    string text = string.IsNullOrEmpty(header) ? label : header + " " + label;
                    if (text.Length > 256) continue;
                    yield return new BusinessBargainOptionInput { Identity = button, Text = text,
                        Kind = kind == 0 ? "lower_sale_price" : kind == 1 ? "raise_sale_price" :
                            kind == 2 ? "raise_purchase_price" : "lower_purchase_price",
                        PriceChangePercent = (kind == 0 || kind == 3) ? -percent : percent,
                        Enabled = !dialogue && button.button.isActiveAndEnabled && button.button.IsInteractable(),
                        Selected = IsSelected(button) };
                }
            }
        }

        internal List<LegalAction> BuildLegalActions(Snapshot snapshot)
        {
            var actions = new List<LegalAction>();
            var state = snapshot.business?.details?.bargain;
            if (state == null) return actions;
            Add("open_bargain_panel", null);
            Add("close_bargain_panel", null);
            foreach (var option in state.options) if (option.enabled && !option.selected) Add("choose_bargain_option", option.option_ref);
            return actions;
            void Add(string action, string optionRef)
            {
                var args = new ActionArguments { customer_ref = snapshot.business.customer_ref, mode = state.direction, option_ref = optionRef };
                var command = new Command { schema_version = 3, action = action, arguments = args };
                string reason = Check(command, snapshot, out _, candidate: true);
                snapshot.business.details.candidates.Add(new BusinessCandidate { action = action,
                    status = reason == null ? "candidate" : "blocked", reason = reason ?? "native_preflight_verified" });
                if (reason == null) CandidateAdmission.TryAdd(snapshot, actions, new LegalAction { action = action, arguments = args });
            }
        }

        private string Check(Command command, Snapshot snapshot, out TabletButton target, bool candidate = false)
        {
            target = null;
            string rejected = candidate ? BusinessActionPolicy.ValidateCandidate(command, snapshot) : BusinessActionPolicy.Validate(command, snapshot);
            if (rejected != null) return rejected;
            var store = PlayerStore.Instance;
            bool returningFromAcknowledgedResponse = command.action == "close_bargain_panel" && acknowledged &&
                ReferenceEquals(encounter, store?.currentClientInstance) && command.arguments.mode == lastDirection;
            if (!projection.TryResolveCustomer(snapshot.world_epoch, command.arguments.customer_ref, out var identity) ||
                !ReferenceEquals(identity, store?.currentClientInstance) || (!OrdinaryCurrent() && !returningFromAcknowledgedResponse) ||
                store == null || !store.isClientArrived || !EntryReady() || NegociationUIManager.IsTradeLocked())
                return "bargain_customer_or_entry_changed";
            var ui = StoreUIManager.Instance;
            var actor = ui?.clientGameObject;
            if (actor == null || actor.IsMoving() || ui.isAboutToStartMainDialog) return "customer_arriving";
            var manager = BargainUIManager.Instance;
            if (manager?.uiObject == null) return "bargain_panel_unavailable";
            bool open = manager.uiObject.activeInHierarchy;
            if (command.action == "open_bargain_panel")
                return !open && DialogUIManager.Instance?.currentDialog == null ? null : "bargain_panel_or_dialogue_changed";
            if (!open || !ReferenceEquals(TrackedClient?.GetValue(manager), CurrentClient())) return "bargain_panel_changed";
            if (command.action == "close_bargain_panel") return null;
            if (DialogUIManager.Instance == null || DialogUIManager.Instance.currentDialog != null || ui.IsTyping())
                return "dialogue_in_progress";
            if (!projection.TryResolveBargainOption(snapshot.world_epoch, command.arguments.customer_ref,
                command.arguments.option_ref, out var optionIdentity)) return "bargain_option_expired";
            target = optionIdentity as TabletButton;
            var resolvedTarget = target;
            return target != null && ReadOptions(manager, command.arguments.mode, false)
                .Any(option => ReferenceEquals(option.Identity, resolvedTarget) && option.Enabled && !option.Selected)
                ? null : "bargain_option_unavailable";
        }

        internal void Invoke(Command command, Snapshot before)
        {
            string reason = Check(command, before, out var target);
            if (reason != null) throw new NativeActionRejectedException(reason);
            var manager = BargainUIManager.Instance;
            if (command.action == "open_bargain_panel" || command.action == "close_bargain_panel")
            {
                // Same native button for open/return; the current-state checks above prove
                // the requested direction immediately before its one-shot callback.
                NegociationUIManager.Instance.uiObject.GetComponent<NegotiationTabletFrame>().bargainButton.onClick.Invoke();
                return;
            }
            // The existing ActionDriver owns command consumption and never replays this callback.
            beforeAmount = before.business.details.quote.displayed_total;
            beforeCollection = Collection(BusinessReader.Read());
            target.button.onClick.Invoke();
            // RefreshUI synchronously sets the visible selected marker even on refusal.
            // It acknowledges submission only; acceptance is left to visible dialogue/prices.
            if (IsSelected(target) && ReferenceEquals(TrackedClient?.GetValue(manager), CurrentClient()) &&
                ReferenceEquals(encounter, PlayerStore.Instance.currentClientInstance))
            {
                termsRevision++; lastOptionRef = command.arguments.option_ref; lastDirection = command.arguments.mode;
                result = "responding"; resultText = null; acknowledged = true;
            }
        }

        internal VerificationDecision Verify(Command command, Snapshot before, Snapshot after) =>
            BusinessActionPolicy.Verify(command, before, after);

        private static bool EntryReady()
        {
            var negotiation = NegociationUIManager.Instance;
            if (negotiation?.uiObject == null || !negotiation.uiObject.activeInHierarchy) return false;
            var button = negotiation.uiObject.GetComponent<NegotiationTabletFrame>()?.bargainButton;
            return button != null && button.isActiveAndEnabled && button.gameObject.activeInHierarchy && button.IsInteractable();
        }
        private static StoreClient CurrentClient() => PlayerStore.Instance?.currentClientInstance?.GetClientBlueprint();
        private static bool OrdinaryCurrent() => GameBusinessActions.IsSupportedOrdinarySeller(CurrentClient()) ||
            GameSaleActions.IsSupportedOrdinaryBuyer(CurrentClient());
        private static string Direction(string direction) => direction == "buy" || direction == "sell" ? direction : "unknown";
        private static bool IsSelected(TabletButton button) => button != null && Selected?.GetValue(button) is bool selected && selected;
        private static string VisibleText(TMP_Text text)
        {
            string value = VisibleUiReader.TextVisible(text) ? VisibleUiReader.RenderedText(text) : null;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        private static string Collection(BusinessInput input)
        {
            var back = input.Containers.FirstOrDefault(container => container.Container == "back");
            if (back?.Completeness != "complete") return null;
            return string.Join("|", back.Items.Select(item => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item.Identity) + ":" + item.Units + ":" + item.Owned));
        }
    }
}
