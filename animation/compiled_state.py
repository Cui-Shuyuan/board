#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Shared logical-state replay helpers for compiled v2 tracks.

The compiler, checkers, audit and runtime-facing tools all need to answer the
same questions about ``state_ops`` and cue state inheritance.  This module is
the single place for those mechanics; checkers keep their own rule assertions.
"""
from __future__ import annotations

from typing import Iterator


def components_by_id(snapshot) -> dict:
    """Return ``{component Id: component}`` for a snapshot or component list."""
    if isinstance(snapshot, dict):
        components = snapshot.get("components") or []
    else:
        components = snapshot or []
    return {comp.get("Id"): comp for comp in components if isinstance(comp, dict)}


def apply_state_ops(start_state, ops, t: float = 1e9) -> dict:
    """Apply compile-time ``put`` / ``remove`` ops with ``at <= t``.

    Returns the same id->component mapping the compiler treats as the logical
    state.  Other op kinds are ignored by design (state_ops is already concrete).
    """
    items = components_by_id(start_state)
    for op in ops or []:
        if op.get("at", 0.0) > t + 1e-9:
            break
        if op.get("op") == "put" and (op.get("item") or {}).get("Id"):
            items[op["item"]["Id"]] = op["item"]
        elif op.get("op") == "remove" and op.get("item_id"):
            items.pop(op["item_id"], None)
    return items


def iter_renderable_states(cue: dict) -> Iterator[tuple[float, list[dict], bool]]:
    """Yield every logical state the runtime can render for one compiled cue.

    Yields ``(at, components, is_start)``: the start snapshot first, then one
    entry after each applied put/remove state op.  Unknown/malformed ops are
    skipped in the same way the concrete state-op applier skips them.
    """
    items = components_by_id(cue.get("start_state") or {})
    yield 0.0, list(items.values()), True
    for op in cue.get("state_ops") or []:
        if op.get("op") == "put" and (op.get("item") or {}).get("Id"):
            item = op["item"]
            items = dict(items)
            items[item["Id"]] = item
        elif op.get("op") == "remove" and op.get("item_id"):
            items = dict(items)
            items.pop(op["item_id"], None)
        else:
            continue
        yield op.get("at", 0.0), list(items.values()), False


def resolve_effective_state_source(
    cue: dict,
    *,
    by_id: dict,
    prev_id: str | None = None,
    strict: bool = False,
    implicit_initial: bool = False,
) -> dict:
    """Resolve one cue's effective state source.

    Resolution order is: explicit ``entry`` -> ``cut``/``world_cut`` -> explicit
    ``parent`` -> previous cue in track order -> initial.  Returning a label
    keeps audit/compiler diagnostics descriptive; callers only need ``kind``
    and ``id`` to find the actual snapshot.
    """
    cid = cue.get("id")
    entry = cue.get("entry")
    if entry:
        if str(entry) == "initial":
            return {"kind": "initial", "id": None, "label": "entry:initial"}
        if entry in by_id:
            return {"kind": "cue", "id": str(entry), "label": f"entry:{entry}"}
        if strict:
            raise ValueError(f"cue {cid}: entry source {entry!r} does not exist")
        # The audit wants to surface this later as a missing compiled snapshot.
        return {"kind": "cue", "id": str(entry), "label": f"entry:{entry}"}

    transition = cue.get("transition", "continue")
    if transition in ("cut", "world_cut"):
        return {"kind": "initial", "id": None, "label": "cut:initial"}

    parent = cue.get("parent")
    if parent:
        if parent in by_id:
            return {"kind": "cue", "id": str(parent), "label": f"parent:{parent}"}
        if strict:
            raise ValueError(f"cue {cid}: parent {parent!r} does not exist")
        # Non-strict falls through to previous-cue order, matching audit.

    if prev_id and prev_id in by_id:
        return {"kind": "cue", "id": str(prev_id), "label": f"prev:{prev_id}"}

    if implicit_initial:
        return {"kind": "initial", "id": None, "label": "implicit:initial"}
    return {"kind": "initial", "id": None, "label": "initial"}
