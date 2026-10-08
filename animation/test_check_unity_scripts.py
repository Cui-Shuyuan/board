#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Pure-function tests for tools/ops/check_unity_scripts.py.

These tests never invoke dotnet; they pin the WSL/Windows path-selection and
diagnostic-formatting behavior that regressed under WSL.
"""
from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools" / "ops"))

import check_unity_scripts as cus  # noqa: E402


class WslDetectionTests(unittest.TestCase):
    def test_wsl_distro_env_marks_wsl(self):
        with mock.patch.dict(os.environ, {"WSL_DISTRO_NAME": "Ubuntu-24.04"}, clear=True):
            self.assertTrue(cus.running_in_wsl())

    def test_wsl_interop_env_marks_wsl(self):
        with mock.patch.dict(os.environ, {"WSL_INTEROP": "/run/WSL/1_interop"}, clear=True):
            self.assertTrue(cus.running_in_wsl())

    def test_non_wsl_kernel_is_not_wsl(self):
        with mock.patch.dict(os.environ, {}, clear=True):
            with mock.patch.object(Path, "read_text", return_value="Linux version 6.8.0 generic"):
                self.assertFalse(cus.running_in_wsl())

    def test_missing_proc_version_is_not_wsl(self):
        with mock.patch.dict(os.environ, {}, clear=True):
            with mock.patch.object(Path, "read_text", side_effect=OSError):
                self.assertFalse(cus.running_in_wsl())


class PathSelectionTests(unittest.TestCase):
    def test_windows_dotnet_under_wsl_uses_repo_scratch_dir(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            parent = cus.choose_project_parent(root, Path("/home/cui/.dotnet/dotnet.exe"), wsl=True)
            self.assertEqual(parent, root / ".tmp" / "check_unity_scripts")
            self.assertTrue(parent.is_dir())

    def test_native_dotnet_under_wsl_uses_platform_temp(self):
        with tempfile.TemporaryDirectory() as tmp:
            parent = cus.choose_project_parent(Path(tmp), Path("/usr/bin/dotnet"), wsl=True)
        self.assertEqual(parent, Path(tempfile.gettempdir()))

    def test_windows_dotnet_outside_wsl_uses_platform_temp(self):
        with tempfile.TemporaryDirectory() as tmp:
            parent = cus.choose_project_parent(Path(tmp), Path("C:/dotnet/dotnet.exe"), wsl=False)
        self.assertEqual(parent, Path(tempfile.gettempdir()))

    def test_copy_sources_only_for_windows_dotnet_under_wsl(self):
        self.assertTrue(cus.should_copy_sources(Path("/mnt/d/dotnet/dotnet.exe"), wsl=True))
        self.assertFalse(cus.should_copy_sources(Path("/usr/bin/dotnet"), wsl=True))
        self.assertFalse(cus.should_copy_sources(Path("C:/dotnet/dotnet.exe"), wsl=False))

    def test_is_windows_executable_handles_symlink_target_name(self):
        with tempfile.TemporaryDirectory() as tmp:
            link = Path(tmp) / "dotnet"
            link.write_text("", encoding="utf-8")
            self.assertFalse(cus.is_windows_executable(link))
            exe = Path(tmp) / "dotnet.exe"
            exe.write_text("", encoding="utf-8")
            self.assertTrue(cus.is_windows_executable(exe))


class DiagnosticFormattingTests(unittest.TestCase):
    def test_format_error_file_maps_windows_and_posix_copied_paths(self):
        source_map = {"0001_Broken.cs": "/tmp/input/Broken.cs"}
        self.assertEqual(
            cus.format_error_file(r"D:\scratch\src\0001_Broken.cs", source_map),
            "/tmp/input/Broken.cs",
        )
        self.assertEqual(
            cus.format_error_file("/mnt/d/scratch/src/0001_Broken.cs", source_map),
            "/tmp/input/Broken.cs",
        )

    def test_parse_errors_maps_copied_file_and_extracts_line_column(self):
        output = (
            "D:\\scratch\\src\\0001_Broken.cs(9,21): error CS1525: "
            "Invalid expression term ';'"
        )
        errors = cus.parse_errors(output, source_map={"0001_Broken.cs": "/tmp/Broken.cs"})
        self.assertEqual(errors, [{
            "file": "/tmp/Broken.cs",
            "line": 9,
            "column": 21,
            "code": "CS1525",
            "message": "Invalid expression term ';'",
        }])

    def test_parse_errors_keeps_unlocated_cs_errors(self):
        output = "CSC : error CS1504: cannot open source file 'x.cs'"
        errors = cus.parse_errors(output)
        self.assertEqual(len(errors), 1)
        self.assertEqual(errors[0]["code"], "CS1504")
        self.assertEqual(errors[0]["line"], 0)
        self.assertIn("cannot open source file", errors[0]["message"])

    def test_parse_errors_deduplicates_repeated_diagnostics(self):
        output = (
            "src/0001_Broken.cs(1,2): error CS1002: ; expected\n"
            "src/0001_Broken.cs(1,2): error CS1002: ; expected\n"
        )
        self.assertEqual(len(cus.parse_errors(output)), 1)


class ProjectLayoutTests(unittest.TestCase):
    def test_build_project_copies_sources_and_returns_original_map(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = root / "Broken.cs"
            source.write_text("class Broken {}", encoding="utf-8")
            project = root / "project"
            project.mkdir()
            mapping = cus.build_project(project, [source], copy_sources=True)
            self.assertEqual(mapping, {"0001_Broken.cs": str(source)})
            csproj = (project / "Check.csproj").read_text(encoding="utf-8")
            self.assertIn('Compile Include="src/0001_Broken.cs"', csproj)
            self.assertTrue((project / "src" / "0001_Broken.cs").exists())

    def test_build_project_uses_relative_original_paths_without_copying(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = root / "Sample.cs"
            source.write_text("class Sample {}", encoding="utf-8")
            project = root / "project"
            project.mkdir()
            mapping = cus.build_project(project, [source], copy_sources=False)
            self.assertEqual(mapping, {})
            csproj = (project / "Check.csproj").read_text(encoding="utf-8")
            self.assertIn('Compile Include="../Sample.cs"', csproj.replace("\\", "/"))


if __name__ == "__main__":
    unittest.main()
