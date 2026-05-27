#!/usr/bin/env bash
#
# Live data-only smoke run of the Finam brokerage inside the real LEAN engine.
#
# Builds the LEAN Launcher + the Finam brokerage + a data-only demo algorithm, stages the two plugin
# DLLs into the Launcher output (where the Composer discovers them), writes a config with the live-finam
# environment (token injected from .smoke-token, never committed), and runs the Launcher for a bounded
# time. The demo algorithm places NO orders.
#
# Usage:  FINAM_RUN_SECONDS=90 QC_FINAM_ACCOUNT_ID=1680964 ./live-demo/run-live-demo.sh
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
LEAN="$REPO/../trading-reference-applications/Lean"
LAUNCHER_CSProj="$LEAN/Launcher/QuantConnect.Lean.Launcher.csproj"
LAUNCHER_BIN="$LEAN/Launcher/bin/Release"
DATA_FOLDER="$LEAN/Data"
ACCOUNT_ID="${QC_FINAM_ACCOUNT_ID:-1680964}"
RUN_SECONDS="${FINAM_RUN_SECONDS:-90}"
CFG="Release"

if [[ ! -s "$REPO/.smoke-token" ]]; then
  echo "ERROR: $REPO/.smoke-token is missing/empty (put the Finam API token there)." >&2
  exit 1
fi
TOKEN="$(cat "$REPO/.smoke-token")"

echo "== build Launcher =="
dotnet build "$LAUNCHER_CSProj" -c "$CFG" -v q --nologo

echo "== build Finam brokerage + demo algorithm =="
dotnet build "$REPO/QuantConnect.FinamBrokerage/QuantConnect.FinamBrokerage.csproj" -c "$CFG" -v q --nologo
dotnet build "$REPO/live-demo/QuantConnect.FinamBrokerage.DemoAlgorithm/QuantConnect.FinamBrokerage.DemoAlgorithm.csproj" -c "$CFG" -v q --nologo

echo "== stage plugin DLLs into Launcher output =="
cp -f "$REPO/QuantConnect.FinamBrokerage/bin/$CFG/net10.0/QuantConnect.Brokerages.Finam.dll" "$LAUNCHER_BIN/"
cp -f "$REPO/live-demo/QuantConnect.FinamBrokerage.DemoAlgorithm/bin/$CFG/net10.0/QuantConnect.FinamBrokerage.DemoAlgorithm.dll" "$LAUNCHER_BIN/"

echo "== write config.json (token injected, not committed) =="
# Use a non-/ delimiter for sed because the token and paths contain slashes.
sed -e "s|__DATA_FOLDER__|$DATA_FOLDER|" \
    -e "s|__ACCOUNT__|$ACCOUNT_ID|" \
    -e "s|__TOKEN__|$TOKEN|" \
    "$REPO/live-demo/config.live-finam.json" > "$LAUNCHER_BIN/config.json"

echo "== run LEAN Launcher (live-finam) for ${RUN_SECONDS}s =="
cd "$LAUNCHER_BIN"
timeout "${RUN_SECONDS}" dotnet QuantConnect.Lean.Launcher.dll || true

# Scrub the token from the staged config when done.
rm -f "$LAUNCHER_BIN/config.json"
echo "== done (staged config removed) =="
