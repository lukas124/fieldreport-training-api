#!/bin/bash
# Wdrożenie FieldReport API na MyDevil: artefakt `dotnet publish` (framework-dependent, net9.0) → rsync → restart → health.
# Build odbywa się POZA serwerem (GitHub Actions albo lokalna maszyna); host ma tylko runtime .NET (/usr/local/bin/dotnet).
#
# Env:
#   MYDEVIL_HOST      host albo alias z ~/.ssh/config (np. „mydevil”)            [wymagane]
#   MYDEVIL_USER      użytkownik (pusty = z ~/.ssh/config / aliasu)
#   MYDEVIL_SSH_PORT  port ssh (pusty = z ~/.ssh/config, domyślnie 22)
#   PUBLISH_DIR       katalog artefaktu (domyślnie artifacts/publish; brak → dotnet publish)
#
# Z lokalnej maszyny:  MYDEVIL_HOST=mydevil bash deploy/mydevil/deploy.sh
# Z GitHub Actions:    .github/workflows/deploy.yml (sekrety MYDEVIL_HOST/USER/SSH_PORT/SSH_KEY)
#
# Na hoście (~/apps/fieldreport-api): rsync --delete WYŁĄCZNIE do app/. Env (fieldreport-api.env), data/, secrets/, logs/
# leżą obok i nie są ruszane. Pierwsze uruchomienie wymaga setup-mydevil.sh (tworzy env, port, stronę, cron).
set -euo pipefail

: "${MYDEVIL_HOST:?ustaw MYDEVIL_HOST (np. mydevil albo s88.mydevil.net)}"
REMOTE="${MYDEVIL_USER:+$MYDEVIL_USER@}$MYDEVIL_HOST"
SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=20)
[ -n "${MYDEVIL_SSH_PORT:-}" ] && SSH_OPTS+=(-p "$MYDEVIL_SSH_PORT")
SSH=(ssh "${SSH_OPTS[@]}")
RSYNC_SSH="ssh ${SSH_OPTS[*]}"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PUBLISH_DIR="${PUBLISH_DIR:-$ROOT/artifacts/publish}"
REMOTE_ROOT="apps/fieldreport-api"          # względem $HOME na hoście
REMOTE_APP="$REMOTE_ROOT/app/"              # jedyny cel rsync --delete

if [ ! -f "$PUBLISH_DIR/FieldReport.Api.dll" ]; then
  echo "== dotnet publish → $PUBLISH_DIR"
  dotnet publish "$ROOT/src/FieldReport.Api/FieldReport.Api.csproj" -c Release -o "$PUBLISH_DIR" --nologo
fi
# artefakt nie może nieść danych ani sekretów
for bad in data secrets appsettings.Development.json; do
  [ ! -e "$PUBLISH_DIR/$bad" ] || { echo "ERROR: $PUBLISH_DIR/$bad nie powinno trafić na serwer" >&2; exit 1; }
done
find "$PUBLISH_DIR" -maxdepth 1 \( -name '*.p8' -o -name '*firebase*.json' -o -name '*.env' \) | grep -q . \
  && { echo "ERROR: w artefakcie są pliki wyglądające na sekrety" >&2; exit 1; } || true

echo "== host: $REMOTE"
"${SSH[@]}" "$REMOTE" "test -f $REMOTE_ROOT/fieldreport-api.env" \
  || { echo "ERROR: brak ~/$REMOTE_ROOT/fieldreport-api.env — najpierw setup-mydevil.sh (README → Wdrożenie)" >&2; exit 1; }

echo "== skrypty → ~/$REMOTE_ROOT/bin/"
rsync -az -e "$RSYNC_SSH" "$ROOT/deploy/mydevil/run.sh" "$ROOT/deploy/mydevil/setup-mydevil.sh" \
  "$ROOT/deploy/mydevil/env.template" "$REMOTE:$REMOTE_ROOT/bin/"
"${SSH[@]}" "$REMOTE" "chmod +x $REMOTE_ROOT/bin/run.sh"

echo "== rsync artefaktu → ~/$REMOTE_APP"
rsync -az --delete --delay-updates -e "$RSYNC_SSH" "$PUBLISH_DIR/" "$REMOTE:$REMOTE_APP"

echo "== restart"
"${SSH[@]}" "$REMOTE" "$REMOTE_ROOT/bin/run.sh restart"

echo "== smoke (health na hoście)"
for i in 1 2 3 4 5; do
  if OUT="$("${SSH[@]}" "$REMOTE" "$REMOTE_ROOT/bin/run.sh health")"; then
    echo "$OUT"
    echo "Done."
    exit 0
  fi
  sleep 3
done
echo "ERROR: health nie odpowiada — logi: ssh $REMOTE tail -n 100 $REMOTE_ROOT/logs/api.log" >&2
exit 1
