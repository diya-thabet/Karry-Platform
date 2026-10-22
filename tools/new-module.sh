#!/usr/bin/env bash
# Create a bounded-context module from the karry-module template and add its projects to Karry.slnx.
# Usage: tools/new-module.sh <PascalCaseName>      e.g. tools/new-module.sh Maintenance
set -euo pipefail

name="${1:?usage: $0 <PascalCaseName>}"
if [[ ! "$name" =~ ^[A-Z][A-Za-z0-9]*$ ]]; then
  echo "Module name must be PascalCase letters/digits (e.g. Maintenance), got '$name'" >&2
  exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="src/backend/Modules/$name"
cd "$root"

if [[ -e "$target" ]]; then
  echo "$target already exists" >&2
  exit 1
fi

dotnet new install "$root/tools/templates/karry-module" --force >/dev/null
dotnet new karry-module -n "$name" -o "$target" >/dev/null

for project in "$target"/*/*.csproj; do
  case "$project" in
    *.Tests.csproj) folder="/tests/" ;;
    *) folder="/src/" ;;
  esac
  dotnet sln Karry.slnx add --solution-folder "$folder" "$project" >/dev/null
done

echo "Created module $name in $target:"
find "$target" -name '*.csproj' -printf '  %P\n' | sort
cat <<NEXT

Next steps:
  1. dotnet add src/backend/Karry.Host reference $target/Karry.Modules.$name.Api/Karry.Modules.$name.Api.csproj
  2. Register 'new ${name}Module()' in src/backend/Karry.Host/ModuleCatalog.cs
  3. dotnet restore Karry.slnx && dotnet build Karry.slnx && dotnet test --solution Karry.slnx
  4. Commit the generated packages.lock.json files.
NEXT
