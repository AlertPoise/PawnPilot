using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace PawnPilot
{
    // Main-thread read-only observation. Enumerates only three documented direct grids;
    // it never recurses into bags, prices items, calls refresh, or infers quote binding.
    internal static class BusinessReader
    {
        private const int MaxItems = 64;

        internal static BusinessInput Read()
        {
            var input = BusinessCustomerReader.Read();
            var entry = EmporiumEntry.Instance;
            input.Ui = ReadUi();
            bool uiBlocked = input.Ui.Status == "blocked" ||
                Array.IndexOf(input.Ui.Blockers, "ui_reader_failed") >= 0;
            int totalItems = 0;
            input.Containers = new[]
            {
                ReadInventory("front", entry == null ? null : entry.frontInvWindow,
                    entry == null ? null : entry.frontInvinvElement, uiBlocked, ref totalItems),
                ReadInventory("back", entry == null ? null : entry.backInvWindow,
                    entry == null ? null : entry.backInvinvElement, uiBlocked, ref totalItems),
                ReadInventory("player", entry == null ? null : entry.invWindow,
                    entry == null ? null : entry.invElement, uiBlocked, ref totalItems)
            };
            input.Quote = ReadQuote(input.Customer, input.Containers);
            return input;
        }

        private static BusinessContainerInput ReadInventory(string name, PixelWindow window, GameGridInventory inventory,
            bool uiBlocked, ref int totalItems)
        {
            var result = new BusinessContainerInput { Container = name, Completeness = "unavailable",
                Reason = "container_unavailable", Items = Array.Empty<BusinessItemInput>() };
            try
            {
                if (uiBlocked)
                {
                    result.Completeness = "incomplete";
                    result.Reason = "ui_blocked_or_unverified";
                    return result;
                }
                if (window == null || inventory == null || window.rectTransform == null || inventory.rectTransform == null ||
                    inventory.inner == null || inventory.inner.rectTransform == null)
                    return result;
                if (!window.rectTransform.gameObject.activeInHierarchy || !window.IsVisible())
                {
                    result.Completeness = "incomplete";
                    result.Reason = "window_inactive";
                    return result;
                }
                if (!ReferenceEquals(window.childElement, inventory))
                {
                    result.Completeness = "incomplete";
                    result.Reason = "window_inventory_link_unverified";
                    return result;
                }
                if (!inventory.rectTransform.gameObject.activeInHierarchy)
                {
                    result.Completeness = "incomplete";
                    result.Reason = "inventory_inactive";
                    return result;
                }
                var viewport = inventory.inner.rectTransform;
                if (!viewport.gameObject.activeInHierarchy)
                {
                    result.Completeness = "incomplete";
                    result.Reason = "inventory_viewport_inactive";
                    return result;
                }
                string rectFailure = VisibleUiReader.RectFailure(viewport);
                if (rectFailure != null)
                {
                    result.Completeness = "incomplete";
                    result.Reason = "viewport_" + rectFailure;
                    return result;
                }

                // Copy the direct collection first. A concurrent mutation or unsupported item makes
                // this container incomplete; no nested container is traversed.
                var children = inventory.childItems;
                if (children == null)
                {
                    result.Completeness = "incomplete";
                    result.Reason = "container_members_unavailable";
                    return result;
                }
                var copied = new List<GameItem>(children.Count);
                foreach (var child in children)
                {
                    if (copied.Count >= MaxItems)
                    {
                        result.Completeness = "incomplete";
                        result.Reason = "item_limit_exceeded";
                        break;
                    }
                    copied.Add(child);
                }

                var visible = new List<BusinessItemInput>(copied.Count);
                bool complete = result.Reason != "item_limit_exceeded";
                foreach (var item in copied)
                {
                    var element = item as GameItemElement;
                    if (item == null || element == null || !ReferenceEquals(item.parentInventory, inventory) ||
                        element.rectTransform == null || !VisibleUiReader.RectVisible(element.rectTransform) ||
                        !VisibleUiReader.Contained(element.rectTransform, viewport))
                    {
                        complete = false;
                        if (result.Reason == "container_unavailable") result.Reason = "member_visibility_unverified";
                        continue;
                    }
                    if (visible.Count >= MaxItems || totalItems >= MaxItems)
                    {
                        complete = false;
                        result.Reason = "item_limit_exceeded";
                        break;
                    }
                    int units = item.unitCount;
                    if (units <= 0)
                    {
                        complete = false;
                        result.Reason = "member_data_unverified";
                        continue;
                    }
                    string shapeStatus = "unverified";
                    int? width = null, height = null;
                    var modifiedShape = item.modifiedShape;
                    if (modifiedShape != null && modifiedShape.GetType() == typeof(GridShapeBuilder))
                    {
                        var builder = (GridShapeBuilder)modifiedShape;
                        // Exact-type getters only swap backing width/height for orientation.
                        // These are current bounding dimensions, not an occupied-cell proof.
                        int observedWidth = builder.globalWidth;
                        int observedHeight = builder.globalHeight;
                        if (observedWidth > 0 && observedHeight > 0)
                        {
                            shapeStatus = "known";
                            width = observedWidth;
                            height = observedHeight;
                        }
                    }
                    // Same basic title used by the native inventory tooltip and trade row.
                    // false avoids the custom-name path; no inspection or valuation is invoked.
                    string displayName = item.GetDisplayName(false);
                    if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 256) displayName = null;
                    visible.Add(new BusinessItemInput { Identity = item, Name = displayName, Units = units,
                        Owned = GeneralHelper.IsItemOwned(item), ShapeStatus = shapeStatus, Width = width, Height = height });
                    totalItems++;
                }
                result.Items = visible.ToArray();
                result.Completeness = complete ? "complete" : "incomplete";
                if (complete) result.Reason = null;
                else if (result.Reason == "container_unavailable") result.Reason = "member_visibility_unverified";
                return result;
            }
            catch
            {
                // Fail closed and discard this container's values if enumeration itself failed.
                result.Completeness = "unavailable";
                result.Reason = "container_read_failed";
                result.Items = Array.Empty<BusinessItemInput>();
                return result;
            }
        }

        private static BusinessQuoteInput ReadQuote(BusinessCustomerInput customer, BusinessContainerInput[] containers)
        {
            var unavailable = new BusinessQuoteInput { Status = "unverified", Mode = "unknown",
                Reason = "quote_binding_unverified", BindingVerified = false,
                Rows = Array.Empty<BusinessQuoteRowInput>() };
            try
            {
                var ui = BusinessUiReader.Read();
                if (ui.status == "blocked" || ui.blockers == null || ui.blockers.Contains("ui_reader_failed"))
                {
                    unavailable.Reason = "ui_blocked_or_unverified";
                    return unavailable;
                }
                var negotiation = NegociationUIManager.Instance;
                if (negotiation == null || negotiation.uiObject == null || !negotiation.uiObject.activeInHierarchy)
                {
                    unavailable.Reason = "quote_surface_unavailable";
                    return unavailable;
                }
                var frame = negotiation.uiObject.GetComponent<NegotiationTabletFrame>();
                if (frame == null || frame.content == null || frame.footer == null || frame.scroll == null || frame.scroll.viewport == null)
                {
                    unavailable.Reason = "quote_surface_unverified";
                    return unavailable;
                }

                if (customer == null || customer.Presence != "present" || customer.Arrived != true ||
                    (customer.TradeType != "sell" && customer.TradeType != "buy") || customer.Phase != "trading")
                {
                    unavailable.Reason = "normal_sell_context_unverified";
                    return unavailable;
                }
                BusinessContainerInput back = null;
                foreach (var container in containers ?? Array.Empty<BusinessContainerInput>())
                    if (container != null && container.Container == "back") back = container;
                if (back == null || back.Completeness != "complete" || back.Items == null)
                {
                    unavailable.Reason = "purchase_container_incomplete";
                    return unavailable;
                }
                var backPurchaseItems = new List<BusinessItemInput>();
                bool sale = customer.TradeType == "buy";
                foreach (var item in back.Items)
                    if (item != null && item.Owned == sale) backPurchaseItems.Add(item);
                if (sale && (back.Items.Length != 1 || backPurchaseItems.Count != 1))
                {
                    unavailable.Reason = "sale_requires_single_owned_counter_item";
                    return unavailable;
                }
                if (backPurchaseItems.Count == 0)
                {
                    unavailable.Reason = "purchase_set_empty";
                    return unavailable;
                }

                var rows = new List<BusinessQuoteRowInput>();
                var negotiationContentRows = new List<TabletListRow>();
                int renderedRowCount = 0;
                bool filtered = false;
                int visitedRows = 0;
                foreach (Transform child in frame.content)
                {
                    if (++visitedRows > 64) { filtered = true; break; }
                    if (!child.gameObject.activeInHierarchy) continue;
                    var rowComponent = child.GetComponent<TabletListRow>();
                    if (rowComponent == null) continue;
                    renderedRowCount++;
                    negotiationContentRows.Add(rowComponent);
                    if (rows.Count >= 64) { filtered = true; break; }
                    string host = child.name == "SelectedRow" ? "Texts/" : child.name == "Row" ? "Visuals/" : null;
                    if (host == null) { filtered = true; continue; }
                    var nameTransform = child.Find(host + "Name");
                    var priceTransform = child.Find(host + "Right");
                    var nameText = nameTransform == null ? null : nameTransform.GetComponent<TMP_Text>();
                    var priceText = priceTransform == null ? null : priceTransform.GetComponent<TMP_Text>();
                    if (!VisibleUiReader.TextVisible(nameText, frame.scroll.viewport) ||
                        !VisibleUiReader.TextVisible(priceText, frame.scroll.viewport))
                    {
                        filtered = true;
                        continue;
                    }
                    VisibleQuoteBinding.TryGetItem(rowComponent, out var visibleItem);
                    rows.Add(new BusinessQuoteRowInput { ItemIdentity = visibleItem,
                        Name = VisibleUiReader.RenderedText(nameText), PriceText = VisibleUiReader.RenderedText(priceText), Amount = null });
                }

                string visibleTotal = null;
                string visibleWholesale = null;
                int visitedFooter = 0;
                int footerRowCount = 0;
                foreach (Transform child in frame.footer)
                {
                    if (++visitedFooter > 8) { filtered = true; break; }
                    if (!child.gameObject.activeInHierarchy) continue;
                    if (child.name != "FooterRow") continue;
                    footerRowCount++;
                    if (footerRowCount > 2) { filtered = true; continue; }
                    var labelTransform = child.Find("Label");
                    var valueTransform = child.Find("Value");
                    var label = labelTransform == null ? null : labelTransform.GetComponent<TMP_Text>();
                    var value = valueTransform == null ? null : valueTransform.GetComponent<TMP_Text>();
                    if (!VisibleUiReader.TextVisible(label) || !VisibleUiReader.TextVisible(value)) { filtered = true; continue; }
                    string labelValue = VisibleUiReader.RenderedText(label);
                    string valueText = VisibleUiReader.RenderedText(value);
                    if (string.IsNullOrEmpty(labelValue) || string.IsNullOrEmpty(valueText)) { filtered = true; continue; }
                    // BuildSelling emits exactly two direct footer rows: funds, then total.
                    // The total row's wholesale variant displays normal / wholesale (discount).
                    if (footerRowCount == 2) { visibleTotal = valueText; visibleWholesale = valueText; }
                }
                unavailable.Rows = rows.ToArray();
                unavailable.Mode = "normal";
                unavailable.DisplayedTotal = BusinessQuoteParsing.ParseVisibleNormalTotal(visibleTotal);
                unavailable.WholesaleTotal = BusinessQuoteParsing.ParseVisibleWholesaleTotal(visibleWholesale);
                bool rowSetMatches = renderedRowCount == backPurchaseItems.Count;
                var boundItems = new HashSet<object>();
                foreach (var row in negotiationContentRows)
                {
                    if (!VisibleQuoteBinding.TryGetItem(row, out var boundItem) || !boundItems.Add(boundItem) ||
                        !backPurchaseItems.Exists(item => ReferenceEquals(item.Identity, boundItem))) rowSetMatches = false;
                }
                bool supportedSelection = false;
                var purchaseIdentities = new List<object>();
                if (sale)
                {
                    var client = PlayerStore.Instance.currentClientInstance?.GetClientBlueprint();
                    if (GameSaleActions.IsSupportedOrdinaryBuyer(client) &&
                        GameSaleActions.CanOfferItem(backPurchaseItems[0].Identity as GameItem, client))
                    {
                        purchaseIdentities.Add(backPurchaseItems[0].Identity);
                        supportedSelection = true;
                    }
                    unavailable.WholesaleTotal = null;
                }
                else if (customer.MultiBuyDisabled.HasValue)
                {
                    if (!customer.MultiBuyDisabled.Value || backPurchaseItems.Count == 1)
                    {
                        foreach (var item in backPurchaseItems) purchaseIdentities.Add(item.Identity);
                        supportedSelection = true;
                    }
                    else if (negotiationContentRows.Count == backPurchaseItems.Count)
                    {
                        int selectedIndex = -1;
                        bool ambiguous = false;
                        for (int i = 0; i < negotiationContentRows.Count; i++)
                            if (negotiationContentRows[i].selected)
                            {
                                if (selectedIndex >= 0) ambiguous = true;
                                selectedIndex = i;
                            }
                        if (!ambiguous)
                        {
                            if (selectedIndex < 0)
                            {
                                purchaseIdentities.Add(backPurchaseItems[0].Identity); // native fallback
                                supportedSelection = true;
                            }
                            else if (VisibleQuoteBinding.TryGetItem(negotiationContentRows[selectedIndex], out var selectedItem))
                            {
                                purchaseIdentities.Add(selectedItem);
                                supportedSelection = true;
                            }
                        }
                    }
                }
                unavailable.PurchaseIdentities = purchaseIdentities.ToArray();
                bool footerMatches = footerRowCount == 2 && unavailable.DisplayedTotal.HasValue;
                unavailable.BindingVerified = rowSetMatches && footerMatches && supportedSelection;
                if (unavailable.BindingVerified)
                {
                    unavailable.Status = "complete";
                    unavailable.Reason = null;
                }
                else unavailable.Reason = !footerMatches ? "normal_total_unverified" :
                    !rowSetMatches ? "quote_row_set_unverified" : filtered ? "quote_visible_rows_partial" :
                    "purchase_selection_unverified";
                return unavailable;
            }
            catch
            {
                // Do not expose exception text, rows collected before failure, or stale quote data.
                unavailable.Rows = Array.Empty<BusinessQuoteRowInput>();
                unavailable.DisplayedTotal = null;
                unavailable.Reason = "quote_read_failed";
                return unavailable;
            }
        }

        private static BusinessUiInput ReadUi()
        {
            try
            {
                var state = BusinessUiReader.Read();
                return new BusinessUiInput { Status = state.status, Coverage = state.coverage,
                    Blockers = state.blockers == null ? Array.Empty<string>() : state.blockers.ToArray(), Complete = false };
            }
            catch
            {
                return new BusinessUiInput { Status = "unverified", Coverage = "known_roots_only",
                    Blockers = new[] { "ui_reader_failed" }, Complete = false };
            }
        }
    }
}
