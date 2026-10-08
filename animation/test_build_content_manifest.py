# -*- coding: utf-8 -*-
"""Regression tests for the runtime-only immutable content publisher."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO_ROOT / "tools" / "content"))

import build_content_manifest as bcm  # noqa: E402


class BuildContentManifestTest(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.game = "testgame"
        self.game_dir = self.root / "content" / "games" / self.game
        self.write_runtime_tree()

    def write_runtime_tree(self) -> None:
        tutorial = self.game_dir / "tutorial"
        (tutorial / "anim" / "v2").mkdir(parents=True, exist_ok=True)
        (self.game_dir / "media" / "tts" / "full").mkdir(parents=True, exist_ok=True)
        (self.game_dir / "media" / "card").mkdir(parents=True, exist_ok=True)

        (self.game_dir / "media" / "tts" / "full" / "one.mp3").write_bytes(b"mp3-v1")
        (self.game_dir / "media" / "card" / "card_cutout.png").write_bytes(b"png-v1")

        runtime = {
            "schema_version": 1,
            "game_id": self.game,
            "track": "full",
            "cues": [
                {
                    "id": "cue.001",
                    "audio": "media/tts/full/one.mp3",
                    "text": "hello",
                }
            ],
        }
        compiled = {
            "schema": "board-content/v1",
            "game": self.game,
            "track": "full",
            "stages": [
                {
                    "templates": [
                        {
                            "id": "card",
                            "face_image": "media/card/card_cutout.png",
                            "back_image": "",
                        }
                    ],
                    "background": "#000000",
                }
            ],
            "cues": [
                {
                    "id": "cue.001",
                    "clips": [{"kind": "show", "face_image": "media/card/card_cutout.png"}],
                }
            ],
        }

        self.runtime_path = tutorial / "full.runtime.json"
        self.compiled_path = tutorial / "anim" / "v2" / "full.compiled.json"
        self.runtime_path.write_text(json.dumps(runtime, ensure_ascii=False), encoding="utf-8")
        self.compiled_path.write_text(json.dumps(compiled, ensure_ascii=False), encoding="utf-8")

    def manifest(self) -> dict:
        path = self.root / "content" / "manifests" / f"{self.game}.json"
        return json.loads(path.read_text(encoding="utf-8"))

    def publish(self) -> dict:
        return bcm.publish(self.game, self.root)

    def release_dir(self, version: str) -> Path:
        return self.root / "content" / "releases" / self.game / version

    def test_package_contains_only_explicit_runtime_dependencies(self) -> None:
        first = self.publish()
        manifest = self.manifest()
        paths = sorted(entry["path"] for entry in manifest["files"])

        self.assertEqual(
            paths,
            [
                "media/card/card_cutout.png",
                "media/tts/full/one.mp3",
                "tutorial/anim/v2/full.compiled.json",
                "tutorial/full.runtime.json",
            ],
        )
        self.assertEqual(first["version"], manifest["version"])
        self.assertEqual(len(manifest["files"]), 4)
        self.assertTrue((self.release_dir(manifest["version"]) / "tutorial/full.runtime.json").is_file())
        self.assertFalse(any(bcm.is_excluded_path(path) for path in paths))

    def test_qa_logs_pyc_and_documentation_do_not_change_version(self) -> None:
        self.publish()
        version_before = self.manifest()["version"]
        files_before = self.manifest()["files"]

        (self.game_dir / "tutorial" / "anim" / "_qa").mkdir(parents=True, exist_ok=True)
        (self.game_dir / "tutorial" / "anim" / "_qa" / "ask_log.jsonl").write_text(
            '{"qa":"log"}\n', encoding="utf-8"
        )
        (self.game_dir / "tutorial" / "checks" / "__pycache__").mkdir(
            parents=True, exist_ok=True
        )
        (self.game_dir / "tutorial" / "checks" / "__pycache__" / "ledger.pyc").write_bytes(
            b"pyc"
        )
        (self.game_dir / "tutorial" / "checks" / "ledger.py").write_text(
            "print('tool')\n", encoding="utf-8"
        )
        (self.game_dir / "tutorial" / "anim" / "v2" / "README.md").write_text(
            "# not runtime\n", encoding="utf-8"
        )
        (self.game_dir / "tutorial" / "anim" / "v2" / "full.anim.json").write_text(
            '{"source": true}\n', encoding="utf-8"
        )
        (self.game_dir / "tutorial" / "animation").mkdir(parents=True, exist_ok=True)
        (self.game_dir / "tutorial" / "animation" / "audit.json").write_text(
            '{"audit": true}\n', encoding="utf-8"
        )
        (self.game_dir / "tutorial" / "full.lrc").write_text("[00:00]hi\n", encoding="utf-8")
        (self.game_dir / "media" / "card" / "unused.png").write_bytes(b"unused")

        second = self.publish()

        self.assertEqual(version_before, second["version"])
        self.assertEqual(files_before, self.manifest()["files"])

    def test_editing_runtime_bytes_changes_version(self) -> None:
        first = self.publish()
        (self.game_dir / "media" / "tts" / "full" / "one.mp3").write_bytes(b"mp3-v2")
        second = self.publish()

        self.assertNotEqual(first["version"], second["version"])

    def test_editing_runtime_json_changes_version(self) -> None:
        first = self.publish()
        runtime = json.loads(self.runtime_path.read_text(encoding="utf-8"))
        runtime["cues"][0]["text"] = "changed"
        self.runtime_path.write_text(json.dumps(runtime), encoding="utf-8")
        second = self.publish()

        self.assertNotEqual(first["version"], second["version"])

    def test_failed_publish_preserves_old_manifest_and_release(self) -> None:
        first = self.publish()
        version = first["version"]
        manifest_path = self.root / "content" / "manifests" / f"{self.game}.json"
        manifest_before = manifest_path.read_bytes()
        release_file = self.release_dir(version) / "media" / "tts" / "full" / "one.mp3"
        release_before = release_file.read_bytes()

        # New source bytes => new version; fail while staging the new release.
        (self.game_dir / "media" / "tts" / "full" / "one.mp3").write_bytes(b"mp3-v2")
        with mock.patch.object(bcm.shutil, "copy2", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                self.publish()

        self.assertEqual(manifest_before, manifest_path.read_bytes())
        self.assertEqual(release_before, release_file.read_bytes())
        self.assertFalse((self.root / "content" / "manifests" / f"{self.game}.json.tmp").exists())

    def test_republish_with_mismatched_existing_release_fails(self) -> None:
        first = self.publish()
        version = first["version"]
        manifest_path = self.root / "content" / "manifests" / f"{self.game}.json"
        manifest_before = manifest_path.read_bytes()

        tampered = self.release_dir(version) / "media" / "tts" / "full" / "one.mp3"
        tampered.write_bytes(b"tampered")

        with self.assertRaises(ValueError):
            self.publish()

        self.assertEqual(manifest_before, manifest_path.read_bytes())

    def test_missing_referenced_media_fails_loudly(self) -> None:
        (self.game_dir / "media" / "tts" / "full" / "one.mp3").unlink()

        with self.assertRaises(FileNotFoundError):
            bcm.build_manifest(self.game, self.root)


if __name__ == "__main__":
    unittest.main()
