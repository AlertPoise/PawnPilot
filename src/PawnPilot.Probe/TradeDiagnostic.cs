using System;
using System.Text;

namespace PawnPilot
{
    // Local diagnostic projection only. Neither of these types is an MCP DTO.
    // Raw object identities never leave this class or enter the JSON serializer.
    public sealed class TradeDiagnosticInput
    {
        public object Customer, Target;
        public bool CustomerArrived, TradeUiOpen, WaitingForAdvance, ConfirmationOpen;
        public string StorePhase, KnownBlocker, Header, Subtitle, Section, ConfirmationText;
        public int? TargetUnits;
        public string[] FooterLabels;
    }

    [Serializable]
    public sealed class TradeDiagnosticSnapshot
    {
        public string world_epoch, availability, stage, store_phase, customer_ref, target_ref;
        public string known_blocker, header, subtitle, section, confirmation_text;
        public string[] footer_labels = Array.Empty<string>();
        public bool customer_present, customer_arrived, trade_ui_open, waiting_for_advance, confirmation_open;
        public int? target_units;
        public int diagnostic_revision;
    }

    public sealed class TradeDiagnostic
    {
        private object customer, target;
        private string world, customerRef, targetRef, signature;
        private long nextReference;
        private int revision;

        public TradeDiagnosticSnapshot Capture(string worldEpoch, bool ready, Func<TradeDiagnosticInput> read)
        {
            if (world != worldEpoch)
            {
                ResetReferences();
                world = worldEpoch;
                revision = 0;
                signature = null;
            }
            var result = new TradeDiagnosticSnapshot { world_epoch = worldEpoch };
            if (!ready || string.IsNullOrEmpty(worldEpoch))
            {
                ResetReferences();
                result.availability = "not_ready";
                result.stage = "unavailable";
                return Version(result);
            }
            try
            {
                var input = read();
                if (input == null) throw new InvalidOperationException();
                if (!ReferenceEquals(customer, input.Customer))
                {
                    ResetReferences();
                    customer = input.Customer;
                    customerRef = customer == null ? null : NewReference("customer");
                }
                // A target is meaningful only for an arrived current customer in the
                // open tablet. The Unity reader additionally checks back-counter membership.
                var visibleTarget = customer != null && input.CustomerArrived && input.TradeUiOpen && input.KnownBlocker == null
                    ? input.Target : null;
                if (!ReferenceEquals(target, visibleTarget))
                {
                    target = visibleTarget;
                    targetRef = target == null ? null : NewReference("target");
                }
                result.availability = "sampled";
                result.store_phase = Limit(input.StorePhase);
                result.customer_ref = customerRef;
                result.target_ref = targetRef;
                result.customer_present = customer != null;
                result.customer_arrived = customer != null && input.CustomerArrived;
                result.target_units = target == null ? null : input.TargetUnits;
                result.trade_ui_open = input.TradeUiOpen;
                result.waiting_for_advance = input.WaitingForAdvance;
                result.known_blocker = Limit(input.KnownBlocker);
                result.confirmation_open = input.TradeUiOpen && input.ConfirmationOpen;
                if (customer != null && input.CustomerArrived && input.TradeUiOpen && input.KnownBlocker == null)
                {
                    result.header = Limit(input.Header);
                    result.subtitle = Limit(input.Subtitle);
                    result.section = Limit(input.Section);
                    result.confirmation_text = result.confirmation_open ? Limit(input.ConfirmationText) : null;
                    int count = Math.Min(input.FooterLabels?.Length ?? 0, 4);
                    result.footer_labels = new string[count];
                    for (int i = 0; i < count; i++) result.footer_labels[i] = Limit(input.FooterLabels[i]);
                }
                result.stage = customer == null ? "no_customer" : !input.CustomerArrived ? "arrival_pending" :
                    result.confirmation_open ? "confirmation_visible" : input.TradeUiOpen ? "trade_tablet_visible" :
                    input.WaitingForAdvance ? "dialogue_waiting" : "customer_present";
            }
            catch (Exception)
            {
                // Never retain stale identities or exception messages containing game data.
                ResetReferences();
                result = new TradeDiagnosticSnapshot { world_epoch = worldEpoch,
                    availability = "read_error", stage = "unavailable" };
            }
            return Version(result);
        }

        private string NewReference(string kind) => world + ":" + kind + ":" + (++nextReference);
        private void ResetReferences() { customer = target = null; customerRef = targetRef = null; }
        private static string Limit(string value) => value == null ? null : value.Substring(0, Math.Min(value.Length, 256));

        private TradeDiagnosticSnapshot Version(TradeDiagnosticSnapshot value)
        {
            var b = new StringBuilder();
            // Length prefixes prevent label content from colliding with field boundaries.
            foreach (string part in new[] { value.world_epoch, value.availability, value.stage, value.store_phase,
                value.customer_ref, value.target_ref, value.known_blocker, value.header, value.subtitle,
                value.section, value.confirmation_text, value.customer_present.ToString(), value.customer_arrived.ToString(),
                value.trade_ui_open.ToString(), value.waiting_for_advance.ToString(), value.confirmation_open.ToString(),
                value.target_units?.ToString() }) Append(b, part);
            b.Append(value.footer_labels.Length).Append(':');
            foreach (var label in value.footer_labels) Append(b, label);
            string next = b.ToString();
            if (next != signature) { signature = next; revision++; }
            value.diagnostic_revision = revision;
            return value;
        }
        private static void Append(StringBuilder b, string value) => b.Append(value == null ? -1 : value.Length).Append(':').Append(value);
    }
}
