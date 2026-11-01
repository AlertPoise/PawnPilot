using System;
using System.Collections.Generic;
using UnityEngine;

namespace PawnPilot
{
    // Bounded scan of public, known UI roots and direct interaction flags. It does
    // not raycast, invoke refresh/escape helpers, enumerate arbitrary scene objects,
    // or infer that the complete topmost UI stack is clear.
    public static class BusinessUiReader
    {
        public static BusinessUiState Read()
        {
            var blockers = new List<string>(20);
            var unverifiedFindings = new List<string>(4);
            bool failed = false;
            try
            {
                var ui = StoreUIManager.Instance;
                if (ui == null)
                {
                    failed = true;
                    return BusinessUiEvidence.Evaluate(blockers, failed);
                }

                AddIfActive(blockers, ui.escapeUIManager == null ? null : ui.escapeUIManager.menuPanel, "escape_menu");
                AddIfActive(blockers, ui.gameOptionUIManager == null ? null : ui.gameOptionUIManager.optionPanel, "options");
                AddIfActive(blockers, ui.confirmPanelUIManager == null ? null : ui.confirmPanelUIManager.panel, "confirmation");
                AddIfActive(blockers, ui.closeShutterConfirmationPanel, "shutter_confirmation");
                if (Active(ui.controlHintOverlay)) unverifiedFindings.Add("control_hints_active_unverified");
                if (Active(ui.tutorialGroup)) unverifiedFindings.Add("tutorial_group_active_unverified");
                if (Active(ui.backgroundBlocking)) unverifiedFindings.Add("background_blocker_active_unverified");

                var store = PlayerStore.Instance;
                if (store == null) failed = true;
                else if (!store.isPowerOn) blockers.Add("store_power_off");
                if (Active(ui.darknessPanel) && (store == null || store.isPowerOn))
                    unverifiedFindings.Add("darkness_root_power_mismatch");

                var genericConfirmation = GenericConfirmationUIManager.Instance;
                if (genericConfirmation != null)
                    AddIfActive(blockers, genericConfirmation.panel, "generic_confirmation");

                var bargain = BargainUIManager.Instance;
                if (bargain != null)
                    AddIfActive(blockers, bargain.uiObject, "bargain_panel");

                var negotiation = NegociationUIManager.Instance;
                if (negotiation != null && Active(negotiation.uiObject))
                {
                    var frame = negotiation.uiObject.GetComponent<NegotiationTabletFrame>();
                    if (frame == null) failed = true;
                    else AddIfActive(blockers, frame.confirmRoot, "trade_confirmation");
                }

                var dialogue = DialogUIManager.Instance;
                if (dialogue == null)
                {
                    failed = true;
                }
                // Dialogue is action-local state. The customer projection exposes its
                // visible text/options; each dialogue or trade action checks its own phase.

                if (UIDragHandler.isDraggingAnyUI) blockers.Add("ui_window_drag_active");

                // These getters are verified field projections in the supported game build.
                var itemDrag = ItemMouseDragHandler.current;
                if (itemDrag != null && itemDrag.IsDraggingItem) blockers.Add("item_drag_active");
                var multiSelect = ItemMultiSelectHandler.current;
                if (multiSelect != null && multiSelect.HasSelection) blockers.Add("item_selection_active");
                if (multiSelect != null && multiSelect.IsGroupDragging) blockers.Add("item_group_drag_active");
                var itemSelect = ItemSelectHandler.current;
                if (itemSelect != null && itemSelect.IsEquipped) blockers.Add("item_equip_selection_active");
            }
            catch
            {
                // Do not return exception text or stale evidence.
                failed = true;
            }

            return BusinessUiEvidence.Evaluate(blockers, failed, unverifiedFindings);
        }

        private static void AddIfActive(List<string> blockers, GameObject root, string reason)
        {
            if (Active(root)) blockers.Add(reason);
        }

        private static bool Active(GameObject obj) => obj != null && obj.activeInHierarchy;
    }
}
