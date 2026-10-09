#!/bin/bash
# Usage: ./bench.sh [runs] [model]. Runs the harness repeatedly while sampling memory.
RUNS=${1:-4}
MODEL=${2:-qwen2.5-7b}
cd "$(dirname "$0")"

sample() {
  local out=$1
  : > "$out"
  while true; do
    vm_stat | awk -v swap="$(sysctl -n vm.swapusage | awk '{print $6}' | tr -d 'M')" '
      /page size of/ {ps=$8}
      /Pages active/ {a=$3} /Pages wired/ {w=$4} /Pages occupied by compressor/ {c=$5}
      END {printf "%.0f %.0f\n", (a+w+c)*ps/1048576, swap}' | tr -d '.' >> "$out.sys"
    ps -axo rss,command | awk '/ToolCallingScratch/ && !/awk/ {s+=$1} END {printf "%.0f\n", s/1024}' >> "$out"
    sleep 0.5
  done
}

for i in $(seq 1 "$RUNS"); do
  base=$(mktemp -u /tmp/memsample.XXXX)
  : > "$base.sys"
  sample "$base" &
  spid=$!
  log=$(dotnet run --no-build -- "$MODEL" 8192 2>&1)
  kill $spid 2>/dev/null; wait $spid 2>/dev/null
  load=$(echo "$log" | grep -o "Loading model... it took [0-9,]* ms" | grep -o "[0-9,]* ms")
  chat=$(echo "$log" | grep -o "Sending prompt... it took [0-9,]* ms" | grep -o "[0-9,]* ms")
  res=$(echo "$log" | grep -oE "SUCCESS|FAIL[^.]*" | head -1)
  peak_rss=$(sort -n "$base" | tail -1)
  peak_sys=$(awk '{print $1}' "$base.sys" | sort -n | tail -1)
  base_sys=$(awk 'NR==1{print $1}' "$base.sys")
  peak_swap=$(awk '{print $2}' "$base.sys" | sort -n | tail -1)
  echo "run $i: load=$load chat=$chat result=$res procRSS=${peak_rss}MB sysUsed=${base_sys}->${peak_sys}MB swapUsed=${peak_swap}MB"
  rm -f "$base" "$base.sys"
done
