using System.Collections.Generic;
using UnityEngine;

namespace PawnPilot
{
    // Main-thread, read-only projection input. It intentionally carries only opaque object
    // references and state needed by the pure BusinessProjection; raw game objects never serialize.
    internal static class BusinessCustomerReader
    {
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
                customer.TradeType = blueprint.clientIntent == StoreClient.ClientIntent.SELL ? "sell" : "other";

            var dialog = DialogUIManager.Instance;
            var negotiation = NegociationUIManager.Instance;
            if (dialog == null) return input;
            var current = dialog.currentDialog;
            if (current == null)
            {
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

            // These methods are direct comparisons/field reads in the reviewed IL. Do not read
            // Dialogue.plainText/text or invoke any action callback to infer display state.
            if (dialog.IsChoice())
            {
                if (TryReadVisibleMainOptions(dialog, out var visibleOptions))
                {
                    customer.OptionIdentities = visibleOptions.ToArray();
                    customer.DialogueStage = "choices";
                }
                else customer.DialogueStage = "updating";
            }
            else if (dialog.IsLineStillDisplaying(current))
            {
                customer.DialogueStage = "typing";
            }
            else if (dialog.isWaitingForManualAdvance)
            {
                customer.DialogueStage = "waiting";
            }
            else
            {
                customer.DialogueStage = "updating";
            }
            return input;
        }

        private static bool TryReadVisibleMainOptions(DialogUIManager dialog, out List<object> result)
        {
            result = new List<object>();
            if (dialog == null || dialog.dialogChoiceBubbles == null || dialog.dialogChoiceBubbles.Count == 0 ||
                dialog.dialogChoiceBubbles.Count > 64) return false;
            foreach (var bubble in dialog.dialogChoiceBubbles)
            {
                if (bubble == null || bubble.type != 0 || !bubble.gameObject.activeInHierarchy) continue;
                var rect = bubble.GetComponent<RectTransform>();
                if (!bubble.IsInteractable() || rect == null || bubble.TMP == null ||
                    !VisibleUiReader.RectVisible(rect) || !VisibleUiReader.TextVisible(bubble.TMP, rect)) continue;
                result.Add(bubble);
                if (result.Count > 16) { result.Clear(); return false; }
            }
            return result.Count > 0;
        }
    }
}
