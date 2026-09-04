#!/usr/bin/env python3
"""Exercise compile-gate exit status and cleanup without launching Unity.

Fixtures contain only this shell script, minimal .NET project text, and command
stubs. No Unity project, assets, or generated project files are copied.
"""

import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest


GATE = Path(__file__).with_name("dungeon-compile-gate.sh")
PROJECTS = ("Assembly-CSharp", "Assembly-CSharp-Editor", "Arena.EditModeTests")
PROJECT_XML = """<Project>
  <PropertyGroup>
    <BaseIntermediateOutputPath>Temp/obj/</BaseIntermediateOutputPath>
    <OutputPath>Temp/bin/</OutputPath>
  </PropertyGroup>
  <ItemGroup></ItemGroup>
</Project>
"""


class CompileGateTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="arena-compile-gate-test-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / "ops").mkdir()
        shutil.copyfile(GATE, self.root / "ops" / GATE.name)
        for project in PROJECTS:
            (self.root / f"{project}.csproj").write_text(PROJECT_XML)

        self.commands = self.root / "commands"
        self.commands.mkdir()
        self.invocations = self.root / "build-invocations.txt"
        self.env = {
            **os.environ,
            "PATH": f"{self.commands}{os.pathsep}{os.environ['PATH']}",
            "TMPDIR": str(self.root / "output"),
            "ARENA_GATE_INVOCATIONS": str(self.invocations),
            "ARENA_GATE_FAIL_PREPARATION": "",
            "ARENA_GATE_FAIL_BUILD": "",
            "ARENA_GATE_BUILD_MODE": "success",
        }
        self.write_command("python3", f"""#!/bin/sh
if [ "$2" = "$ARENA_GATE_FAIL_PREPARATION" ]; then
    echo "injected project preparation failure" >&2
    exit 17
fi
exec {shlex.quote(sys.executable)} "$@"
""")
        self.write_command("dotnet", """#!/bin/sh
printf '%s\\n' "$2" >> "$ARENA_GATE_INVOCATIONS"
test -f "$2" || exit 2
if [ "$ARENA_GATE_BUILD_MODE" = "interrupt" ]; then
    kill -TERM "$PPID"
    exit 1
fi
if [ "$ARENA_GATE_BUILD_MODE" != "no-summary" ]; then
    printf '    0 Error(s)\\n'
fi
if [ "$2" = "_compilegate_$ARENA_GATE_FAIL_BUILD.csproj" ]; then
    exit 1
fi
""")

    def write_command(self, name, source):
        path = self.commands / name
        path.write_text(source)
        path.chmod(0o755)

    def run_gate(self):
        self.invocations.unlink(missing_ok=True)
        return subprocess.run(
            ["bash", str(self.root / "ops" / GATE.name)],
            env=self.env, capture_output=True, text=True, timeout=15,
        )

    def builds(self):
        return self.invocations.read_text().splitlines() if self.invocations.exists() else []

    def assert_failed(self, result):
        output = result.stdout + result.stderr
        self.assertNotEqual(result.returncode, 0, output)
        self.assertNotIn("COMPILE GATE: PASS", output)
        self.assertEqual(list(self.root.glob("_compilegate_*.csproj")), [], output)

    def test_all_projects_missing_cannot_pass(self):
        for project in PROJECTS:
            (self.root / f"{project}.csproj").unlink()
        result = self.run_gate()
        self.assert_failed(result)
        self.assertIn(f"{PROJECTS[0]}.csproj", result.stdout + result.stderr)
        self.assertEqual(self.builds(), [])

    def test_each_required_project_missing_prevents_compilation(self):
        for project in PROJECTS:
            with self.subTest(project=project):
                path = self.root / f"{project}.csproj"
                path.unlink()
                try:
                    result = self.run_gate()
                    self.assert_failed(result)
                    self.assertIn(project, result.stdout + result.stderr)
                    self.assertEqual(self.builds(), [])
                finally:
                    path.write_text(PROJECT_XML)

    def test_preparation_failure_cannot_build_a_stale_project(self):
        project = PROJECTS[1]
        (self.root / f"_compilegate_{project}.csproj").write_text(PROJECT_XML)
        self.env["ARENA_GATE_FAIL_PREPARATION"] = project
        result = self.run_gate()
        self.assert_failed(result)
        self.assertIn(project, result.stdout + result.stderr)
        self.assertEqual(self.builds(), [])

    def test_each_build_failure_overrides_a_successful_log_summary(self):
        for project in PROJECTS:
            with self.subTest(project=project):
                self.env["ARENA_GATE_FAIL_BUILD"] = project
                self.assert_failed(self.run_gate())

    def test_missing_compiler_summary_cannot_pass(self):
        self.env["ARENA_GATE_BUILD_MODE"] = "no-summary"
        self.assert_failed(self.run_gate())

    def test_interruption_removes_temporary_projects(self):
        self.env["ARENA_GATE_BUILD_MODE"] = "interrupt"
        self.assert_failed(self.run_gate())

    def test_success_requires_all_three_builds_and_cleans_up(self):
        result = self.run_gate()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("COMPILE GATE: PASS", result.stdout)
        self.assertCountEqual(self.builds(), [f"_compilegate_{p}.csproj" for p in PROJECTS])
        self.assertEqual(list(self.root.glob("_compilegate_*.csproj")), [])


if __name__ == "__main__":
    unittest.main()
