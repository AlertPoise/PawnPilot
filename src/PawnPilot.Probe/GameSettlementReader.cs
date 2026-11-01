using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PawnPilot
{
    // Reads the already built end-of-day controls. Never refreshes costs, changes
    // service choices, forces TMP layout, or reads a hidden/future service list.
    internal static class GameSettlementReader
    {
        private const int MaxServices = 16;
        private static readonly FieldInfo Frame = Field("frame");
        private static readonly FieldInfo Rows = Field("rows");
        private static readonly FieldInfo Total = Field("totalRow");
        private static readonly FieldInfo Funds = Field("fundsRow");
        private static readonly FieldInfo Confirm = Field("endDayButton");

        private static FieldInfo Field(string name) => typeof(EndOfDayUIManager)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool TryRead(out SettlementState state, out object identity,
            out string terms, out string reason)
        {
            state = null; identity = null; terms = null; reason = "settlement_not_visible";
            try
            {
                var manager = EndOfDayUIManager.Instance;
                if (manager?.UI == null || !manager.UI.activeInHierarchy || TransitionPending()) return false;
                var frame = Frame?.GetValue(manager) as EndOfDayTabletFrame;
                var button = Confirm?.GetValue(manager) as TabletButton;
                var rows = Rows?.GetValue(manager) as IList;
                if (frame?.scroll?.viewport == null || frame.content == null || button?.button == null ||
                    rows == null || rows.Count > MaxServices || Total == null || Funds == null)
                { reason = "settlement_controls_unverified"; return false; }
                string header = Text(frame.sectionHeader);
                string info = Text(frame.infoLine);
                string confirmLabel = Text(button.label);
                var totalRow = (TabletUI.FooterRow)Total.GetValue(manager);
                var fundsRow = (TabletUI.FooterRow)Funds.GetValue(manager);
                string totalLabel = Text(totalRow.label), fundsLabel = Text(fundsRow.label);
                if (header == null || info == null || confirmLabel == null || totalLabel == null || fundsLabel == null ||
                    !ReadCredits(totalRow.value, null, out string totalText, out int total) ||
                    !ReadCredits(fundsRow.value, null, out string fundsText, out int funds))
                { reason = "settlement_text_unverified"; return false; }
                if (!PlayerStore.HasInstance || PlayerStore.Instance.playerCash != funds ||
                    !VisibleUiReader.RectVisible(button.transform as RectTransform))
                { reason = "settlement_funds_or_button_unverified"; return false; }
                var builder = new StringBuilder();
                AddIdentity(builder, manager.UI); AddIdentity(builder, frame);
                AddIdentity(builder, frame.sectionHeader); AddIdentity(builder, frame.infoLine);
                AddIdentity(builder, totalRow.value); AddIdentity(builder, fundsRow.value);
                AddIdentity(builder, button); AddIdentity(builder, button.button); AddIdentity(builder, button.label);
                Add(builder, DayNumber()?.ToString(CultureInfo.InvariantCulture));
                Add(builder, header); Add(builder, info); Add(builder, confirmLabel);
                Add(builder, totalLabel); Add(builder, totalText); Add(builder, fundsLabel); Add(builder, fundsText);
                Add(builder, button.button.IsInteractable() ? "enabled" : "disabled");
                var services = new List<DayServiceState>(rows.Count);
                long selectedTotal = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    var boxed = rows[i];
                    var tupleType = boxed?.GetType();
                    var service = tupleType?.GetField("Item1")?.GetValue(boxed) as StoreService;
                    var checkbox = tupleType?.GetField("Item2")?.GetValue(boxed) as TabletCheckbox;
                    var visuals = checkbox?.transform.parent;
                    var name = visuals?.Find("Name")?.GetComponent<TMP_Text>();
                    var cost = visuals?.Find("Cost")?.GetComponent<TMP_Text>();
                    if (service == null || checkbox?.button == null || visuals == null ||
                        !checkbox.gameObject.activeInHierarchy ||
                        !VisibleUiReader.RectVisible(checkbox.transform as RectTransform) ||
                        !VisibleUiReader.Contained(checkbox.transform as RectTransform, frame.scroll.viewport))
                    { reason = "settlement_service_visibility_unverified"; return false; }
                    string label = Text(name, frame.scroll.viewport);
                    if (label == null || !ReadCredits(cost, frame.scroll.viewport, out string costText, out int amount))
                    { reason = "settlement_service_text_unverified"; return false; }
                    bool selected = checkbox.IsOn;
                    // Compare only this displayed row with its own native source. No
                    // values from the undisplayed service collection are projected.
                    if (!service.unlocked || service.cost != amount || service.isUsing != selected)
                    { reason = "settlement_service_stale"; return false; }
                    if (selected) selectedTotal += amount;
                    services.Add(new DayServiceState { label = label, cost_text = costText, cost = amount, selected = selected });
                    AddIdentity(builder, visuals); AddIdentity(builder, checkbox); AddIdentity(builder, checkbox.button);
                    AddIdentity(builder, name); AddIdentity(builder, cost);
                    Add(builder, label); Add(builder, costText); Add(builder, selected ? "selected" : "unselected");
                    Add(builder, checkbox.button.IsInteractable() ? "enabled" : "disabled");
                }
                if (selectedTotal != total)
                { reason = "settlement_total_mismatch"; return false; }
                state = new SettlementState { settlement_ref = null, total = total, services = services };
                identity = manager.UI; terms = builder.ToString(); reason = null;
                return true;
            }
            catch
            {
                state = null; identity = null; terms = null; reason = "settlement_read_failed";
                return false;
            }
        }

        internal static bool ConfirmationReady()
        {
            var manager = EndOfDayUIManager.Instance;
            var button = manager == null ? null : Confirm?.GetValue(manager) as TabletButton;
            return manager?.UI != null && manager.UI.activeInHierarchy && !TransitionPending() &&
                button?.button != null && button.button.isActiveAndEnabled && button.button.IsInteractable() &&
                VisibleUiReader.RectVisible(button.transform as RectTransform) && Text(button.label) != null;
        }

        internal static bool ReportOpen()
        {
            var manager = AdvCalendarUIManager.Instance;
            return manager?.UIPanel != null && manager.UIPanel.activeInHierarchy &&
                manager.nightlyReportSection != null && manager.nightlyReportSection.activeInHierarchy;
        }

        internal static bool CloseReportReady() => FindReportCloseButton() != null;

        internal static void CloseReport()
        {
            var button = FindReportCloseButton();
            if (button == null) throw new NativeActionRejectedException("nightly_report_close_unverified");
            // The actual visible button proves this is the normal available close
            // operation; call only its reviewed semantic method, not other listeners.
            AdvCalendarUIManager.Instance.CloseUI();
        }

        private static Button FindReportCloseButton()
        {
            if (!ReportOpen() || TransitionPending()) return null;
            var manager = AdvCalendarUIManager.Instance;
            var buttons = manager.UIPanel.GetComponentsInChildren<Button>(false);
            if (buttons.Length > 128) return null;
            Button found = null;
            foreach (var button in buttons)
            {
                if (!button.isActiveAndEnabled || !button.IsInteractable() ||
                    !VisibleUiReader.RectVisible(button.transform as RectTransform)) continue;
                int count = button.onClick.GetPersistentEventCount();
                if (count > 8) continue;
                bool closes = false;
                for (int i = 0; i < count; i++)
                    if (ReferenceEquals(button.onClick.GetPersistentTarget(i), manager) &&
                        button.onClick.GetPersistentMethodName(i) == "CloseUI") closes = true;
                if (!closes) continue;
                if (found != null) return null;
                found = button;
            }
            return found;
        }

        internal static bool TransitionPending()
        {
            var ui = StoreUIManager.Instance;
            return ui == null || ui.blackscreen == null || ui.blackscreen.gameObject.activeInHierarchy;
        }

        internal static int? DayNumber()
        {
            if (!PlayerStore.HasInstance) return null;
            int day = StoreStation.Instance.dayCounter;
            return day > 0 ? day : (int?)null;
        }

        private static string Text(TMP_Text text, RectTransform viewport = null)
        {
            if (!VisibleUiReader.TextVisible(text, viewport)) return null;
            string value = VisibleUiReader.RenderedText(text);
            return string.IsNullOrWhiteSpace(value) || value.Length > 256 ? null : value;
        }

        private static bool ReadCredits(TMP_Text text, RectTransform viewport, out string rendered, out int amount)
        {
            rendered = Text(text, viewport); amount = 0;
            if (rendered == null) return false;
            var info = text.textInfo;
            var numeric = new StringBuilder();
            bool currency = false;
            for (int i = 0; i < info.characterCount; i++)
            {
                var character = info.characterInfo[i];
                if (character.elementType == TMP_TextElementType.Sprite)
                {
                    var asset = character.spriteAsset;
                    if (currency || asset == null || character.spriteIndex < 0 ||
                        character.spriteIndex >= asset.spriteCharacterTable.Count ||
                        asset.spriteCharacterTable[character.spriteIndex].name != "c") return false;
                    currency = true;
                }
                else
                {
                    if (currency && !char.IsWhiteSpace(character.character)) return false;
                    if (!currency) numeric.Append(character.character);
                }
            }
            string value = numeric.ToString().Trim();
            if (!currency || value.Length == 0 || value.Length > 11) return false;
            int firstDigit = value[0] == '-' ? 1 : 0;
            if (firstDigit == value.Length) return false;
            for (int i = firstDigit; i < value.Length; i++) if (value[i] < '0' || value[i] > '9') return false;
            return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
        }

        private static void AddIdentity(StringBuilder builder, UnityEngine.Object value) =>
            Add(builder, value == null ? null : value.GetInstanceID().ToString(CultureInfo.InvariantCulture));
        private static void Add(StringBuilder builder, string value) =>
            builder.Append(value == null ? -1 : value.Length).Append(':').Append(value).Append('|');
    }
}
