using System;
using System.Linq;
using PawnPilot;

public static class VerificationDiagnosticTests
{
    public static void Run(Action<string, bool> check)
    {
        var item = new object();
        var movedProbe = new VerificationDiagnostic();
        var first = movedProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "front") }));
        var moved = movedProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "back") }));
        check("diagnostic_same_object_keeps_opaque_ref_after_move",
            first.items[0].item_ref == moved.items[0].item_ref && moved.items[0].container == "back");

        var closedTablet = Sample(new[] { Item(item, "player") });
        closedTablet.TabletOpen = false;
        var closed = movedProbe.Capture("world-a", true, () => closedTablet);
        check("diagnostic_closed_tablet_keeps_visible_inventory_without_stale_quote",
            closed.items.Length == 1 && closed.rows.Length == 0 && closed.footer.Length == 0);

        var disappearProbe = new VerificationDiagnostic();
        var original = disappearProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "front") }));
        disappearProbe.Capture("world-a", true, () => Sample(Array.Empty<VerificationItemInput>()));
        var returned = disappearProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "front") }));
        check("diagnostic_absent_then_returned_item_gets_new_ref",
            original.items[0].item_ref != returned.items[0].item_ref);

        var epochProbe = new VerificationDiagnostic();
        var epochOne = epochProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "front") }));
        var epochTwo = epochProbe.Capture("world-b", true, () => Sample(new[] { Item(item, "front") }));
        check("diagnostic_world_change_rotates_item_ref", epochOne.items[0].item_ref != epochTwo.items[0].item_ref);

        var notReadyProbe = new VerificationDiagnostic();
        bool readerCalled = false;
        var notReady = notReadyProbe.Capture("world-a", false, () => { readerCalled = true; return Sample(new[] { Item(item, "front") }); });
        check("diagnostic_not_ready_skips_reader_and_clears_evidence",
            !readerCalled && notReady.availability == "unavailable" && notReady.items.Length == 0 && notReady.customer_ref == null);

        var blockedProbe = new VerificationDiagnostic();
        var blockedInput = Sample(new[] { Item(item, "front") });
        blockedInput.KnownBlocker = "escape_menu";
        var blocked = blockedProbe.Capture("world-a", true, () => blockedInput);
        check("diagnostic_known_blocker_hides_items_rows_footer",
            blocked.availability == "blocked" && blocked.ui_consistency == "blocked" &&
            blocked.items.Length == 0 && blocked.rows.Length == 0 && blocked.footer.Length == 0);

        var confirmationProbe = new VerificationDiagnostic();
        var confirmationInput = Sample(new[] { Item(item, "front") });
        confirmationInput.ConfirmationOpen = true;
        var confirmation = confirmationProbe.Capture("world-a", true, () => confirmationInput);

        var dialogueProbe = new VerificationDiagnostic();
        var dialogueInput = Sample(new[] { Item(item, "front") });
        dialogueInput.DialogueActive = true;
        dialogueInput.WaitingForAdvance = true;
        var dialogue = dialogueProbe.Capture("world-a", true, () => dialogueInput);
        check("diagnostic_confirmation_dialogue_and_wait_block_projection",
            confirmation.availability == "blocked" && confirmation.blocker == "confirmation_open" &&
            dialogue.availability == "blocked" && dialogue.items.Length == 0 &&
            dialogue.blocker.Contains("dialogue_active") && dialogue.blocker.Contains("waiting_for_advance"));

        var unknownUiProbe = new VerificationDiagnostic();
        var unknownUiInput = Sample(new[] { Item(item, "front") });
        unknownUiInput.UnknownUi = true;
        var unknownUi = unknownUiProbe.Capture("world-a", true, () => unknownUiInput);
        check("diagnostic_unknown_ui_is_unverified_but_keeps_visible_subset",
            unknownUi.availability == "unverified" && unknownUi.blocker == null &&
            unknownUi.ui_consistency == "unverified" && unknownUi.items.Length == 1);

        var rowsProbe = new VerificationDiagnostic();
        var rowsInput = Sample(Array.Empty<VerificationItemInput>());
        rowsInput.Rows = new[]
        {
            new VerificationRowInput { Name = "Visible row", Price = "$12", FullyVisible = true },
            new VerificationRowInput { Name = "Clipped row", Price = "$34", FullyVisible = false }
        };
        var rows = rowsProbe.Capture("world-a", true, () => rowsInput);
        check("diagnostic_only_fully_visible_rows_are_projected",
            rows.rows.Length == 1 && rows.rows[0].name == "Visible row" && rows.rows[0].price == "$12");

        var projectionProbe = new VerificationDiagnostic();
        var projected = projectionProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "back") }));
        check("diagnostic_quote_unverified_and_item_fields_are_primitive_opaque_projection",
            projected.quote_binding == "unverified" && projected.ui_consistency == "unverified" &&
            projected.items[0].item_ref.StartsWith("item-", StringComparison.Ordinal) &&
            projected.items[0].item_ref != item.ToString() && projected.items[0].container == "back" &&
            projected.items[0].units == 2 && projected.items[0].owned && projected.items[0].shape_min_x == 3 && projected.items[0].shape_min_y == 4 &&
            projected.visibility_basis == "geometric_candidates_require_screenshot");

        var exceptionProbe = new VerificationDiagnostic();
        var beforeException = exceptionProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "front") }));
        var exceptionSample = exceptionProbe.Capture("world-a", true, () => throw new InvalidOperationException("private failure text"));
        var afterException = exceptionProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "front") }));
        check("diagnostic_reader_exception_is_redacted_and_clears_refs",
            exceptionSample.blocker == "diagnostic_reader_failed" &&
            !exceptionSample.blocker.Contains("private failure text") &&
            beforeException.items[0].item_ref != afterException.items[0].item_ref);

        var invalidProbe = new VerificationDiagnostic();
        var invalidUnits = invalidProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "front", 0) }));

        var duplicateProbe = new VerificationDiagnostic();
        var duplicate = duplicateProbe.Capture("world-a", true, () => Sample(new[] { Item(item, "front"), Item(item, "front") }));

        var multiContainerProbe = new VerificationDiagnostic();
        var multiContainer = multiContainerProbe.Capture("world-a", true,
            () => Sample(new[] { Item(item, "front"), Item(item, "back") }));
        check("diagnostic_invalid_units_duplicate_identity_and_multiple_containers_fail_closed",
            invalidUnits.availability == "unavailable" && invalidUnits.items.Length == 0 && invalidUnits.blocker == "invalid_item_units" &&
            duplicate.availability == "unavailable" && duplicate.blocker == "duplicate_item_identity" &&
            multiContainer.availability == "unavailable" && multiContainer.blocker == "item_in_multiple_containers");

        var limitProbe = new VerificationDiagnostic();
        var tooManyRowsInput = Sample(Array.Empty<VerificationItemInput>());
        tooManyRowsInput.Rows = Enumerable.Range(0, 65)
            .Select(i => new VerificationRowInput { Name = "row" + i, Price = "1", FullyVisible = true }).ToArray();
        var tooManyRows = limitProbe.Capture("world-a", true, () => tooManyRowsInput);
        var tooManyItemsInput = Sample(Enumerable.Range(0, 65).Select(_ => Item(new object(), "front")).ToArray());
        var tooManyItems = limitProbe.Capture("world-a", true, () => tooManyItemsInput);
        check("diagnostic_more_than_64_items_or_rows_is_not_silently_truncated",
            tooManyRows.availability == "unavailable" && tooManyRows.rows.Length == 0 && tooManyRows.blocker == "sample_limit_exceeded" &&
            tooManyItems.availability == "unavailable" && tooManyItems.items.Length == 0 && tooManyItems.blocker == "sample_limit_exceeded");

        var oversizedProbe = new VerificationDiagnostic();
        var oversizedInput = Sample(Array.Empty<VerificationItemInput>());
        oversizedInput.Rows = new[] { new VerificationRowInput { Name = new string('x', 257), Price = "1", FullyVisible = false } };
        var oversized = oversizedProbe.Capture("world-a", true, () => oversizedInput);
        check("diagnostic_oversized_hidden_string_invalidates_sample",
            oversized.availability == "unavailable" && oversized.blocker == "invalid_string");

        var missingListProbe = new VerificationDiagnostic();
        var missingListInput = Sample(Array.Empty<VerificationItemInput>());
        missingListInput.Items = null;
        var missingList = missingListProbe.Capture("world-a", true, () => missingListInput);
        check("diagnostic_null_item_array_is_incomplete_not_empty",
            missingList.availability == "unavailable" && missingList.blocker == "incomplete_sample");

        var copyProbe = new VerificationDiagnostic();
        var source = Sample(new[] { Item(item, "front") });
        source.Rows = new[] { new VerificationRowInput { Name = "source name", Price = "$5", FullyVisible = true } };
        source.Footer = new[] { "source footer" };
        var copied = copyProbe.Capture("world-a", true, () => source);
        source.Items[0].Container = "changed";
        source.Rows[0].Name = "changed";
        source.Footer[0] = "changed";
        source.Items[0] = Item(new object(), "other");
        check("diagnostic_projection_detaches_source_fields_and_arrays",
            copied.items[0].container == "front" && copied.rows[0].name == "source name" && copied.footer[0] == "source footer");
    }

    private static VerificationDiagnosticInput Sample(VerificationItemInput[] items) => new VerificationDiagnosticInput
    {
        Customer = new object(),
        TabletOpen = true,
        Items = items,
        Rows = new[] { new VerificationRowInput { Name = "Visible offer", Price = "$12", FullyVisible = true } },
        Footer = new[] { "Total $12" }
    };

    private static VerificationItemInput Item(object identity, string container, int units = 2) => new VerificationItemInput
    {
        Identity = identity,
        Container = container,
        Units = units,
        Owned = true,
        X = 3,
        Y = 4
    };
}
