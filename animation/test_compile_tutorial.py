#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for compile_tutorial path handling and game/track parameters."""
from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import compile_tutorial as ct  # noqa: E402


class CompileTutorialPathTests(unittest.TestCase):
    def test_resolve_game_uses_explicit_game(self):
        with mock.patch.object(ct, "ROOT", Path("/nonexistent")):
            self.assertEqual("chosen", ct.resolve_game("chosen", "full"))

    def test_resolve_game_discovers_unique_track_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            game = root / "content" / "games" / "fake-game"
            (game / "tutorial" / "anim" / "v2").mkdir(parents=True)
            (game / "media" / "tts" / "full").mkdir(parents=True)
            (game / "tutorial" / "script.full.json").write_text("{}", encoding="utf-8")
            (game / "tutorial" / "anim" / "v2" / "full.anim.json").write_text("{}", encoding="utf-8")
            (game / "media" / "tts" / "full" / "tts_manifest.json").write_text("{}", encoding="utf-8")
            with mock.patch.object(ct, "ROOT", root):
                self.assertEqual("fake-game", ct.resolve_game(None, "full"))

    def test_normalize_manifest_paths_converts_windows_separators(self):
        manifest = {
            "cues": [
                {
                    "id": "cue.1",
                    "file": "content\\games\\fake\\media\\tts\\full\\cue.1.mp3",
                    "subtitle_file": "content\\games\\fake\\media\\tts\\full\\cue.1.subtitle.json",
                }
            ]
        }

        ct.normalize_manifest_paths(manifest)

        cue = manifest["cues"][0]
        self.assertEqual("content/games/fake/media/tts/full/cue.1.mp3", cue["file"])
        self.assertEqual(
            "content/games/fake/media/tts/full/cue.1.subtitle.json",
            cue["subtitle_file"],
        )

    def test_windows_manifest_path_is_not_treated_as_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            audio = root / "content/games/fake/media/tts/full/cue.1.mp3"
            audio.parent.mkdir(parents=True)
            audio.write_bytes(b"")
            cue = {
                "id": "cue.1",
                "file": "content\\games\\fake\\media\\tts\\full\\cue.1.mp3",
            }

            self.assertTrue(ct.manifest_audio_ok(cue, root=root))
            self.assertFalse(ct.manifest_audio_ok({"file": "content\\missing.mp3"}, root=root))
            self.assertFalse(ct.manifest_audio_ok({"file": ""}, root=root))

    def test_delta_lrc_uses_explicit_game_and_track(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "delta.lrc"
            cues = [{"id": "cue.1", "beats": [{"text": "hello"}]}]

            ct.write_delta_lrc(out, cues, {}, game="fake-game", track="quick")

            text = out.read_text(encoding="utf-8")
        self.assertIn("[game:fake-game]", text)
        self.assertIn("[track:quick]", text)
        self.assertNotIn("splendor", text)

    def test_tts_asset_path_uses_game_and_track(self):
        rel = ct.tts_asset_rel("fake-game", "quick", "cue.1", ".mp3")
        self.assertEqual("content/games/fake-game/media/tts/quick/cue.1.mp3", rel)

    def test_qa_questions_path_uses_game(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with mock.patch.object(ct, "ROOT", root):
                path = ct.qa_questions_path("fake-game")
        self.assertEqual(
            "content/games/fake-game/tutorial/anim/_qa/questions.json",
            path.as_posix()[len(str(root)) + 1:],
        )


class CompileTutorialDryRunTests(unittest.TestCase):
    def _write_fixture(self, root: Path, *, script_text="hello", manifest_extra=None):
        game_dir = root / "content" / "games" / "fake"
        tutorial = game_dir / "tutorial"
        anim_v2 = tutorial / "anim" / "v2"
        manifest_dir = game_dir / "media" / "tts" / "full"
        anim_v2.mkdir(parents=True)
        manifest_dir.mkdir(parents=True)
        script = {
            "cues": [{
                "id": "cue.1",
                "beats": [{"text": script_text}],
                "refs": [],
            }]
        }
        manifest = {"cues": [{
            "id": "cue.1",
            "file": "content/games/fake/media/tts/full/cue.1.mp3",
            "duration": 1.0,
            "refs": [],
        }]}
        if manifest_extra:
            manifest["cues"].append(manifest_extra)
        (tutorial / "script.full.json").write_text(
            json.dumps(script, ensure_ascii=False), encoding="utf-8")
        (anim_v2 / "full.anim.json").write_text("{}", encoding="utf-8")
        (manifest_dir / "tts_manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False), encoding="utf-8")
        return game_dir, script, manifest

    def test_main_dry_run_with_no_changes_reports_zero_and_does_not_write(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            game_dir, _script, manifest = self._write_fixture(root)
            manifest_path = game_dir / "media" / "tts" / "full" / "tts_manifest.json"
            before = manifest_path.read_text(encoding="utf-8")
            out = io.StringIO()
            with mock.patch.object(ct, "ROOT", root),                  mock.patch.object(ct, "read_current_tts_text", return_value={"cue.1": "hello"}),                  mock.patch.object(ct, "manifest_audio_ok", return_value=True),                  mock.patch.object(sys, "argv", [
                     "compile_tutorial.py", "--game", "fake", "--track", "full", "--dry-run"
                 ]),                  contextlib.redirect_stdout(out):
                rc = ct.main()
            self.assertEqual(rc, 0)
            self.assertIn("changed text cues: 0", out.getvalue())
            self.assertEqual(manifest_path.read_text(encoding="utf-8"), before)

    def test_main_dry_run_with_text_change_calls_tts_in_dry_run_mode(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._write_fixture(root, script_text="new text")
            out = io.StringIO()
            with mock.patch.object(ct, "ROOT", root),                  mock.patch.object(ct, "read_current_tts_text", return_value={"cue.1": "old text"}),                  mock.patch.object(ct, "manifest_audio_ok", return_value=True),                  mock.patch.object(ct, "run_tts_delta") as run_tts,                  mock.patch.object(sys, "argv", [
                     "compile_tutorial.py", "--game", "fake", "--track", "full", "--dry-run"
                 ]),                  contextlib.redirect_stdout(out):
                run_tts.side_effect = lambda game_dir, game, track, script, manifest, changed, tts_python, dry_run: manifest
                rc = ct.main()
            self.assertEqual(rc, 0)
            self.assertIn("changed text cues: 1", out.getvalue())
            run_tts.assert_called_once()
            self.assertTrue(run_tts.call_args.args[-1])

    def test_main_missing_sources_returns_two(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            err = io.StringIO()
            with mock.patch.object(ct, "ROOT", root),                  mock.patch.object(sys, "argv", [
                     "compile_tutorial.py", "--game", "fake", "--track", "full", "--dry-run"
                 ]),                  contextlib.redirect_stderr(err):
                rc = ct.main()
            self.assertEqual(rc, 2)
            self.assertIn("missing source", err.getvalue())

    def test_prune_removed_dry_run_keeps_files_and_manifest(self):
        with tempfile.TemporaryDirectory() as tmp:
            game_dir = Path(tmp)
            media = game_dir / "media" / "tts" / "full"
            media.mkdir(parents=True)
            (media / "old.mp3").write_bytes(b"")
            (media / "old.subtitle.json").write_text("{}", encoding="utf-8")
            manifest = {"cues": [{"id": "keep"}, {"id": "old"}]}
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                new_manifest, removed = ct.prune_removed(
                    game_dir, "full", {"cues": [{"id": "keep"}]}, manifest, True)
            self.assertEqual(removed, ["old"])
            self.assertEqual(new_manifest, manifest)
            self.assertTrue((media / "old.mp3").exists())
            self.assertIn("[dry-run] TTS would prune: old", out.getvalue())

    def test_prune_removed_actual_deletes_media_and_filters_manifest(self):
        with tempfile.TemporaryDirectory() as tmp:
            game_dir = Path(tmp)
            media = game_dir / "media" / "tts" / "full"
            media.mkdir(parents=True)
            (media / "old.mp3").write_bytes(b"")
            (media / "old.subtitle.json").write_text("{}", encoding="utf-8")
            manifest = {"cues": [{"id": "keep"}, {"id": "old"}]}
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                new_manifest, removed = ct.prune_removed(
                    game_dir, "full", {"cues": [{"id": "keep"}]}, manifest, False)
            self.assertEqual(removed, ["old"])
            self.assertEqual(new_manifest["cues"], [{"id": "keep"}])
            self.assertFalse((media / "old.mp3").exists())
            self.assertFalse((media / "old.subtitle.json").exists())
            self.assertIn("pruned 1 cue", out.getvalue())

    def test_normalize_manifest_path_is_separator_agnostic(self):
        self.assertEqual(
            ct.normalize_manifest_path(r"content\games\fake\a.mp3"),
            "content/games/fake/a.mp3",
        )
        self.assertEqual(ct.normalize_manifest_path(""), "")


if __name__ == "__main__":
    unittest.main()
