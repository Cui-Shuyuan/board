#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for animation/audit_anim_v2.py (standard library only)."""
from __future__ import annotations

import copy
import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import audit_anim_v2 as audit  # noqa: E402


def base_components() -> list[dict]:
    components: list[dict] = []
    for level, count in ((1, 40), (2, 30), (3, 20)):
        for index in range(count):
            components.append({
                "Id": f"L{level}_{index}|card_level_{level}#1",
                "Concept": f"development_card_level_{level}",
                "TemplateId": f"market_card_{level}_diamond",
                "Palette": f"card_level_{level}",
                "ZoneId": f"deck_level_{level}",
                "Order": index,
                "parts": [],
            })
    for index in range(3):
        components.append({
            "Id": f"noble_{index}|noble#1",
            "Concept": "noble",
            "TemplateId": f"noble_{index}",
            "Palette": "noble",
            "ZoneId": "noble_market",
            "Order": index,
            "parts": [],
        })
    for color in audit.GEM_COLORS:
        for index in range(4):
            components.append({
                "Id": f"gem_{color}_{index}|gem_{color}#1",
                "Concept": "gem",
                "TemplateId": "gem",
                "Palette": f"gem_{color}",
                "ZoneId": f"gem_supply_{color}",
                "Order": index,
                "parts": [{"key": "color", "value": f"<{color}>"}],
            })
    for index in range(5):
        components.append({
            "Id": f"gold_{index}|gem_gold#1",
            "Concept": "gold",
            "TemplateId": "gem",
            "Palette": "gem_gold",
            "ZoneId": "gold_supply",
            "Order": index,
            "parts": [],
        })
    return components


def marker_component() -> dict:
    return {
        "Id": "starting_marker|marker#1",
        "Concept": "starting_player_marker",
        "TemplateId": "starting_marker",
        "Palette": "marker",
        "ZoneId": "player_holding",
        "Order": 0,
        "parts": [],
    }


def make_docs(
    source_events: list[dict] | None = None,
    start_components: list[dict] | None = None,
    end_components: list[dict] | None = None,
    clips: list[dict] | None = None,
    pointer_resolution: list[dict] | None = None,
    cue_id: str = "cue.1",
) -> tuple[dict, dict]:
    source_events = source_events if source_events is not None else []
    start_components = start_components if start_components is not None else base_components() + [marker_component()]
    end_components = end_components if end_components is not None else copy.deepcopy(start_components)
    clips = clips if clips is not None else []

    track_doc = {
        "game": "splendor",
        "track": "full",
        "trees": [{"id": "main", "world": "real"}],
        "cues": [{
            "id": cue_id,
            "tree": "main",
            "events": copy.deepcopy(source_events),
        }],
    }
    compiled_cue = {
        "id": cue_id,
        "tree": "main",
        "transition": "continue",
        "start_state": {"components": copy.deepcopy(start_components)},
        "end_state": {"components": copy.deepcopy(end_components)},
        "clips": copy.deepcopy(clips),
    }
    if pointer_resolution is not None:
        compiled_cue["pointer_resolution"] = copy.deepcopy(pointer_resolution)
    compiled_doc = {
        "game": "splendor",
        "track": "full",
        "trees": [{"id": "main", "world": "real"}],
        "cues": [compiled_cue],
    }
    return track_doc, compiled_doc


class AuditAnimV2Tests(unittest.TestCase):
    def test_valid_synthetic_track_passes_all_checks(self):
        source_events = [{"op": "highlight", "zone": "player_holding"}]
        clips = [{"kind": "highlight", "item_id": "starting_marker|marker#1"}]
        pointer_resolution = [{
            "event_index": 0,
            "op": "highlight",
            "zone": "player_holding",
            "order": None,
            "matched_count": 1,
            "item_ids": ["starting_marker|marker#1"],
        }]
        track_doc, compiled_doc = make_docs(
            source_events=source_events,
            clips=clips,
            pointer_resolution=pointer_resolution,
        )

        result = audit.audit_documents(track_doc, compiled_doc)

        self.assertEqual([], result["errors"], result["errors"])
        self.assertEqual([], result["warnings"], result["warnings"])
        self.assertEqual(1, result["stats"]["cues_checked"])

    def test_extra_level_1_development_card_reports_conservation_error(self):
        base = base_components() + [marker_component()]
        end = copy.deepcopy(base)
        end.append({
            "Id": "extra_L1|card_level_1#1",
            "Concept": "development_card_level_1",
            "TemplateId": "market_card_1_emerald",
            "Palette": "card_level_1",
            "ZoneId": "player_development",
            "Order": 0,
            "parts": [],
        })
        track_doc, compiled_doc = make_docs(start_components=base, end_components=end)

        result = audit.audit_documents(track_doc, compiled_doc)

        self.assertTrue(result["errors"], result)
        conservation_errors = [item for item in result["errors"] if item["check"] == "conservation"]
        self.assertEqual(1, len(conservation_errors))
        self.assertIn("L1 41/40", conservation_errors[0]["message"])

    def test_missing_card_market_refill_reports_error(self):
        source_events = [{
            "op": "transfer",
            "source": "card_market",
            "destination": "player_development",
            "concept": "development_card_level_1",
            "quantity": 1,
        }]
        track_doc, compiled_doc = make_docs(source_events=source_events)

        result = audit.audit_documents(track_doc, compiled_doc)

        refill_errors = [item for item in result["errors"] if item["check"] == "refill"]
        self.assertEqual(1, len(refill_errors), result)
        self.assertIn("缺一级补牌", refill_errors[0]["message"])
        self.assertEqual("cue.1", refill_errors[0]["cue_id"])

    def test_pointer_empty_resolution_reports_error(self):
        source_events = [{
            "op": "point",
            "zone": "player_holding",
            "order": 0,
            "anchor": "hold.marker",
            "offset": 1.5,
        }]
        pointer_resolution = [{
            "event_index": 0,
            "op": "point",
            "zone": "player_holding",
            "order": 0,
            "matched_count": 0,
            "item_ids": [],
        }]
        track_doc, compiled_doc = make_docs(
            source_events=source_events,
            clips=[],
            pointer_resolution=pointer_resolution,
        )

        result = audit.audit_documents(track_doc, compiled_doc)

        pointer_errors = [item for item in result["errors"] if item["check"] == "pointer"]
        self.assertEqual(1, len(pointer_errors), result)
        self.assertIn("event_index=0", pointer_errors[0]["message"])
        self.assertIn("op=point", pointer_errors[0]["message"])
        self.assertIn("zone=player_holding", pointer_errors[0]["message"])
        self.assertIn("anchor='hold.marker'", pointer_errors[0]["message"])
        self.assertIn("offset=1.5", pointer_errors[0]["message"])
        self.assertIn("item_ids 为空", pointer_errors[0]["message"])
        self.assertEqual(1, result["stats"]["pointer_unresolved_events"])
        self.assertEqual(1, result["stats"]["pointer_unresolved_cues"])

    def test_highlight_multiple_clips_do_not_mask_empty_pointer(self):
        source_events = [
            {"op": "highlight", "zone": "player_holding", "order": 0},
            {"op": "highlight", "zone": "player_development", "order": 0},
        ]
        clips = [
            {"kind": "highlight", "item_id": "a|card#1"},
            {"kind": "highlight", "item_id": "b|card#1"},
            {"kind": "highlight", "item_id": "c|card#1"},
        ]
        pointer_resolution = [
            {
                "event_index": 0,
                "op": "highlight",
                "zone": "player_holding",
                "order": 0,
                "matched_count": 3,
                "item_ids": ["a|card#1", "b|card#1", "c|card#1"],
            },
            {
                "event_index": 1,
                "op": "highlight",
                "zone": "player_development",
                "order": 0,
                "matched_count": 0,
                "item_ids": [],
            },
        ]
        track_doc, compiled_doc = make_docs(
            source_events=source_events,
            clips=clips,
            pointer_resolution=pointer_resolution,
        )

        result = audit.audit_documents(track_doc, compiled_doc)

        pointer_errors = [item for item in result["errors"] if item["check"] == "pointer"]
        self.assertEqual(1, len(pointer_errors), result)
        message = pointer_errors[0]["message"]
        self.assertIn("event_index=1", message)
        self.assertIn("op=highlight", message)
        self.assertIn("zone=player_development", message)
        self.assertNotIn("event_index=0", message)
        self.assertEqual([], result["warnings"], result["warnings"])

    def test_missing_resolution_record_reports_error(self):
        source_events = [{"op": "point", "zone": "player_holding", "order": 0}]
        clips = [{"kind": "point", "item_id": "starting_marker|marker#1"}]
        track_doc, compiled_doc = make_docs(source_events=source_events, clips=clips)

        result = audit.audit_documents(track_doc, compiled_doc)

        pointer_errors = [item for item in result["errors"] if item["check"] == "pointer"]
        self.assertEqual(1, len(pointer_errors), result)
        self.assertIn("event_index=0", pointer_errors[0]["message"])
        self.assertIn("缺少 resolution record", pointer_errors[0]["message"])

    def test_json_output_contains_errors_warnings_and_stats(self):
        track_doc, compiled_doc = make_docs()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            base = root / "content" / "games" / "splendor" / "tutorial" / "anim" / "v2"
            base.mkdir(parents=True)
            (base / "full.anim.json").write_text(
                json.dumps(track_doc, ensure_ascii=False), encoding="utf-8"
            )
            (base / "full.compiled.json").write_text(
                json.dumps(compiled_doc, ensure_ascii=False), encoding="utf-8"
            )

            stdout = io.StringIO()
            stderr = io.StringIO()
            with mock.patch.object(audit, "ROOT", root):
                with redirect_stdout(stdout), redirect_stderr(stderr):
                    exit_code = audit.main(["--game", "splendor", "--track", "full", "--json"])

        self.assertEqual(0, exit_code, stderr.getvalue())
        payload = json.loads(stdout.getvalue())
        self.assertIn("errors", payload)
        self.assertIn("warnings", payload)
        self.assertIn("stats", payload)
        self.assertEqual([], payload["errors"])


if __name__ == "__main__":
    unittest.main()
