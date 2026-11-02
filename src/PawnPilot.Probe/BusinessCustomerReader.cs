using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace PawnPilot
{
    // Main-thread, read-only projection input. It intentionally carries only opaque object
    // references and state needed by the pure BusinessProjection; raw game objects never serialize.
    internal static class BusinessCustomerReader
    {
        private static readonly System.Reflection.FieldInfo PanelMode = typeof(NegociationUIManager)
            .GetField("mode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // Read the built UI mode, never invoke ResolveMode or refresh from observation.
        internal static string ReadTradePanel()
        {
            var panel = NegociationUIManager.Instance;
            if (panel?.uiObject == null) return "unavailable";
            if (!panel.uiObject.activeSelf) return "closed";
            if (!panel.uiObject.activeInHierarchy) return "unavailable";
            try
            {
                string mode = PanelMode?.GetValue(panel)?.ToString();
                return mode == "Buying" ? "buy" : mode == "Selling" ? "sell" : "unavailable";
            }
            catch { return "unavailable"; }
        }

        internal static BusinessInput Read()
        {
            var input = new BusinessInput();
            if (!PlayerStore.HasInstance) return input;
            var store = PlayerStore.Instance;
            var ui = StoreUIManager.Instance;
            if (store == null || ui == null) return input;

            var instance = store.currentClientInstance;
            var customer = input.Customer;
            if (instance == null)
            {
                customer.Presence = "absent";
                customer.Phase = ui.clientGameObject != null && ui.clientGameObject.gameObject.activeInHierarchy
                    ? "leaving" : "absent";
                customer.Arrived = null;
                customer.TradeType = "unknown";
                customer.DialogueStage = "absent";
                return input;
            }

            customer.Identity = instance;
            customer.Presence = "present";
            customer.Arrived = store.isClientArrived;
            customer.Phase = !store.isClientArrived ? "arriving" : "unknown";

            // Blueprint access is limited to these direct data fields. The blueprint object,
            // callbacks, identifier, inventory, and dialog graph are never returned or serialized.
            var blueprint = instance.GetClientBlueprint();
            if (blueprint != null)
            {
                customer.TradeType = blueprint.clientIntent == StoreClient.ClientIntent.SELL ? "sell" :
                    blueprint.clientIntent == StoreClient.ClientIntent.BUY ? "buy" : "other";
                if (customer.TradeType == "sell") customer.MultiBuyDisabled = blueprint.isMultiBuyDisabled;
            }

            var dialog = DialogUIManager.Instance;
            var negotiation = NegociationUIManager.Instance;
            customer.TradePanel = ReadTradePanel();
            if (customer.TradeType == "buy" && store.isClientArrived &&
                negotiation?.uiObject != null && negotiation.uiObject.activeInHierarchy)
            {
                var frame = negotiation.uiObject.GetComponent<NegotiationTabletFrame>();
                if (frame?.infoHost != null)
                {
                    var visibleInterests = new List<string>();
                    int visited = 0;
                    foreach (Transform child in frame.infoHost)
                    {
                        if (++visited > 32) { visibleInterests.Clear(); break; }
                        if (!child.gameObject.activeInHierarchy || child.name != "Tag") continue;
                        var label = child.Find("Text")?.GetComponent<TMPro.TMP_Text>();
                        if (!VisibleUiReader.TextVisible(label)) continue;
                        string text = VisibleUiReader.RenderedText(label);
                        if (!string.IsNullOrWhiteSpace(text)) visibleInterests.Add(text);
                    }
                    string request = string.Join(", ", visibleInterests);
                    if (request.Length > 0 && request.Length <= 256) customer.RequestText = request;
                }
            }
            if (dialog == null) return input;
            var current = dialog.currentDialog;
            if (current == null)
            {
                customer.DialogueText = null;
                customer.IsTyping = null;
                customer.WaitingForAdvance = null;
                customer.OptionIdentities = Array.Empty<object>();
                customer.OptionTexts = Array.Empty<string>();
                if (store.isClientArrived && negotiation != null && negotiation.uiObject != null &&
                    negotiation.uiObject.activeInHierarchy)
                    customer.Phase = "trading";
                else customer.DialogueStage = "updating";
                if (store.isClientArrived && customer.Phase != "trading") customer.Phase = "unknown";
                return input;
            }

            customer.Identity = instance;
            customer.DialogueIdentity = current;
            customer.Phase = "dialogue";
            customer.IsTyping = dialog.IsLineStillDisplaying(current);
            customer.WaitingForAdvance = dialog.isWaitingForManualAdvance;
            customer.DialogueText = ReadCurrentVisibleDialogue(dialog);

            // These methods are direct comparisons/field reads in the reviewed IL. Do not read
            // Dialogue.plainText/text or invoke any action callback to infer display state.
            if (dialog.IsChoice())
            {
                if (TryReadVisibleMainOptions(dialog, out var visibleOptions, out var visibleTexts))
                {
                    customer.OptionIdentities = visibleOptions.ToArray();
                    customer.OptionTexts = visibleTexts.ToArray();
                    customer.DialogueStage = "choices";
                }
                else
                {
                    customer.OptionIdentities = Array.Empty<object>();
                    customer.OptionTexts = Array.Empty<string>();
                    customer.DialogueStage = "updating";
                }
            }
            else if (customer.IsTyping == true)
            {
                customer.DialogueStage = "typing";
            }
            else if (customer.WaitingForAdvance == true)
            {
                customer.DialogueStage = "waiting";
            }
            else
            {
                customer.DialogueStage = "updating";
            }
            return input;
        }

        private static bool TryReadVisibleMainOptions(DialogUIManager dialog, out List<object> result, out List<string> texts)
        {
            result = new List<object>();
            texts = new List<string>();
            if (dialog == null || dialog.dialogChoiceBubbles == null || dialog.dialogChoiceBubbles.Count == 0 ||
                dialog.dialogChoiceBubbles.Count > 64) return false;
            foreach (var bubble in dialog.dialogChoiceBubbles)
            {
                if (bubble == null || bubble.type != 0 || !bubble.gameObject.activeInHierarchy) continue;
                var rect = bubble.GetComponent<RectTransform>();
                if (!bubble.IsInteractable() || rect == null || bubble.TMP == null ||
                    !VisibleUiReader.RectVisible(rect) || !VisibleUiReader.TextVisible(bubble.TMP, rect)) continue;
                string visibleText = ReadRenderedText(bubble.TMP, allowPartial: false);
                if (string.IsNullOrWhiteSpace(visibleText) || visibleText.Length > 256)
                {
                    result.Clear();
                    texts.Clear();
                    return false;
                }
                result.Add(bubble);
                texts.Add(visibleText);
                if (result.Count > 16) { result.Clear(); texts.Clear(); return false; }
            }
            return result.Count > 0;
        }

        private static string ReadCurrentVisibleDialogue(DialogUIManager dialog)
        {
            if (dialog == null) return null;
            var candidates = new[] { dialog.clientBubble, dialog.playerBubble, dialog.secBubble };
            string visible = null;
            foreach (var bubble in candidates)
            {
                if (bubble == null || bubble.textfield == null || !bubble.gameObject.activeInHierarchy) continue;
                var rect = bubble.textfield.rectTransform;
                // 打字中仅读已经生成并显示的前缀；完整文本校验会把typing误判为无文本。
                if (rect == null || !VisibleUiReader.RectVisible(rect) || !bubble.textfield.isActiveAndEnabled ||
                    bubble.textfield.color.a <= 0) continue;
                if (visible != null) return null; // Ambiguous active speaker; never guess which line to expose.
                visible = ReadRenderedText(bubble.textfield, allowPartial: true);
            }
            return visible;
        }

        // Reads TMP's already-generated character buffer, not Dialogue.text/plainText. This
        // respects maxVisibleCharacters during typewriter animation and never exports a future line.
        private static string ReadRenderedText(TMPro.TMP_Text text, bool allowPartial)
        {
            if (text == null || text.textInfo == null || text.havePropertiesChanged ||
                text.textInfo.characterInfo == null || text.textInfo.pageCount > 1 || text.pageToDisplay != 1 ||
                text.isTextOverflowing || text.isTextTruncated) return null;
            int count = text.textInfo.characterCount;
            if (count <= 0) return null;
            int visibleCount = text.maxVisibleCharacters < 0 ? count : Math.Min(count, text.maxVisibleCharacters);
            if (!allowPartial && visibleCount < count) return null;
            if (visibleCount <= 0) return null;
            var builder = new StringBuilder(Math.Min(visibleCount, 256));
            var characters = text.textInfo.characterInfo;
            for (int i = 0; i < visibleCount && i < characters.Length; i++)
            {
                if (builder.Length >= 256) return null;
                if (characters[i].pageNumber != 0 || characters[i].lineNumber >= text.maxVisibleLines ||
                    (!char.IsWhiteSpace(characters[i].character) && (!characters[i].isVisible || characters[i].color.a == 0))) return null;
                builder.Append(characters[i].character);
            }
            var value = builder.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
