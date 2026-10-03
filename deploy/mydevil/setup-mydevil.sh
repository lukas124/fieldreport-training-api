#!/bin/sh
# Jednorazowa (idempotentna) konfiguracja konta MyDevil pod FieldReport API.
# Uruchamiana NA HOŚCIE, np. z lokalnej maszyny:
#
#   rsync -a deploy/mydevil/ mydevil:apps/fieldreport-api/bin/
#   ssh mydevil sh apps/fieldreport-api/bin/setup-mydevil.sh
#
# Skrypty zawsze przez `sh`: MyDevil nie wykonuje plików z $HOME bez `devil binexec on` (nie włączamy).
#
# Konto jest współdzielone z blogiem (strefakodera.pl, beta., wp.) — skrypt dotyka WYŁĄCZNIE:
#   ~/apps/fieldreport-api/**, strony $DOMAIN, portu z opisem „fieldreport-api” i WŁASNYCH linii crona
#   (dopisuje, niczego nie usuwa ani nie przestawia).
#
# Kroki: katalogi → port (devil port add tcp random, gdy brak) → strona proxy → env z szablonu
#        (chmod 600, losowy Admin__Key) → cron @reboot + watchdog */5 → SSL Let's Encrypt (MyDevil) + sslonly.
#
# Baza PostgreSQL powstaje ręcznie (devil pyta o hasło): `devil pgsql db add fieldreport`, potem hasło
# wpisz w ConnectionStrings__FieldReport w pliku env. SSL wymaga, by DNS domeny wskazywał już na WEB_IP.
set -eu

DOMAIN="${DOMAIN:-mobilny.dev}"
WEB_IP="${WEB_IP:-185.36.169.6}"          # WEB IP konta (web88), nie IP węzła ssh
PORT_DESC="fieldreport-api"
APP_ROOT="$HOME/apps/fieldreport-api"
ENV_FILE="$APP_ROOT/fieldreport-api.env"
HERE="$(cd "$(dirname "$0")" && pwd -P)"
TEMPLATE="$HERE/env.template"
RUN="$APP_ROOT/bin/run.sh"

ESC="$(printf '\033')"
strip() { sed "s/${ESC}\[[0-9;]*m//g"; }   # devil koloruje wyjście (kody ANSI)

echo "== katalogi w $APP_ROOT"
mkdir -p "$APP_ROOT/app" "$APP_ROOT/data" "$APP_ROOT/secrets" "$APP_ROOT/logs" "$APP_ROOT/run" "$APP_ROOT/bin"
chmod 700 "$APP_ROOT/secrets" "$APP_ROOT/data"
if [ "$HERE" != "$(cd "$APP_ROOT/bin" && pwd -P)" ]; then   # $HOME bywa symlinkiem (/home → /usr/home)
  cp "$HERE/run.sh" "$HERE/env.template" "$APP_ROOT/bin/"
  cp "$0" "$APP_ROOT/bin/setup-mydevil.sh" 2>/dev/null || true
fi
chmod +x "$RUN" 2>/dev/null || true

echo "== port"
PORT=""
if [ -f "$ENV_FILE" ]; then
  PORT="$(sed -n 's#^ASPNETCORE_URLS=.*:\([0-9][0-9]*\).*#\1#p' "$ENV_FILE" | head -n1)"
fi
if [ -z "$PORT" ]; then
  PORT="$(devil port list | strip | awk -v d="$PORT_DESC" '$2 == "tcp" && $3 == d { print $1; exit }')"
fi
if [ -n "$PORT" ]; then
  if devil port list | strip | awk -v p="$PORT" '$1 == p && $2 == "tcp" { f = 1 } END { exit !f }'; then
    echo "   zarezerwowany: $PORT"
  else
    echo "!! port $PORT z env nie jest zarezerwowany na koncie (devil port list) — sprawdź ręcznie" >&2
    exit 1
  fi
else
  devil port add tcp random "$PORT_DESC" | strip
  PORT="$(devil port list | strip | awk -v d="$PORT_DESC" '$2 == "tcp" && $3 == d { print $1; exit }')"
  [ -n "$PORT" ] || { echo "!! nie udało się odczytać zarezerwowanego portu" >&2; exit 1; }
  echo "   nowy port: $PORT"
fi

echo "== strona $DOMAIN (proxy → localhost:$PORT)"
if devil www list | strip | awk -v d="$DOMAIN" '$1 == d { f = 1 } END { exit !f }'; then
  echo "   już istnieje: $(devil www list | strip | awk -v d="$DOMAIN" '$1 == d')"
else
  devil www add "$DOMAIN" proxy localhost "$PORT" | strip
fi

echo "== env $ENV_FILE"
if [ -f "$ENV_FILE" ]; then
  echo "   istnieje — bez zmian"
else
  ADMIN_KEY="$(openssl rand -hex 24)"
  umask 077
  sed -e "s#__PORT__#$PORT#g" -e "s#__HOME__#$HOME#g" -e "s#__ADMIN_KEY__#$ADMIN_KEY#g" "$TEMPLATE" > "$ENV_FILE"
  umask 022
  echo "   utworzono (Admin__Key wylosowany). UZUPEŁNIJ hasło bazy: __PGSQL_PASSWORD__ w $ENV_FILE"
fi
chmod 600 "$ENV_FILE"

echo "== cron (tylko WŁASNE linie — z $RUN; cudzych nie ruszam)"
# /bin/sh jawnie: MyDevil nie pozwala wykonywać plików z $HOME bez `devil binexec on` (tego nie włączamy)
CRON_BOOT="@reboot /bin/sh $RUN start >/dev/null 2>&1"
CRON_DOG="*/5 * * * * /bin/sh $RUN watchdog >/dev/null 2>&1"
CURRENT="$(crontab -l 2>/dev/null || true)"
# nasze linie w starszej postaci (np. bez /bin/sh) zastępujemy; każda inna linia zostaje bajt w bajt
KEEP="$(printf '%s\n' "$CURRENT" | grep -vF "$RUN " || true)"
OURS_NOW="$(printf '%s\n' "$CURRENT" | grep -F "$RUN " || true)"
WANT="$CRON_BOOT
$CRON_DOG"
if [ "$OURS_NOW" = "$WANT" ]; then
  echo "   linie już są"
else
  { [ -n "$KEEP" ] && printf '%s\n' "$KEEP"; printf '%s\n' "$WANT"; } | crontab -
  echo "   ustawiono:"; printf '     %s\n' "$CRON_BOOT" "$CRON_DOG"
fi

echo "== SSL Let's Encrypt przez MyDevil ($WEB_IP, SNI $DOMAIN)"
if devil ssl www list | strip | grep -qw "$DOMAIN"; then
  echo "   certyfikat już jest"
else
  if devil ssl www add "$WEB_IP" le le "$DOMAIN" | strip; then :; else
    echo "!! nie wydano certyfikatu (DNS? strona?) — ponów później: devil ssl www add $WEB_IP le le $DOMAIN" >&2
  fi
fi
if devil ssl www list | strip | grep -qw "$DOMAIN"; then
  devil www options "$DOMAIN" sslonly on | strip
fi

echo
echo "Gotowe. Port: $PORT. Teraz deploy (z lokalnej maszyny): MYDEVIL_HOST=mydevil bash deploy/mydevil/deploy.sh"
