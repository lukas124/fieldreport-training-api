#!/usr/bin/env bash
# Smoke test API FieldReport przez curl (bez jq).
#
#   scripts/smoke.sh http://localhost:5080
#   scripts/smoke.sh https://mobilny.dev/maui/podstawowe
#   ADMIN_KEY=... scripts/smoke.sh <baseUrl>   # na końcu usuwa dane technika smoke (admin reset)
#
# Przebieg: health → login (+401) → refresh → PUT ×2 (201/200) → GET lista/szczegóły/updatedSince →
# zdjęcie PUT/GET → devices POST → dispatcher technicians/reports/status → DELETE device → [reset].
set -euo pipefail

BASE="${1:?użycie: scripts/smoke.sh <baseUrl>}"
BASE="${BASE%/}"
EMAIL="${SMOKE_EMAIL:-smoke.$(date +%s)$RANDOM@example.com}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
PASS=0

uuid() {
  if command -v uuidgen >/dev/null 2>&1; then uuidgen | tr 'A-Z' 'a-z'
  else cat /proc/sys/kernel/random/uuid; fi
}
# json_str <plik> <pole> → pierwsza wartość tekstowa pola (proste JSON-y API, bez zagnieżdżonych cudzysłowów)
json_str() { sed -n "s/.*\"$2\":\"\([^\"]*\)\".*/\1/p" "$1" | head -n1; }
fail() { echo "FAIL: $*" >&2; [ -f "$TMP/body" ] && { echo "--- odpowiedź:" >&2; head -c 2000 "$TMP/body" >&2; echo >&2; }; exit 1; }
ok() { PASS=$((PASS + 1)); printf '  ok  %s\n' "$*"; }

# req <oczekiwany kod> <metoda> <ścieżka> [argumenty curl...] → treść w $TMP/body
req() {
  local expected="$1" method="$2" path="$3"; shift 3
  local code
  code="$(curl -sS -o "$TMP/body" -w '%{http_code}' -X "$method" "$@" "$BASE$path")" || fail "$method $path: błąd połączenia"
  [ "$code" = "$expected" ] || fail "$method $path: HTTP $code (oczekiwano $expected)"
  ok "$method $path → $code"
}
contains() { grep -q -- "$1" "$TMP/body" || fail "brak '$1' w odpowiedzi"; }

echo "== smoke $BASE (technik: $EMAIL)"

req 200 GET /api/health
contains '"status":"ok"'
echo "     $(json_str "$TMP/body" firebaseStatus) | pathBase='$(json_str "$TMP/body" pathBase)'"

req 401 POST /api/auth/login -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"pin\":\"0000\"}"
req 200 POST /api/auth/login -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"pin\":\"1234\"}"
REFRESH="$(json_str "$TMP/body" refreshToken)"
[ -n "$REFRESH" ] || fail "brak refreshToken"
req 200 POST /api/auth/refresh -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$REFRESH\"}"
TOKEN="$(json_str "$TMP/body" accessToken)"
[ -n "$TOKEN" ] || fail "brak accessToken"
AUTH=(-H "Authorization: Bearer $TOKEN")

req 401 GET /api/reports

ID="$(uuid)"
REPORT="{\"id\":\"$ID\",\"title\":\"Smoke: awaria pompy\",\"description\":\"test\",\"priority\":\"High\",\"status\":\"New\",\"createdAt\":\"2026-09-26T08:15:00+00:00\"}"
req 201 PUT "/api/reports/$ID" "${AUTH[@]}" -H 'Content-Type: application/json' -d "$REPORT"
FIRST_UPDATED="$(json_str "$TMP/body" updatedAt)"
req 200 PUT "/api/reports/$ID" "${AUTH[@]}" -H 'Content-Type: application/json' -d "${REPORT/\"New\"/\"InProgress\"}"
contains '"status":"InProgress"'

req 200 GET /api/reports "${AUTH[@]}"
[ "$(grep -o "\"id\":\"$ID\"" "$TMP/body" | wc -l | tr -d ' ')" = "1" ] || fail "PUT ×2 nie dał dokładnie 1 rekordu"
req 200 GET "/api/reports/$ID" "${AUTH[@]}"
req 200 GET "/api/reports?updatedSince=$(printf %s "$FIRST_UPDATED" | sed 's/+/%2B/g')" "${AUTH[@]}"
contains "\"id\":\"$ID\""

printf '\377\330\377\340smoke-jpeg\377\331' > "$TMP/photo.jpg"
req 200 PUT "/api/reports/$ID/photo" "${AUTH[@]}" -F "photo=@$TMP/photo.jpg;type=image/jpeg"
contains '"hasPhoto":true'
echo "     photoUrl=$(json_str "$TMP/body" photoUrl)"
req 200 GET "/api/reports/$ID/photo" "${AUTH[@]}"
cmp -s "$TMP/body" "$TMP/photo.jpg" || fail "pobrane zdjęcie różni się od wysłanego"

DEVICE="smoke-token-$(uuid)"
req 200 POST /api/devices "${AUTH[@]}" -H 'Content-Type: application/json' -d "{\"token\":\"$DEVICE\",\"platform\":\"android\"}"

req 200 GET /api/dispatcher/technicians
contains "$(printf %s "$EMAIL" | tr 'A-Z' 'a-z')"
req 200 POST /api/dispatcher/reports -H 'Content-Type: application/json' \
  -d "{\"technicianEmail\":\"$EMAIL\",\"title\":\"Smoke: od dyspozytora\",\"priority\":\"Normal\"}"
contains '"type":"report_assigned"'
echo "     push: $(json_str "$TMP/body" message)"
req 200 PATCH "/api/dispatcher/reports/$ID/status" -H 'Content-Type: application/json' -d '{"status":"Done"}'
contains '"type":"report_status_changed"'
req 200 GET "/api/reports/$ID" "${AUTH[@]}"
contains '"status":"Done"'

req 204 DELETE "/api/devices/$DEVICE" "${AUTH[@]}"
req 200 GET /swagger/v1/swagger.json
contains '"servers"'

if [ -n "${ADMIN_KEY:-}" ]; then
  req 200 POST "/api/admin/reset?technicianEmail=$EMAIL" -H "X-Admin-Key: $ADMIN_KEY"
fi

echo "== SMOKE OK ($PASS kroków)"
