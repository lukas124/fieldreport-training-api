#!/bin/sh
# Stary adres API → 301 na nowy, z zachowaniem ścieżki i parametrów:
#   https://szkolenia.strefakodera.pl/maui/podstawowe/swagger → https://mobilny.dev/maui/podstawowe/swagger
# Strona typu php z jednym plikiem .htaccess. Uruchamiana NA HOŚCIE (idempotentna):
#   ssh mydevil sh apps/fieldreport-api/bin/redirect-old-domain.sh
set -eu

OLD_DOMAIN="${OLD_DOMAIN:-szkolenia.strefakodera.pl}"
NEW_ORIGIN="${NEW_ORIGIN:-https://mobilny.dev}"
WEB_IP="${WEB_IP:-185.36.169.6}"
ESC="$(printf '\033')"
strip() { sed "s/${ESC}\[[0-9;]*m//g"; }
DIR="$HOME/domains/$OLD_DOMAIN/public_html"

TYPE="$(devil www list | strip | awk -v d="$OLD_DOMAIN" '$1 == d { print $2 }')"
if [ "$TYPE" != "php" ]; then
  [ -n "$TYPE" ] && devil www del "$OLD_DOMAIN" | strip
  devil www add "$OLD_DOMAIN" php | strip
fi

mkdir -p "$DIR"
rm -f "$DIR/index.html"   # strona startowa MyDevil
cat > "$DIR/.htaccess" <<HTACCESS
RewriteEngine On
RewriteRule ^ $NEW_ORIGIN%{REQUEST_URI} [R=301,L]
HTACCESS

# https na starym adresie też musi działać, żeby przekierowanie zadziałało dla linków https://
devil ssl www list | strip | grep -qw "$OLD_DOMAIN" || devil ssl www add "$WEB_IP" le le "$OLD_DOMAIN" | strip
devil www options "$OLD_DOMAIN" sslonly on | strip
echo "Gotowe: $OLD_DOMAIN → $NEW_ORIGIN (301)"
