"""Research-only, independent capacity fixture; this is not production logic.

The model intentionally handles only a flat rectangular grid and one-cell,
single-unit, non-stackable items. It does not load or call the game assembly,
GraphUtils.CanAccept, TryFindOneValidInventorySlot, or any real GameItem.
Passing these fixtures demonstrates only the math under the explicit
preconditions below, not equivalence with the game implementation.
"""

from dataclasses import dataclass
from typing import TypeAlias
import unittest


Cell: TypeAlias = tuple[int, int]


@dataclass(frozen=True)
class ItemFixture:
    ref: str
    cells: frozenset[Cell] = frozenset({(0, 0)})
    unit_count: int = 1
    stackable: bool = False
    has_nested_inventory: bool = False


@dataclass(frozen=True)
class ScopeFixture:
    """Facts an external caller would have to prove before using this model."""

    flat_visible_target: bool = False
    regular_grid: bool = False
    source_removal_unrestricted: bool = False
    insertion_and_state_rules_known: bool = False
    no_unmodeled_callbacks_or_filters: bool = False
    post_purchase_shape_unchanged: bool = False
    no_stack_targets: bool = False
    no_hidden_or_nested_items: bool = False


@dataclass(frozen=True)
class Plan:
    safe: bool
    reason: str
    # Ordered exactly as the simulated native batch loop would process items.
    placements: tuple[tuple[str, Cell], ...] = ()


def _is_int(value: object) -> bool:
    return type(value) is int


def preflight_one_cell_batch(
    *,
    width: int,
    height: int,
    occupied: tuple[Cell, ...],
    ordered_purchase_items: tuple[ItemFixture, ...],
    scope: ScopeFixture,
) -> Plan:
    """Pure conservative fixture for a deliberately tiny supported subset.

    `ordered_purchase_items` is the list passed to BuyMultipleItem. The game
    loops it from Count-1 down to 0, so simulation order is reversed here.
    The function fails closed and never edits caller-owned fixtures.
    """

    if not _is_int(width) or not _is_int(height) or width <= 0 or height <= 0:
        return Plan(False, "unknown_or_invalid_grid")
    if not isinstance(scope, ScopeFixture):
        return Plan(False, "scope_precondition_unverified")
    if not all(
        getattr(scope, name) is True
        for name in ScopeFixture.__dataclass_fields__
    ):
        return Plan(False, "scope_precondition_unverified")

    if not isinstance(occupied, tuple):
        return Plan(False, "unknown_or_out_of_bounds_occupancy")
    if any(
        not isinstance(cell, tuple)
        or len(cell) != 2
        or not _is_int(cell[0])
        or not _is_int(cell[1])
        or not (0 <= cell[0] < width and 0 <= cell[1] < height)
        for cell in occupied
    ):
        return Plan(False, "unknown_or_out_of_bounds_occupancy")
    cells = set(occupied)
    if len(cells) != len(occupied):
        return Plan(False, "duplicate_occupied_cell")

    if not isinstance(ordered_purchase_items, tuple) or any(
        not isinstance(item, ItemFixture) for item in ordered_purchase_items
    ):
        return Plan(False, "unknown_item_ref")
    refs = [item.ref for item in ordered_purchase_items]
    if any(not isinstance(ref, str) or not ref for ref in refs):
        return Plan(False, "unknown_item_ref")
    if len(set(refs)) != len(refs):
        return Plan(False, "duplicate_item_ref")
    for item in ordered_purchase_items:
        if item.cells != frozenset({(0, 0)}):
            return Plan(False, "unsupported_shape_or_orientation")
        if item.unit_count != 1 or item.stackable:
            return Plan(False, "unsupported_stack_or_quantity")
        if item.has_nested_inventory:
            return Plan(False, "nested_inventory_unverified")

    if len(ordered_purchase_items) > width * height - len(cells):
        return Plan(False, "insufficient_shared_capacity")

    # This placement rule is sufficient for feasibility only because every
    # item is a 1x1 non-stackable object and every grid cell is equivalent.
    # It deliberately does not model game-specific slot priorities.
    placements: list[tuple[str, Cell]] = []
    for item in reversed(ordered_purchase_items):
        free = next(
            ((x, y) for y in range(height) for x in range(width) if (x, y) not in cells),
            None,
        )
        if free is None:  # defensive; capacity was checked above
            return Plan(False, "insufficient_shared_capacity")
        cells.add(free)
        placements.append((item.ref, free))

    return Plan(True, "fixture_preconditions_satisfied", tuple(placements))


def naive_independent_can_accept(
    *, width: int, height: int, occupied: tuple[Cell, ...], item: ItemFixture
) -> bool:
    """Counterexample helper: independently checks each item against same state."""

    return len(set(occupied)) < width * height and item.cells == frozenset({(0, 0)})


class CapacityModelFixtureTests(unittest.TestCase):
    def setUp(self):
        self.scope = ScopeFixture(
            flat_visible_target=True,
            regular_grid=True,
            source_removal_unrestricted=True,
            insertion_and_state_rules_known=True,
            no_unmodeled_callbacks_or_filters=True,
            post_purchase_shape_unchanged=True,
            no_stack_targets=True,
            no_hidden_or_nested_items=True,
        )

    def test_shared_occupancy_uses_reverse_native_batch_order_without_mutation(self):
        occupied = ((0, 0),)
        items = (ItemFixture("first"), ItemFixture("second"))
        before = (occupied, items, self.scope)

        plan = preflight_one_cell_batch(
            width=3,
            height=1,
            occupied=occupied,
            ordered_purchase_items=items,
            scope=self.scope,
        )

        self.assertTrue(plan.safe)
        self.assertEqual(plan.placements, (("second", (1, 0)), ("first", (2, 0))))
        self.assertEqual((occupied, items, self.scope), before)

    def test_independent_acceptance_does_not_prove_whole_batch_capacity(self):
        # Each item fits when tested alone against the same initial state, but
        # both cannot fit in the one remaining cell.
        occupied = ((0, 0),)
        items = (ItemFixture("a"), ItemFixture("b"))
        before = (occupied, items, self.scope)
        self.assertTrue(all(
            naive_independent_can_accept(width=2, height=1, occupied=occupied, item=item)
            for item in items
        ))

        plan = preflight_one_cell_batch(
            width=2,
            height=1,
            occupied=occupied,
            ordered_purchase_items=items,
            scope=self.scope,
        )
        self.assertFalse(plan.safe)
        self.assertEqual(plan.reason, "insufficient_shared_capacity")
        self.assertEqual(plan.placements, ())
        self.assertEqual((occupied, items, self.scope), before)

    def test_free_cell_count_does_not_prove_multicell_shape_fits(self):
        wide = ItemFixture("wide", cells=frozenset({(0, 0), (1, 0)}))
        plan = preflight_one_cell_batch(
            width=2,
            height=1,
            occupied=(),
            ordered_purchase_items=(wide,),
            scope=self.scope,
        )
        self.assertFalse(plan.safe)
        self.assertEqual(plan.reason, "unsupported_shape_or_orientation")

    def test_duplicate_cells_refs_unknown_rules_and_nesting_fail_closed(self):
        cases = (
            (dict(occupied=((0, 0), (0, 0))), self.scope, "duplicate_occupied_cell"),
            (dict(ordered_purchase_items=(ItemFixture("x"), ItemFixture("x"))),
             self.scope, "duplicate_item_ref"),
            (dict(scope=ScopeFixture()), None, "scope_precondition_unverified"),
            (dict(ordered_purchase_items=(ItemFixture("box", has_nested_inventory=True),)),
             self.scope, "nested_inventory_unverified"),
        )
        for kwargs, scope_override, expected in cases:
            with self.subTest(expected=expected):
                options = {
                    "width": 2,
                    "height": 1,
                    "occupied": (),
                    "ordered_purchase_items": (ItemFixture("x"),),
                    "scope": self.scope,
                }
                options.update(kwargs)
                if scope_override is not None:
                    options["scope"] = scope_override
                plan = preflight_one_cell_batch(**options)
                self.assertFalse(plan.safe)
                self.assertEqual(plan.reason, expected)
                self.assertEqual(plan.placements, ())

    def test_stack_and_quantity_rules_are_outside_model(self):
        for item in (ItemFixture("stack", stackable=True), ItemFixture("qty", unit_count=2)):
            with self.subTest(item=item):
                plan = preflight_one_cell_batch(
                    width=2,
                    height=1,
                    occupied=(),
                    ordered_purchase_items=(item,),
                    scope=self.scope,
                )
                self.assertFalse(plan.safe)
                self.assertEqual(plan.reason, "unsupported_stack_or_quantity")


if __name__ == "__main__":
    unittest.main()
