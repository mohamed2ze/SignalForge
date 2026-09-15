#!/usr/bin/env bash
#
# Coverage gate for CI: runs the integration suite with the XPlat coverage collector, then
# enforces a minimum combined line-coverage percentage. Aggregate line counts are summed from the
# Cobertura report's <line> elements (hits > 0 == covered), which stays correct regardless of how
# the XML emitter groups classes/packages.
#
# Usage: enforce-coverage.sh [threshold_percent] [results_dir]
#   threshold_percent  Minimum allowed line coverage as a percentage (default 80).

set -euo pipefail

threshold="${1:-80}"
results_dir="${2:-${RUNNER_TEMP:-/tmp}/coverage}"
config="${3:-Release}"

echo "Running integration tests with coverage collection (threshold: ${threshold}%)..."
dotnet test tests/SignalForge.IntegrationTests/SignalForge.IntegrationTests.csproj \
  -c "$config" --no-restore \
  --collect:"XPlat Code Coverage" \
  --settings codecoverage.runsettings \
  --results-directory "$results_dir"

report="$(find "$results_dir" -name 'coverage.cobertura.xml' | sort | head -1)"
if [[ -z "$report" ]]; then
  echo "::error::No Cobertura coverage report produced under $results_dir"
  exit 1
fi

read -r covered total <<<"$(python3 - "$report" <<'EOF'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
seen = {}
for cls in root.findall(".//class"):
    file = cls.get("filename", "")
    for line in cls.findall("lines/line"):
        number = line.get("number")
        if number is None:
            continue
        key = (file, number)
        hits = int(line.get("hits", "0"))
        seen[key] = max(seen.get(key, 0), hits)
covered = sum(1 for h in seen.values() if h > 0)
print(f"{covered} {len(seen)}")
EOF
)"

line_percent=0
if [[ "$total" -gt 0 ]]; then
  line_percent=$((covered * 100 / total))
fi

echo "Coverage report: $report"
echo "Line coverage:   $line_percent% ($covered/$total lines; threshold ${threshold}%)"

if [[ "$line_percent" -lt "$threshold" ]]; then
  echo "::error::Line coverage ${line_percent}% is below the required ${threshold}% gate."
  exit 1
fi

echo "Coverage gate passed."