#!/bin/bash
# Launcher used by the editor app (src/TheGarden.Desktop/WorldClimateJob.cs) through wsl.exe.
#   run_app.sh REPO_LINUX_PATH JOB_ID INPUT_GARDEN_LINUX_PATH WIDTHxHEIGHT [--deepwater LAT,LON ...]
# Outputs go to the WSL file system ($HOME/wc-out/ui/JOB_ID): the D: mount drops briefly every few
# minutes and long writes to it fail, so the app reads the results through \wsl.localhost\<distro>\...
# Resource limits as in artifacts/structure-prototype-20261019/scripts/run_limited.sh (shared machine).
REPO=$1; JOB=$2; INPUT=$3; FRAME=$4; shift 4
OUT=$HOME/wc-out/ui/$JOB; mkdir -p "$OUT" || exit 1
cd "$REPO" || exit 1
export OMP_NUM_THREADS=2 OPENBLAS_NUM_THREADS=2 MKL_NUM_THREADS=2 NUMEXPR_NUM_THREADS=2
export PYTHONPATH=tools/climate MPLCONFIGDIR=$HOME/.cache/worldclimate-mpl
ulimit -v $((4*1024*1024))
exec timeout 2400 nice -n 15 .runtime/seasonal-engine/bin/python -m worldclimate.run "$INPUT" --out "$OUT" \
    --app "$FRAME" --status "$OUT/status.json" --cancel "$OUT/cancel.request" "$@"
