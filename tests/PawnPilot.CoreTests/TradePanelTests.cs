using System;
using System.Collections.Generic;
using PawnPilot;

public static class TradePanelTests
{
    public static void Run(Action<string, bool> check)
    {
        var command = new Command { schema_version = 3, action = "open_trade_panel",
            arguments = new ActionArguments { customer_ref = "customer:1" } };
        check("panel_action_registered_and_customer_only", BusinessActionPolicy.IsEnabled(command.action) &&
            BusinessActionPolicy.ArgumentsValid(command.action, command.arguments) &&
            !BusinessActionPolicy.ArgumentsValid(command.action, new ActionArguments { customer_ref = "customer:1", mode = "normal" }));
        foreach (var direction in new[] { "sell", "buy" })
        {
            var before = State(direction, "closed", 1);
            var after = State(direction, direction, 2);
            check("panel_" + direction + "_local_admission", BusinessActionPolicy.Validate(command, before) == null);
            check("panel_" + direction + "_visible_quote_success", BusinessActionPolicy.Verify(command, before, after).outcome == "succeeded");
            check("panel_" + direction + "_already_open_rejected", BusinessActionPolicy.Validate(command, after) != null);
            after.business.details.customer.trade_panel = direction == "buy" ? "sell" : "buy";
            check("panel_" + direction + "_wrong_mode_not_success", !BusinessActionPolicy.Postcondition(command, before, after));
            after.business.details.customer.trade_panel = direction;
            after.business.details.quote.displayed_total = null;
            check("panel_" + direction + "_no_quote_not_success", !BusinessActionPolicy.Postcondition(command, before, after));
        }
        var snapshot = State("sell", "closed", 1);
        snapshot.business.customer_ref = "customer:other";
        check("panel_stale_customer_rejected", BusinessActionPolicy.Validate(command, snapshot) != null);
        snapshot = State("sell", "closed", 1);
        snapshot.business.details.customer.dialogue_ref = "dialogue:1";
        check("panel_dialogue_blocks", BusinessActionPolicy.Validate(command, snapshot) != null);
        snapshot = State("sell", "closed", 1);
        snapshot.business.details.customer.arrived = false;
        check("panel_arrival_blocks", BusinessActionPolicy.Validate(command, snapshot) != null);
        snapshot = State("other", "closed", 1);
        check("panel_special_direction_blocks", BusinessActionPolicy.Validate(command, snapshot) != null);
        snapshot = State("sell", "closed", 1);
        snapshot.business.details.ui.blockers.Add("generic_confirmation");
        check("panel_confirmation_blocks", BusinessActionPolicy.Validate(command, snapshot) != null);
        var changed = State("sell", "sell", 2);
        changed.business.customer_ref = "customer:other";
        check("panel_customer_replacement_unknown", BusinessActionPolicy.Verify(command, State("sell", "closed", 1), changed).outcome == "uncertain");
        var copy = BusinessWire.Clone(State("buy", "buy", 2).business.details);
        check("panel_wire_copy_preserves_mode", copy.customer.trade_panel == "buy");
        var legacy = new BusinessObservation { details = new BusinessDetails { schema_version = 1 } };
        legacy.details.customer.trade_panel = "closed";
        check("panel_schema1_rejects_extension", !BusinessWire.Validate(legacy));
    }

    private static Snapshot State(string direction, string panel, int revision) => new Snapshot
    {
        schema_version = 3, loaded = true, ready = true, world_epoch = "world:1", scene = "shop", phase = "OPEN",
        revision = revision, version = "world:1:" + revision, cash = 550,
        interaction = new InteractionObservation { consistency = "unverified" },
        business = new BusinessObservation { customer_ref = "customer:1", details = new BusinessDetails
        {
            schema_version = 2,
            customer = new CustomerState { presence = "present", arrived = true, trade_type = direction,
                trade_panel = panel, phase = panel == "closed" ? "unknown" : "trading", dialogue_stage = "absent" },
            quote = new QuoteState { status = "unverified", mode = "normal", displayed_total = 0 },
            ui = new BusinessUiState { status = "unverified", coverage = "known_roots_only",
                blockers = new List<string> { "global_ui_stack_unobservable", "quote_rebuild_completion_unobservable" } }
        } },
        legal_actions = new List<LegalAction> { new LegalAction { action = "open_trade_panel",
            arguments = new ActionArguments { customer_ref = "customer:1" } } }
    };
}
