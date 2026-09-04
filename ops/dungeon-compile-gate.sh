#!/usr/bin/env bash
# Compile-check the Arena runtime + editor + EditMode test assemblies WITHOUT Unity.
#
# This script prepares temporary copies of all three Unity-generated csproj
# files, redirects their intermediate/output paths out of Temp/ so the running
# editor is never disturbed, builds them, and cleans up on exit.
#
# Assembly-CSharp (the runtime assembly) is patched too, and the editor/test
# projects are repointed at the patched copy: it is referenced by both, and a
# stale runtime csproj made new runtime or generated-binding files invisible —
# which surfaced as "type not found" errors inside SpacetimeDBClient.g.cs.
#
# Usage:  ops/dungeon-compile-gate.sh
# Exit:   0 = every assembly compiles, non-zero = errors printed above.

set -uo pipefail

fail() {
  echo "COMPILE GATE: FAIL — $*" >&2
  exit 1
}

cd "$(dirname "$0")/.." || fail "Cannot enter the repository directory."
ROOT="$(pwd)"
OUT="${TMPDIR:-/tmp}/arena-compile-gate"
projects=(Assembly-CSharp Assembly-CSharp-Editor Arena.EditModeTests)

cleanup() {
  local proj
  for proj in "${projects[@]}"; do
    rm -f "_compilegate_$proj.csproj"
  done
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p "$OUT" || fail "Cannot create output directory: $OUT"

# Check the complete input set before preparing or compiling any project.
for proj in "${projects[@]}"; do
  if [ ! -f "$proj.csproj" ]; then
    fail "Missing $proj.csproj; open this project in Unity normally to generate project files."
  fi
done

patch_project() {   # $1 = project name
  python3 - "$1" "_compilegate_$1.csproj" "$OUT" <<'PY'
import os, re, sys, glob
proj, gate, out = sys.argv[1], sys.argv[2], sys.argv[3]
src = open(f'{proj}.csproj', encoding='utf-8-sig').read()
src = re.sub(r'<BaseIntermediateOutputPath>[^<]*</BaseIntermediateOutputPath>',
             f'<BaseIntermediateOutputPath>{out}/obj/{proj}/</BaseIntermediateOutputPath>', src)
src = re.sub(r'<OutputPath>[^<]*</OutputPath>', f'<OutputPath>{out}/bin/{proj}/</OutputPath>', src)
# Every project that references the runtime assembly must reference the PATCHED
# runtime assembly, or the stale file list wins.
src = src.replace('<ProjectReference Include="Assembly-CSharp.csproj" />',
                  '<ProjectReference Include="_compilegate_Assembly-CSharp.csproj" />')

# Drop Compile items for files that no longer exist, then add any new ones on
# disk. Unity only regenerates csproj files when it has focus, so after adding
# or deleting a script the checked-in csproj is stale.
src = re.sub(r'\s*<Compile Include="([^"]+)" />',
             lambda m: m.group(0) if os.path.exists(m.group(1).replace('\\', '/')) else '', src)
listed = {m.replace('\\', '/') for m in re.findall(r'<Compile Include="([^"]+)" />', src)}
roots = {
    'Assembly-CSharp': {'Assets/Arena/Runtime'},
    'Assembly-CSharp-Editor': {'Assets/Arena/Editor', 'Assets/Arena/Tests/Editor'},
    'Arena.EditModeTests': {'Assets/Arena/Tests/Editor'},
}[proj]
found = set()
for r in roots:
    found |= {p for p in glob.glob(f'{r}/**/*.cs', recursive=True)}
missing = sorted(f for f in found - listed if 'Tests/Editor' not in f or proj != 'Assembly-CSharp-Editor')
if missing:
    src = src.replace('</ItemGroup>',
                      ''.join(f'\n    <Compile Include="{f}" />' for f in missing) + '\n  </ItemGroup>', 1)
open(gate, 'w', encoding='utf-8').write(src)
PY
}

for proj in "${projects[@]}"; do
  if ! patch_project "$proj"; then
    fail "Could not prepare $proj.csproj."
  fi
done

status=0
for proj in "${projects[@]}"; do
  # Build ONCE and analyse the captured log. Building twice raced the first
  # build's file locks and produced spurious failures.
  echo "=== $proj ==="
  log="$OUT/$proj.build.log"
  dotnet build "_compilegate_$proj.csproj" -t:Rebuild -v:q -nologo >"$log" 2>&1
  rc=$?
  grep -E "error [A-Z]+[0-9]+" "$log" | sed "s|$ROOT/||" | sort -u | head -40
  if [ "$rc" -ne 0 ] || ! grep -q "0 Error(s)" "$log"; then
    status=1
    echo "  build failed (rc=$rc) — full log: $log"
  else
    echo "  0 errors"
  fi
done

if [ "$status" -eq 0 ]; then
  echo "COMPILE GATE: PASS"
else
  echo "COMPILE GATE: FAIL"
fi
exit "$status"
