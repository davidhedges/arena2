#!/usr/bin/env bash
# Compile-check the Arena editor + EditMode test assemblies WITHOUT Unity.
#
# Unity holds Temp/UnityLockfile whenever the editor is open, which blocks
# batchmode. This script copies the Unity-generated csproj files, redirects
# their intermediate/output paths out of Temp/ so the running editor is never
# disturbed, builds them, and cleans up.
#
# Usage:  ops/dungeon-compile-gate.sh
# Exit:   0 = both assemblies compile, non-zero = errors printed above.

set -uo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"
OUT="${TMPDIR:-/tmp}/arena-compile-gate"
mkdir -p "$OUT"

status=0
for proj in Assembly-CSharp-Editor Arena.EditModeTests; do
  if [ ! -f "$proj.csproj" ]; then
    echo "SKIP $proj (no csproj — open Unity once to generate it)"
    continue
  fi

  gate="_compilegate_$proj.csproj"
  python3 - "$proj" "$gate" "$OUT" <<'PY'
import os, re, sys, glob
proj, gate, out = sys.argv[1], sys.argv[2], sys.argv[3]
src = open(f'{proj}.csproj', encoding='utf-8-sig').read()
src = re.sub(r'<BaseIntermediateOutputPath>[^<]*</BaseIntermediateOutputPath>',
             f'<BaseIntermediateOutputPath>{out}/obj/{proj}/</BaseIntermediateOutputPath>', src)
src = re.sub(r'<OutputPath>[^<]*</OutputPath>', f'<OutputPath>{out}/bin/{proj}/</OutputPath>', src)

# Drop Compile items for files that no longer exist, then add any new ones on
# disk. Unity only regenerates csproj files when it has focus, so after adding
# or deleting a script the checked-in csproj is stale.
src = re.sub(r'\s*<Compile Include="([^"]+)" />',
             lambda m: m.group(0) if os.path.exists(m.group(1).replace('\\', '/')) else '', src)
listed = {m.replace('\\', '/') for m in re.findall(r'<Compile Include="([^"]+)" />', src)}
roots = ({'Assets/Arena/Editor', 'Assets/Arena/Tests/Editor'}
         if proj == 'Assembly-CSharp-Editor' else {'Assets/Arena/Tests/Editor'})
found = set()
for r in roots:
    found |= {p for p in glob.glob(f'{r}/**/*.cs', recursive=True)}
missing = sorted(f for f in found - listed if 'Tests/Editor' not in f or proj != 'Assembly-CSharp-Editor')
if missing:
    src = src.replace('</ItemGroup>',
                      ''.join(f'\n    <Compile Include="{f}" />' for f in missing) + '\n  </ItemGroup>', 1)
open(gate, 'w', encoding='utf-8').write(src)
PY

  echo "=== $proj ==="
  if ! dotnet build "$gate" -t:Rebuild -v:q -nologo 2>&1 | grep -E "error|Error\(s\)"; then
    echo "  (no output)"
  fi
  dotnet build "$gate" -v:q -nologo 2>&1 | grep -q "0 Error(s)" || status=1
  rm -f "$gate"
done

if [ "$status" -eq 0 ]; then
  echo "COMPILE GATE: PASS"
else
  echo "COMPILE GATE: FAIL"
fi
exit "$status"
