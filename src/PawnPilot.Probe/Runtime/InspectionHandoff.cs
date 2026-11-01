using System;
using System.Collections.Generic;
using System.Linq;

namespace PawnPilot
{
    // Exact confirmed factory identifiers. Classification says only that a human must
    // inspect this family; it never evaluates real/fake state, feature values or price.
    public static class InspectionClassifier
    {
        public static bool RequiresManualInspection(string identifier, string npcDirection) => npcDirection == "sell" &&
            (identifier == "packet_red_cigarette" || identifier == "water_pitcher" || identifier == "water_jug" ||
             identifier == "bottled_water" || identifier == "small_bottled_water" ||
             identifier == "large_bottled_water" || identifier == "mini_bottle");
    }

    public sealed partial class BusinessProjection
    {
        private string inspectionWorld, inspectionTriggerWorld;
        private readonly string inspectionNamespace = Guid.NewGuid().ToString("N");
        private long nextInspection;
        private object inspectionVisit;
        private object inspectionCurrentVisit;
        private long inspectionVisitGeneration, inspectionTriggerGeneration;
        private object[] inspectionPendingItems = Array.Empty<object>();
        private readonly HashSet<object> inspectionCompleted = new HashSet<object>(RefComparer.Instance);
        private InspectionHandoffState inspectionHandoff;
        private BusinessInput inspectionInput;
        private bool testInspectionConsumed;

        public bool IsInspectionWaiting => inspectionHandoff?.requires_manual_inspection == true;
        public static bool IsInspectionWaitingIn(Snapshot snapshot) => snapshot?.business?.details?.inspection_handoff?.requires_manual_inspection == true;

        private void ObserveInspectionWorld(string epoch)
        {
            if (inspectionWorld == epoch) return;
            inspectionWorld = epoch;
            inspectionCompleted.Clear();
            inspectionCurrentVisit = null;
            inspectionVisitGeneration++;
            inspectionInput = null;
            // Pending user ownership survives a load/world change. Its references remain
            // historical evidence, while all action versions still bind the current world.
        }

        private void ObserveInspection(BusinessInput input, BusinessObservation business)
        {
            inspectionInput = input;
            if (input.Customer.Presence == "absent")
            {
                inspectionCompleted.Clear(); inspectionCurrentVisit = null; inspectionVisitGeneration++;
            }
            else if (input.Customer.Presence == "present" && input.Customer.Identity != null &&
                !ReferenceEquals(inspectionCurrentVisit, input.Customer.Identity))
            {
                inspectionCompleted.Clear(); inspectionCurrentVisit = input.Customer.Identity; inspectionVisitGeneration++;
            }
            if (IsInspectionWaiting || input.Customer.Presence != "present" || input.Customer.Identity == null ||
                input.Customer.TradeType != "sell" || business.customer_ref == null) return;
            var items = InspectionItems(input, business).Where(pair =>
                InspectionClassifier.RequiresManualInspection(pair.Item1.Identifier, input.Customer.TradeType) &&
                !InspectionCompleted(input.Customer.Identity, pair.Item1.Identity)).ToList();
            if (items.Count > 0) BeginInspection(input.Customer.Identity, business.customer_ref, items, "manual_item_inspection");
        }

        private IEnumerable<Tuple<BusinessItemInput, ItemState>> InspectionItems(BusinessInput input, BusinessObservation business)
        {
            var seen = new HashSet<object>(RefComparer.Instance);
            foreach (var container in input.Containers)
            {
                if (container.Container != "front" && container.Container != "back") continue;
                foreach (var item in container.Items)
                {
                    if (item.Owned || !seen.Add(item.Identity)) continue;
                    var visible = business.details.items.FirstOrDefault(value => value.container == container.Container &&
                        itemRefs.TryGetValue(item.Identity, out var record) && record.reference == value.item_ref);
                    if (visible != null) yield return Tuple.Create(item, visible);
                }
            }
        }

        private bool InspectionCompleted(object visit, object item) =>
            ReferenceEquals(inspectionCurrentVisit, visit) && inspectionCompleted.Contains(item);

        private void BeginInspection(object visit, string customerReference, List<Tuple<BusinessItemInput, ItemState>> items, string reason)
        {
            string previousAck = inspectionHandoff?.last_resumed_handoff_ref;
            inspectionTriggerWorld = inspectionWorld;
            inspectionTriggerGeneration = inspectionVisitGeneration;
            inspectionVisit = visit;
            testInspectionConsumed = true;
            inspectionPendingItems = items.Select(pair => pair.Item1.Identity).ToArray();
            int nameBudget = 2048;
            var visibleItems = new List<InspectionHandoffItem>();
            foreach (var pair in items)
            {
                string name = pair.Item2.name;
                if (name != null && name.Length > nameBudget) name = null;
                if (name != null) nameBudget -= name.Length;
                visibleItems.Add(new InspectionHandoffItem { item_ref = pair.Item2.item_ref, name = name });
            }
            inspectionHandoff = new InspectionHandoffState { requires_manual_inspection = true, status = "waiting_for_user",
                handoff_ref = "inspection-handoff-" + inspectionNamespace + "-" + (++nextInspection).ToString(System.Globalization.CultureInfo.InvariantCulture),
                customer_ref = customerReference, reason = reason, last_resumed_handoff_ref = previousAck,
                items = visibleItems };
        }

        public bool ResumeInspectionHandoff(string worldEpoch, string handoffRef)
        {
            if (!IsInspectionWaiting || worldEpoch != inspectionWorld || handoffRef != inspectionHandoff.handoff_ref) return false;
            if (inspectionTriggerWorld == inspectionWorld && inspectionTriggerGeneration == inspectionVisitGeneration &&
                ReferenceEquals(inspectionVisit, inspectionCurrentVisit))
            {
                foreach (object item in inspectionPendingItems) inspectionCompleted.Add(item);
            }
            inspectionHandoff.requires_manual_inspection = false;
            inspectionHandoff.status = "resumed";
            inspectionHandoff.last_resumed_handoff_ref = handoffRef;
            inspectionPendingItems = Array.Empty<object>();
            return true;
        }

        // Called only from the root's explicitly compiled test path, never a production MCP
        // action. A complete quote binds the chosen real instance; no game state is altered.
        public bool TryTriggerTestInspection(BusinessObservation business)
        {
            var input = inspectionInput;
            if (testInspectionConsumed || IsInspectionWaiting || input == null || business?.details == null ||
                input.Customer.Identity == null || input.Customer.Presence != "present" || input.Customer.TradeType != "sell" ||
                input.Customer.Phase != "trading" || business.details.quote.status != "complete" ||
                business.details.quote.mode != "normal" || business.item_refs == null || business.item_refs.Count == 0 ||
                business.customer_ref != customerRef) return false;
            var item = InspectionItems(input, business).FirstOrDefault(pair => business.item_refs.Contains(pair.Item2.item_ref));
            if (item == null) return false;
            BeginInspection(input.Customer.Identity, business.customer_ref, new List<Tuple<BusinessItemInput, ItemState>> { item }, "test_inspection_handoff");
            business.details.inspection_handoff = BusinessWire.CloneInspection(inspectionHandoff);
            return true;
        }

        public void ApplyInspectionGate(Snapshot snapshot)
        {
            if (snapshot?.business?.details == null) return;
            snapshot.business.details.inspection_handoff = BusinessWire.CloneInspection(inspectionHandoff);
            if (!IsInspectionWaiting) return;
            snapshot.legal_actions.Clear();
            snapshot.can_close_menu = false;
            snapshot.business.details.eligibility.purchase_enabled = false;
            if (!snapshot.business.details.eligibility.missing.Contains("manual_item_inspection"))
                snapshot.business.details.eligibility.missing.Add("manual_item_inspection");
            if (snapshot.interaction != null) { snapshot.interaction.operable = false; snapshot.interaction.reason = "inspection_handoff"; }
            if (snapshot.ready) snapshot.legal_actions.Add(new LegalAction { action = "resume_inspection_handoff",
                arguments = new ActionArguments { handoff_ref = inspectionHandoff.handoff_ref } });
        }
    }
}
