using System;
using System.Reflection;

namespace PawnPilot
{
    public static class TradeDiagnosticTests
    {
        public static void Run(Action<string, bool> check)
        {
            if (check == null) throw new ArgumentNullException(nameof(check));

            var diagnostic = new TradeDiagnostic();
            int reads = 0;
            var notReady = diagnostic.Capture("world-a", false, () =>
            {
                reads++;
                return new TradeDiagnosticInput();
            });
            check("trade_not_ready_skips_reader", reads == 0 && notReady.availability == "not_ready");
            var emptyEpoch = diagnostic.Capture("", true, () =>
            {
                reads++;
                return new TradeDiagnosticInput();
            });
            check("trade_empty_epoch_skips_reader", reads == 0 && emptyEpoch.availability == "not_ready");

            object customer = new object();
            object target = new object();
            TradeDiagnosticInput activeInput = Active(customer, target);
            var firstWorld = diagnostic.Capture("world-a", true, () => activeInput);
            var sameWorld = diagnostic.Capture("world-a", true, () => activeInput);
            var newWorldSameObjects = diagnostic.Capture("world-b", true, () => activeInput);
            check("trade_world_invalidation_relabels_same_objects",
                firstWorld.customer_ref != null && firstWorld.target_ref != null &&
                sameWorld.customer_ref == firstWorld.customer_ref && sameWorld.target_ref == firstWorld.target_ref &&
                newWorldSameObjects.world_epoch == "world-b" &&
                newWorldSameObjects.customer_ref != firstWorld.customer_ref &&
                newWorldSameObjects.target_ref != firstWorld.target_ref &&
                newWorldSameObjects.diagnostic_revision == 1);

            var replacementCustomer = new object();
            activeInput.Customer = replacementCustomer;
            var replaced = diagnostic.Capture("world-b", true, () => activeInput);
            activeInput.Customer = null;
            var absent = diagnostic.Capture("world-b", true, () => activeInput);
            activeInput.Customer = replacementCustomer;
            var returned = diagnostic.Capture("world-b", true, () => activeInput);
            check("trade_customer_loss_and_replacement_references",
                replaced.customer_ref != newWorldSameObjects.customer_ref &&
                replaced.target_ref != newWorldSameObjects.target_ref &&
                absent.stage == "no_customer" && absent.customer_ref == null && absent.target_ref == null &&
                returned.customer_ref != replaced.customer_ref && returned.target_ref != replaced.target_ref);

            var empty = new TradeDiagnostic();
            var noCustomer = empty.Capture("world-empty", true, () => new TradeDiagnosticInput
            {
                StorePhase = "MORNING",
                TradeUiOpen = true,
                Header = "stale customer label",
                FooterLabels = Array.Empty<string>()
            });
            check("trade_absent_customer_is_empty", noCustomer.availability == "sampled" &&
                noCustomer.stage == "no_customer" && !noCustomer.customer_present &&
                noCustomer.customer_ref == null && noCustomer.target_ref == null &&
                noCustomer.target_units == null && noCustomer.footer_labels.Length == 0 && noCustomer.header == null);

            var revisions = new TradeDiagnostic();
            var quote = Active(new object(), new object());
            quote.Header = "Offer";
            quote.Subtitle = "One item";
            quote.Section = "Trade";
            quote.ConfirmationText = "Confirm";
            quote.FooterLabels = new[] { "Accept", "Decline" };
            quote.TargetUnits = 1;
            var baseQuote = revisions.Capture("world-revisions", true, () => quote);
            var unchangedQuote = revisions.Capture("world-revisions", true, () => quote);
            quote.TradeUiOpen = false;
            quote.WaitingForAdvance = true;
            var waiting = revisions.Capture("world-revisions", true, () => quote);
            quote.WaitingForAdvance = false;
            quote.TradeUiOpen = true;
            var reopenedQuote = revisions.Capture("world-revisions", true, () => quote);
            quote.Subtitle = "Updated quote";
            var changedQuoteText = revisions.Capture("world-revisions", true, () => quote);
            quote.TargetUnits = 2;
            var changedQuote = revisions.Capture("world-revisions", true, () => quote);
            quote.FooterLabels[0] = "Take it";
            var changedFooter = revisions.Capture("world-revisions", true, () => quote);
            quote.ConfirmationOpen = true;
            var confirmation = revisions.Capture("world-revisions", true, () => quote);
            check("trade_transition_quote_footer_revisions",
                unchangedQuote.diagnostic_revision == baseQuote.diagnostic_revision &&
                waiting.stage == "dialogue_waiting" && waiting.diagnostic_revision > baseQuote.diagnostic_revision &&
                reopenedQuote.stage == "trade_tablet_visible" &&
                reopenedQuote.diagnostic_revision > waiting.diagnostic_revision &&
                changedQuoteText.diagnostic_revision > reopenedQuote.diagnostic_revision &&
                changedQuote.diagnostic_revision > changedQuoteText.diagnostic_revision && changedQuote.target_units == 2 &&
                changedFooter.diagnostic_revision > changedQuote.diagnostic_revision &&
                confirmation.stage == "confirmation_visible" &&
                confirmation.diagnostic_revision > changedFooter.diagnostic_revision);

            var errors = new TradeDiagnostic();
            object errorCustomer = new object();
            object errorTarget = new object();
            var beforeError = errors.Capture("world-errors", true, () => Active(errorCustomer, errorTarget));
            var failed = errors.Capture("world-errors", true, () => throw new InvalidOperationException("must not escape"));
            var afterError = errors.Capture("world-errors", true, () => Active(errorCustomer, errorTarget));
            check("trade_exception_clears_refs_and_recovers",
                failed.availability == "read_error" && failed.stage == "unavailable" &&
                failed.customer_ref == null && failed.target_ref == null &&
                afterError.availability == "sampled" &&
                afterError.customer_ref != beforeError.customer_ref &&
                afterError.target_ref != beforeError.target_ref);

            var bounded = new TradeDiagnostic().Capture("world-bounds", true, () => new TradeDiagnosticInput
            {
                Customer = new object(),
                CustomerArrived = true,
                TradeUiOpen = true,
                StorePhase = new string('p', 300),
                KnownBlocker = null,
                Header = new string('h', 300),
                Subtitle = new string('s', 300),
                Section = new string('c', 300),
                ConfirmationOpen = true,
                ConfirmationText = new string('x', 300),
                FooterLabels = new[] { new string('1', 300), "2", "3", "4", "5" }
            });
            bool boundedFields = bounded.store_phase?.Length == 256 && bounded.header?.Length == 256 &&
                bounded.subtitle?.Length == 256 && bounded.section?.Length == 256 &&
                bounded.confirmation_text?.Length == 256 && bounded.footer_labels.Length == 4;
            foreach (string label in bounded.footer_labels) boundedFields &= label.Length <= 256;
            var blocker = new TradeDiagnostic().Capture("world-blocker", true, () => new TradeDiagnosticInput
            {
                Customer = new object(), CustomerArrived = true, TradeUiOpen = true,
                Target = new object(), TargetUnits = 1,
                KnownBlocker = new string('b', 300)
            });
            boundedFields &= blocker.known_blocker?.Length == 256 && blocker.header == null &&
                blocker.subtitle == null && blocker.section == null && blocker.footer_labels.Length == 0 &&
                blocker.target_ref == null && blocker.target_units == null;
            check("trade_labels_are_bounded", boundedFields);

            var expectedFields = new[]
            {
                "world_epoch", "availability", "stage", "store_phase", "customer_ref", "target_ref",
                "known_blocker", "header", "subtitle", "section", "confirmation_text", "footer_labels",
                "customer_present", "customer_arrived", "trade_ui_open", "waiting_for_advance",
                "confirmation_open", "target_units", "diagnostic_revision"
            };
            var actualFields = typeof(TradeDiagnosticSnapshot).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            bool safeFields = actualFields.Length == expectedFields.Length;
            foreach (var field in actualFields)
            {
                bool knownName = Array.IndexOf(expectedFields, field.Name) >= 0;
                bool safeType = field.FieldType == typeof(string) || field.FieldType == typeof(string[]) ||
                    field.FieldType == typeof(bool) || field.FieldType == typeof(int) ||
                    field.FieldType == typeof(int?);
                safeFields &= knownName && safeType;
            }
            check("trade_snapshot_is_strict_primitive_whitelist", safeFields);

            var sourceLabels = new[] { "Accept", "Decline" };
            var copied = new TradeDiagnostic().Capture("world-copy", true, () => new TradeDiagnosticInput
            {
                Customer = new object(),
                CustomerArrived = true,
                TradeUiOpen = true,
                Header = "Initial",
                FooterLabels = sourceLabels
            });
            sourceLabels[0] = "Changed";
            check("trade_footer_labels_copy_input_array", copied.footer_labels[0] == "Accept");
        }

        private static TradeDiagnosticInput Active(object customer, object target) => new TradeDiagnosticInput
        {
            Customer = customer,
            Target = target,
            CustomerArrived = true,
            TradeUiOpen = true,
            StorePhase = "MORNING",
            TargetUnits = 1,
            FooterLabels = Array.Empty<string>()
        };
    }
}
