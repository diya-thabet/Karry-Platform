#!/usr/bin/env bash
# Install a pinned CI tool into ~/.local/bin after verifying its SHA-256 (supply chain: never "curl | sh").
# Usage: tools/ci/install-tool.sh <gitleaks|trivy|actionlint|oasdiff|k6>
# Versions are at least one week old when pinned; Renovate proposes updates.
set -euo pipefail

tool="${1:?usage: $0 <gitleaks|trivy|actionlint|oasdiff|k6>}"
case "$tool" in
  gitleaks)
    version=8.30.1
    url="https://github.com/gitleaks/gitleaks/releases/download/v${version}/gitleaks_${version}_linux_x64.tar.gz"
    sha256=551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb ;;
  trivy)
    version=0.75.0
    url="https://github.com/aquasecurity/trivy/releases/download/v${version}/trivy_${version}_Linux-64bit.tar.gz"
    sha256=c6e65abddb348e25f10549df887045629cf28cc72453cd1c63acb717316b3f3f ;;
  actionlint)
    version=1.7.12
    url="https://github.com/rhysd/actionlint/releases/download/v${version}/actionlint_${version}_linux_amd64.tar.gz"
    sha256=8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8 ;;
  oasdiff)
    # 1.33.0: 1.34.x was younger than the 7-day release-age rule on 2026-10-23.
    version=1.33.0
    url="https://github.com/oasdiff/oasdiff/releases/download/v${version}/oasdiff_${version}_linux_amd64.tar.gz"
    sha256=43a4e328e2d13ba1552d760aa68d2485c75c5621f309f6ff64ae895188345247 ;;
  k6)
    # Load tests (P1.18a, tests/load). The binary sits in a folder inside the archive.
    version=2.3.0
    url="https://github.com/grafana/k6/releases/download/v${version}/k6-v${version}-linux-amd64.tar.gz"
    sha256=39c3117b6af817592dcd0ce4242105c0a7af10948c2a425306f0be8f7a8a8ab1
    member="k6-v${version}-linux-amd64/k6" ;;
  *) echo "unknown tool: $tool" >&2; exit 2 ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
curl -fsSL --retry 3 -o "$work/archive.tar.gz" "$url"
echo "${sha256}  $work/archive.tar.gz" | sha256sum -c --quiet -
tar -xzf "$work/archive.tar.gz" -C "$work" "${member:-$tool}"
mkdir -p "$HOME/.local/bin"
install -m 0755 "$work/${member:-$tool}" "$HOME/.local/bin/$tool"
echo "$tool $version installed to $HOME/.local/bin/$tool"
