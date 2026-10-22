#!/usr/bin/env bash
# TC-P1-PLT-003: the karry-module template generates a module that builds, passes its own tests
# and satisfies the architecture rules. Leaves the repository exactly as it found it.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

name="TemplateCheck"
target="src/backend/Modules/$name"
backup="$(mktemp)"
cp Karry.slnx "$backup"

cleanup() {
  rm -rf "$target"
  cp "$backup" Karry.slnx
  rm -f "$backup"
  rmdir src/backend/Modules 2>/dev/null || true
  dotnet new uninstall "$root/tools/templates/karry-module" >/dev/null 2>&1 || true
}
trap cleanup EXIT

tools/new-module.sh "$name" >/dev/null

expected=(Contracts Domain Application Infrastructure Api Tests)
for layer in "${expected[@]}"; do
  project="$target/Karry.Modules.$name.$layer/Karry.Modules.$name.$layer.csproj"
  [[ -f "$project" ]] || { echo "FAIL: missing $project" >&2; exit 1; }
  grep -q "Karry.Modules.$name.$layer.csproj" Karry.slnx || { echo "FAIL: $project not added to Karry.slnx" >&2; exit 1; }
done
count="$(find "$target" -name '*.csproj' | wc -l)"
[[ "$count" -eq ${#expected[@]} ]] || { echo "FAIL: expected ${#expected[@]} projects, found $count" >&2; exit 1; }
grep -q '"templatecheck"' "$target/Karry.Modules.$name.Api/${name}Module.cs" || { echo "FAIL: route name not lower-cased" >&2; exit 1; }

dotnet restore Karry.slnx >/dev/null
dotnet build Karry.slnx -c Release --no-restore
dotnet test --project "$target/Karry.Modules.$name.Tests" -c Release --no-build
dotnet test --project tests/Karry.ArchitectureTests -c Release --no-build

echo "TC-P1-PLT-003 passed: template output builds, its tests pass, architecture rules hold."
