#if M2A_READ_ONLY_PROBE
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace PawnPilot
{
    // Reviewed against Assembly-CSharp SHA256 6398d44a...084e3b5. Main thread only.
    // Deliberately excludes pricing helpers, blueprints, hidden inventory and raw dialogue.
    internal static class TradeDiagnosticReader
    {
        internal static TradeDiagnosticInput Read()
        {
            if (!PlayerStore.HasInstance) return null;
            var store = PlayerStore.Instance;
            var ui = StoreUIManager.Instance;
            var negotiation = NegociationUIManager.Instance;
            var dialogue = DialogUIManager.Instance;
            var entry = EmporiumEntry.Instance;
            if (store == null || ui == null || entry == null) return null;
            var input = new TradeDiagnosticInput
            {
                Customer = store.currentClientInstance,
                CustomerArrived = store.isClientArrived,
                StorePhase = store.storeState.ToString(),
                TradeUiOpen = negotiation != null && Active(negotiation.uiObject),
                WaitingForAdvance = dialogue != null && dialogue.isWaitingForManualAdvance,
                KnownBlocker = KnownBlocker(ui, store)
            };
            if (!input.TradeUiOpen || input.KnownBlocker != null ||
                input.Customer == null || !input.CustomerArrived) return input;

            var frame = negotiation.uiObject.GetComponent<NegotiationTabletFrame>();
            if (frame == null) return input;
            input.Header = Text(frame.clientName);
            input.Subtitle = Text(frame.clientSubtitle);
            input.Section = Text(frame.sectionHeader);
            input.ConfirmationOpen = Active(frame.confirmRoot);
            input.ConfirmationText = input.ConfirmationOpen ? Text(frame.confirmText) : null;
            // Fixed footer only: do not enumerate the scroll list, off-screen rows,
            // tooltip builders or full TMP text behind a typewriter.
            var host = input.ConfirmationOpen ? frame.confirmButtonHost : frame.buttonHost;
            var labels = new List<string>();
            if (host != null)
                foreach (var button in host.GetComponentsInChildren<TabletButton>(false))
                {
                    var label = Text(button.label);
                    if (label != null) labels.Add(label);
                    if (labels.Count == 4) break;
                }
            input.FooterLabels = labels.ToArray();
            var item = negotiation.currentNegociatedItem;
            // This is the negotiation target, NOT the private selected row. It can
            // persist when a row collapses; clients must not treat it as an action target.
            if (item != null && entry.backInvinvElement != null &&
                ReferenceEquals(item.parentInventory, entry.backInvinvElement))
            {
                input.Target = item;
                input.TargetUnits = item.unitCount;
            }
            return input;
        }

        private static bool Active(GameObject obj) => obj != null && obj.activeInHierarchy;
        private static string Text(TMP_Text text)
        {
            if (text == null || !text.isActiveAndEnabled || !Active(text.gameObject) || text.color.a <= 0) return null;
            foreach (var group in text.GetComponentsInParent<CanvasGroup>(false))
                if (group.alpha <= 0) return null;
            return text.text;
        }

        private static string KnownBlocker(StoreUIManager ui, PlayerStore store)
        {
            if (ui.escapeUIManager != null && Active(ui.escapeUIManager.menuPanel)) return "escape_menu";
            if (ui.gameOptionUIManager != null && ui.gameOptionUIManager.IsOpen) return "options";
            var generic = GenericConfirmationUIManager.Instance;
            if (generic != null && Active(generic.panel)) return "generic_confirmation";
            if (ui.confirmPanelUIManager != null && Active(ui.confirmPanelUIManager.panel)) return "confirmation";
            if (Active(ui.closeShutterConfirmationPanel)) return "shutter_confirmation";
            if (store.isClientBeingArrested || store.isHandlingShootout || ui.isShot) return "trade_locked";
            var bargain = BargainUIManager.Instance;
            if (bargain != null && bargain.IsOpen) return "bargain";
            return null; // Absence of a known blocker does NOT establish a legal action.
        }
    }
}
#endif
