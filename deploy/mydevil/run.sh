#!/bin/sh
# Sterowanie procesem FieldReport API na MyDevil (FreeBSD, bez systemd/Passengera).
#
#   sh run.sh start|stop|restart|status|health|watchdog
#   (przez `sh` — MyDevil nie wykonuje plików z $HOME bez `devil binexec on`)
#
# Układ ($APP_ROOT = ~/apps/fieldreport-api):
#   app/                  artefakt `dotnet publish` (jedyny katalog czyszczony przez deploy: rsync --delete)
#   fieldreport-api.env   konfiguracja (chmod 600) — czytana przy każdym starcie
#   data/                 dane (data.json, zdjęcia, klucz JWT)
#   secrets/              klucz konta serwisowego Firebase
#   logs/api.log          stdout/stderr (rotacja przy starcie i w watchdogu)
#   run/api.pid           pidfile
#   bin/run.sh            ten skrypt (cron: @reboot start, */5 watchdog)
set -eu

APP_ROOT="${APP_ROOT:-$HOME/apps/fieldreport-api}"
ENV_FILE="$APP_ROOT/fieldreport-api.env"
APP_DIR="$APP_ROOT/app"
LOG_DIR="$APP_ROOT/logs"
LOG="$LOG_DIR/api.log"
PIDFILE="$APP_ROOT/run/api.pid"
DOTNET="${DOTNET:-/usr/local/bin/dotnet}"
DLL="FieldReport.Api.dll"
LOG_MAX_BYTES=$((10 * 1024 * 1024))
LOG_KEEP=5

mkdir -p "$LOG_DIR" "$APP_ROOT/run"

load_env() {
  [ -f "$ENV_FILE" ] || { echo "Brak $ENV_FILE — uruchom setup-mydevil.sh" >&2; exit 1; }
  set -a
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  set +a
}

port() { sed -n 's#^ASPNETCORE_URLS=.*:\([0-9][0-9]*\).*#\1#p' "$ENV_FILE" | head -n1; }
pathbase() { sed -n 's#^PathBase=\(.*\)$#\1#p' "$ENV_FILE" | head -n1; }
health_url() { echo "http://127.0.0.1:$(port)$(pathbase)/api/health"; }

pid() { [ -f "$PIDFILE" ] && cat "$PIDFILE" 2>/dev/null || true; }

# działa = pid żyje ORAZ to nasz proces (pid mógł zostać użyty ponownie po restarcie hosta)
is_running() {
  p="$(pid)"
  [ -n "$p" ] || return 1
  kill -0 "$p" 2>/dev/null || return 1
  ps -p "$p" -o command= 2>/dev/null | grep -q "$DLL"
}

healthy() { /usr/local/bin/curl -fsS -m 5 -o /dev/null "$(health_url)" 2>/dev/null; }

rotate() {
  [ -f "$LOG" ] || return 0
  size="$(wc -c < "$LOG" | tr -d ' ')"
  [ "$size" -ge "$LOG_MAX_BYTES" ] || return 0
  i=$LOG_KEEP
  while [ $i -gt 1 ]; do
    prev=$((i - 1))
    [ -f "$LOG.$prev" ] && mv -f "$LOG.$prev" "$LOG.$i"
    i=$prev
  done
  # copy-truncate: proces pisze z O_APPEND (>>), więc obcięcie w miejscu jest bezpieczne
  cp "$LOG" "$LOG.1" && : > "$LOG"
}

start() {
  if is_running; then echo "Już działa (pid $(pid))."; return 0; fi
  [ -f "$APP_DIR/$DLL" ] || { echo "Brak $APP_DIR/$DLL — najpierw deploy." >&2; exit 1; }
  load_env
  rotate
  cd "$APP_DIR"
  echo "=== $(date '+%Y-%m-%d %H:%M:%S') start" >> "$LOG"
  nohup "$DOTNET" "$APP_DIR/$DLL" >> "$LOG" 2>&1 < /dev/null &
  echo $! > "$PIDFILE"
  i=0
  while [ $i -lt 40 ]; do
    if ! is_running; then
      echo "Proces zakończył się przy starcie — ostatnie linie logu:" >&2
      tail -n 30 "$LOG" >&2
      exit 1
    fi
    if healthy; then echo "Uruchomiono (pid $(pid), port $(port))."; return 0; fi
    sleep 1; i=$((i + 1))
  done
  echo "Proces działa (pid $(pid)), ale health nie odpowiada po 40 s: $(health_url)" >&2
  exit 1
}

stop() {
  if ! is_running; then echo "Nie działa."; rm -f "$PIDFILE"; return 0; fi
  p="$(pid)"
  kill -TERM "$p" 2>/dev/null || true
  i=0
  while kill -0 "$p" 2>/dev/null && [ $i -lt 15 ]; do sleep 1; i=$((i + 1)); done
  kill -0 "$p" 2>/dev/null && kill -KILL "$p" 2>/dev/null || true
  rm -f "$PIDFILE"
  echo "=== $(date '+%Y-%m-%d %H:%M:%S') stop (pid $p)" >> "$LOG"
  echo "Zatrzymano (pid $p)."
}

status() {
  if is_running; then
    echo "Działa: pid $(pid), port $(port), health: $(healthy && echo OK || echo BRAK)"
    ps -p "$(pid)" -o pid= -o rss= -o etime= -o command= 2>/dev/null || true   # FreeBSD: osobne -o
  else
    echo "Nie działa."
    return 3
  fi
}

# cron co 5 min: proces nie żyje → start; żyje, ale 2× bez health (odstęp 10 s) → restart; duży log → rotacja
watchdog() {
  rotate
  if ! is_running; then
    echo "=== $(date '+%Y-%m-%d %H:%M:%S') watchdog: proces nie działa → start" >> "$LOG"
    start
    return
  fi
  healthy && return 0
  sleep 10
  healthy && return 0
  echo "=== $(date '+%Y-%m-%d %H:%M:%S') watchdog: brak health → restart" >> "$LOG"
  stop
  start
}

case "${1:-}" in
  start) start ;;
  stop) stop ;;
  restart) stop; start ;;
  status) status ;;
  health) /usr/local/bin/curl -fsS -m 5 "$(health_url)"; echo ;;
  watchdog) watchdog ;;
  *) echo "użycie: sh $0 start|stop|restart|status|health|watchdog" >&2; exit 2 ;;
esac
