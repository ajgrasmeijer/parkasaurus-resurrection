#!/usr/bin/env bash
# Logs GPU load and the game's CPU use per thread while Parkasaurus runs, next to the mod's
# PerfProbe log: BepInEx/perf/gpu-<date>.csv and threads-<date>.log.
# Waits for a new game start (ignores a game that is already running when this starts) and
# stops when the game exits.
set -u
OUT=~/.local/share/Steam/steamapps/common/Parkasaurus/BepInEx/perf
mkdir -p "$OUT"

game_pid() { ps -eo pid,comm | awk '$2 == "Parkasaurus.exe" { print $1; exit }'; }

old=$(game_pid)
echo "Waiting for Parkasaurus to start${old:+ (ignoring running pid $old)}..."
while :; do
    pid=$(game_pid)
    [ -n "$pid" ] && [ "$pid" != "$old" ] && break
    [ -z "$pid" ] && old=""
    sleep 2
done
stamp=$(date +%Y-%m-%d_%H%M%S)
echo "Game started (pid $pid), logging to $OUT/*-$stamp.*"

nvidia-smi --query-gpu=timestamp,utilization.gpu,utilization.memory,memory.used,clocks.gr,clocks.mem,power.draw,temperature.gpu,pstate,clocks_throttle_reasons.active \
    --format=csv -l 1 > "$OUT/gpu-$stamp.csv" &
smi=$!

# Every 10 s: the busiest threads of the game (CPU % over the last second)
while kill -0 "$pid" 2>/dev/null; do
    {
        echo "== $(date +%H:%M:%S)"
        top -H -b -d 1 -n 2 -p "$pid" | awk '/^top -/{n++} n==2' | sed -n '7,22p'
    } >> "$OUT/threads-$stamp.log"
    sleep 9
done

kill "$smi" 2>/dev/null
echo "Game exited, logs written."
