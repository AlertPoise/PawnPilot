#if M2A_READ_ONLY_PROBE
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PawnPilot
{
    // Local investigation only. No pricing, capacity, raycast, refresh or business calls.
    // Local geometric candidates need screenshot corroboration; not a player-visible API.
    // Geometry is a clipping filter, NOT a proof of topmost UI or transaction legality.
    internal static class VerificationDiagnosticReader
    {
        internal static VerificationDiagnosticInput Read()
        {
            var basic = TradeDiagnosticReader.Read();
            var entry = EmporiumEntry.Instance;
            var negotiation = NegociationUIManager.Instance;
            var dialogue = DialogUIManager.Instance;
            if (basic == null || entry == null || dialogue == null) return null;
            var input = new VerificationDiagnosticInput
            {
                Customer = basic.Customer, KnownBlocker = basic.KnownBlocker,
                TabletOpen = basic.TradeUiOpen, ConfirmationOpen = basic.ConfirmationOpen,
                DialogueActive = HasKnownDialogueUi(dialogue),
                WaitingForAdvance = basic.WaitingForAdvance,
                UnknownUi = true, // Global raycast/overlay coverage is deliberately not certified.
                Items = Array.Empty<VerificationItemInput>(), Rows = Array.Empty<VerificationRowInput>(),
                Footer = Array.Empty<string>()
            };
            if (input.KnownBlocker != null || input.ConfirmationOpen || input.DialogueActive || input.WaitingForAdvance)
                return input;

            var items = new List<VerificationItemInput>();
            ReadInventory(entry.frontInvWindow, entry.frontInvinvElement, "front", items);
            ReadInventory(entry.backInvWindow, entry.backInvinvElement, "back", items);
            ReadInventory(entry.invWindow, entry.invElement, "player", items);
            input.Items = items.ToArray();
            if (!input.TabletOpen || input.Customer == null || !basic.CustomerArrived) return input;
            var frame = negotiation.uiObject.GetComponent<NegotiationTabletFrame>();
            if (frame == null || frame.content == null || frame.footer == null || frame.scroll == null)
                throw new InvalidOperationException();
            var rows = new List<VerificationRowInput>();
            foreach (Transform child in frame.content)
            {
                if (!child.gameObject.activeInHierarchy || child.GetComponent<TabletListRow>() == null) continue;
                if (rows.Count >= 64) throw new InvalidOperationException();
                string host = child.name == "SelectedRow" ? "Texts/" : child.name == "Row" ? "Visuals/" : null;
                if (host == null) throw new InvalidOperationException();
                var name = child.Find(host + "Name");
                var price = child.Find(host + "Right");
                if (name == null || price == null) throw new InvalidOperationException();
                var nameText = name.GetComponent<TMP_Text>();
                var priceText = price.GetComponent<TMP_Text>();
                bool visible = VisibleUiReader.TextVisible(nameText, frame.scroll.viewport) &&
                    VisibleUiReader.TextVisible(priceText, frame.scroll.viewport);
                // Do not read text for clipped rows. No item binding is inferred from row order.
                rows.Add(new VerificationRowInput { FullyVisible = visible,
                    Name = visible ? DiagnosticRenderedText.Read(nameText) : "",
                    Price = visible ? DiagnosticRenderedText.Read(priceText) : "" });
            }
            var footer = new List<string>();
            foreach (Transform child in frame.footer)
            {
                if (!child.gameObject.activeInHierarchy || child.name != "FooterRow") continue;
                var label = child.Find("Label");
                var value = child.Find("Value");
                if (label == null || value == null) throw new InvalidOperationException();
                var left = label.GetComponent<TMP_Text>();
                var right = value.GetComponent<TMP_Text>();
                if (!VisibleUiReader.TextVisible(left) || !VisibleUiReader.TextVisible(right)) continue;
                if (footer.Count >= 8) throw new InvalidOperationException();
                footer.Add(DiagnosticRenderedText.Read(left) + " | " + DiagnosticRenderedText.Read(right));
            }
            input.Rows = rows.ToArray(); input.Footer = footer.ToArray();
            return input;
        }

        private static bool HasKnownDialogueUi(DialogUIManager dialogue)
        {
            // ScheduleFlow retains completed Coroutine handles until interruption.
            // Its list count (and the unused isConversationActiveFlag) is not UI liveness.
            // Inspect only existing roots, never dialogue text, choices or callbacks.
            if (dialogue.currentDialog != null || dialogue.isWaitingForManualAdvance ||
                Active(dialogue.clientNameBubble) || Active(dialogue.secNameBubble) ||
                (dialogue.clientBubble != null && Active(dialogue.clientBubble.gameObject)) ||
                (dialogue.playerBubble != null && Active(dialogue.playerBubble.gameObject)) ||
                (dialogue.secBubble != null && Active(dialogue.secBubble.gameObject))) return true;
            if (dialogue.dialogChoiceBubbles == null) throw new InvalidOperationException();
            foreach (var choice in dialogue.dialogChoiceBubbles)
                if (choice != null && Active(choice.gameObject)) return true;
            return false; // UnknownUi stays true: no global visibility or action guarantee.
        }

        private static bool Active(GameObject obj) => obj != null && obj.activeInHierarchy;

        private static void ReadInventory(PixelWindow window, GameGridInventory inventory, string location,
            List<VerificationItemInput> result)
        {
            if (window == null || inventory == null || !window.IsVisible() || !VisibleUiReader.RectVisible(window.rectTransform)) return;
            foreach (var item in inventory.childItems)
            {
                // No recursion into closed bags/hidden containers. Only rendered direct members.
                var element = item as GameItemElement;
                if (element == null || !ReferenceEquals(item.parentInventory, inventory) ||
                    !VisibleUiReader.RectVisible(element.rectTransform) ||
                    !VisibleUiReader.Contained(element.rectTransform, inventory.rectTransform)) continue;
                if (result.Count >= 64 || item.modifiedShape == null ||
                    item.modifiedShape.GetType() != typeof(GridShapeBuilder)) throw new InvalidOperationException();
                result.Add(new VerificationItemInput { Identity = item, Container = location,
                    Units = item.unitCount, Owned = GeneralHelper.IsItemOwned(item),
                    X = item.modifiedShape.minX, Y = item.modifiedShape.minY });
            }
        }

    }
}
#endif
