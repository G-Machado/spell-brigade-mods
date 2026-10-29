#!/usr/bin/env bash
# Replay every scenario under the defaults and each candidates/*.json; one row per candidate x scenario:
# share of time per phase (Held = boss back-off) and director crabs spawned per rule.
# Usage: tools/probes/replay/compare.sh [candidate.json ...]   (default: candidates/*.json)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dotnet build "$here/Replay.csproj" -c Release -nologo -v q >/dev/null
dll="$here/bin/Release/net6.0/replay.dll"
[ $# -gt 0 ] || set -- "$here"/candidates/*.json
run() { # <label> [--rules file]
  local label="$1"; shift
  dotnet "$dll" all "$@" --every 1000 | grep '"ev":"summary"' | node -e '
    const label = process.argv[1];
    for (const l of require("fs").readFileSync(0, "utf8").trim().split("\n")) {
      const s = JSON.parse(l), d = s.duration, p = s.phaseSeconds;
      const pct = k => String(Math.round(100 * (p[k] || 0) / d)).padStart(3);
      const rules = Object.entries(s.spawnedByRule).map(([k, v]) => `${k}:${v}`).join(" ") || "-";
      console.log(`${label.padEnd(16)} ${s.scenario.padEnd(10)} build${pct("BuildUp")}% peak${pct("Peak")}% relax${pct("Relax")}% held${pct("Held")}%  maxI=${s.maxI.toFixed(2)} downs=${s.downs} inBoss=${s.spawnsDuringBoss}  ${rules}`);
    }' "$label"
}
run defaults
for c in "$@"; do run "$(basename "$c" .json)" --rules "$c"; done
