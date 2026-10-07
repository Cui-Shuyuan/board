#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Compatibility shim for the old game-generic name.

Game-specific rule ledgers now live under
``content/games/{game}/tutorial/checks/ledger.py`` (P0-3).  Old code that did
``import validate_anim_rules`` still works for the single game repository
layout, but new code should call ``validate_anim_rules_v2.py --game ...``.

Do not add Splendor (or any other game's) rules here.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def _load_default_ledger():
    candidates = sorted((ROOT / "content" / "games").glob("*/tutorial/checks/ledger.py"))
    if len(candidates) != 1:
        raise ImportError(
            "multiple game ledgers found; import the game ledger by path instead: "
            + ", ".join(str(path) for path in candidates)
        )
    path = candidates[0]
    spec = importlib.util.spec_from_file_location(f"_tutorial_ledger_{path.parent.parent.parent.name}", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load ledger: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_ledger_module = _load_default_ledger()

for _name in dir(_ledger_module):
    if not _name.startswith("_"):
        globals()[_name] = getattr(_ledger_module, _name)

del _name
