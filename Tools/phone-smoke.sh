#!/usr/bin/env bash
# Smoke-test the installed Color Splash APK on a USB phone: one autopilot match per special, the summary and the
# special screenshots pulled to Builds/PhoneSmoke/<special>/. Unity reads player args from the `unity` intent extra.
# usage: Tools/phone-smoke.sh SERIAL special [special …]
export MSYS_NO_PATHCONV=1   # (Git Bash: never rewrite the device paths)
D=$1; shift
P=com.zoiko.colorsplash; A=$P/com.unity3d.player.UnityPlayerGameActivity
F=/storage/emulated/0/Android/data/$P/files
OUT="$(dirname "$0")/../Builds/PhoneSmoke"
adb -s $D shell svc power stayon true
for s in "$@"; do
  # the phone dropped off USB: stop here (rerun the rest once it is back) instead of spinning
  if ! timeout 60 adb -s $D wait-for-device; then echo "$s NO DEVICE (stopped)"; exit 1; fi
  adb -s $D shell am force-stop $P
  adb -s $D shell "rm -f $F/*.png $F/summary.txt"
  adb -s $D shell input keyevent KEYCODE_WAKEUP
  adb -s $D shell am start -n $A -e unity "'-smoketest -smoketest-special $s -smoketest-seconds 90 -smoketest-timescale 2'" > /dev/null
  verdict="TIMEOUT"
  for i in $(seq 1 80); do
    sleep 3
    v=$(adb -s $D shell "grep -E '^(PASS|FAIL)' $F/summary.txt 2>/dev/null" | tail -1 | tr -d '\r')
    if [ -n "$v" ]; then verdict=$v; break; fi
  done
  mkdir -p "$OUT/$s"; rm -f "$OUT/$s"/*
  adb -s $D pull $F/summary.txt "$OUT/$s/" > /dev/null 2>&1
  for f in $(adb -s $D shell "ls $F" | tr -d '\r' | grep -E '^(special_|0[45]_)'); do adb -s $D pull $F/$f "$OUT/$s/" > /dev/null 2>&1; done
  echo "$s $verdict $(grep -h '^fps' "$OUT/$s/summary.txt" 2>/dev/null) | $(grep -h 'local special' "$OUT/$s/summary.txt" 2>/dev/null)"
done
adb -s $D shell am force-stop $P
adb -s $D shell svc power stayon false
