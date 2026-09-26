# FieldReport API (szkoleniowe)

Backend do aplikacji **FieldReport** ze szkolenia „Podstawy .NET MAUI” (StrefaKodera.pl).
ASP.NET Core **net9.0**, Swagger, JWT (access + jednorazowy refresh), trwały zapis w JSON, zdjęcia,
rejestracja urządzeń i wysyłka push przez **Firebase Admin SDK** (FCM HTTP v1).

| Gdzie | Adres |
|---|---|
| Publicznie | `https://szkolenia.strefakodera.pl/maui/podstawowe/` (Swagger: `.../swagger`, health: `.../api/health`) |
| Lokalnie | `http://localhost:5080` (emulator Android: `http://10.0.2.2:5080`) |

## Kontrakt

| Metoda | Endpoint | Opis |
|---|---|---|
| POST | `/api/auth/login` | `{ email, pin }` → `{ accessToken, refreshToken, expiresIn, technicianName }` (dowolny poprawny e-mail + PIN `1234`) |
| POST | `/api/auth/refresh` | `{ refreshToken }` → nowa para tokenów (refresh jednorazowy) |
| GET | `/api/reports?updatedSince=` | zgłoszenia technika zmienione po dacie (ISO 8601; pusto = wszystkie) |
| GET | `/api/reports/{id}` | szczegóły |
| PUT | `/api/reports/{id}` | idempotentny upsert (`201` nowe / `200` aktualizacja; `id` od aplikacji) |
| PUT / GET | `/api/reports/{id}/photo` | zdjęcie (multipart, pole `photo`; JPEG/PNG/WebP/HEIC, maks. 5 MB) |
| POST | `/api/devices` | `{ token, platform: android\|ios }` — rejestracja do push |
| DELETE | `/api/devices/{token}` | wyrejestrowanie (idempotentne, `204`) |
| POST | `/api/dispatcher/reports` | dyspozytor: nowe zgłoszenie (`technicianEmail`; pusty = wszyscy) + push `report_assigned` |
| PATCH | `/api/dispatcher/reports/{id}/status` | dyspozytor: status + push `report_status_changed` |
| GET | `/api/dispatcher/reports`, `/api/dispatcher/technicians` | podgląd (id zgłoszeń, urządzenia) |
| POST | `/api/admin/reset[?technicianEmail=]` | reset danych, nagłówek `X-Admin-Key` |
| GET | `/api/health` | wersja, czas, `firebaseConfigured` |

Push (data): `{ "type": "report_assigned" | "report_status_changed", "reportId": "<guid>" }`, notification z tytułem,
kanał Android `reports`. Enumy w JSON jako tekst (`Low/Normal/High`, `New/InProgress/Done`).

Przykładowy `ReportDto`:

```json
{
  "id": "3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f",
  "title": "Awaria pompy",
  "description": "Pompa w węźle C2 nie startuje.",
  "priority": "High",
  "status": "New",
  "createdAt": "2026-09-26T08:15:00+00:00",
  "updatedAt": "2026-09-26T08:20:31.1234567+00:00",
  "hasPhoto": false,
  "photoUrl": null
}
```

## Uruchomienie lokalne

```bash
dotnet run --project src/FieldReport.Api                                  # http://localhost:5080, Admin key: dev-admin-key
dotnet run --project src/FieldReport.Api --launch-profile siec-sali       # 0.0.0.0:5080 — telefony w sieci sali
dotnet run --project src/FieldReport.Api --launch-profile demo-odswiezania # access token 60 s (demo 401 → refresh)

# push lokalnie: klucz konta serwisowego POZA repo
export GOOGLE_APPLICATION_CREDENTIALS=~/sciezka/poza/repo/firebase-sa.json
```

Konfiguracja (`appsettings.json`, nadpisywana zmiennymi `Sekcja__Klucz`): `PathBase`, `Hosting:Port`,
`Hosting:ListenOnAllInterfaces`, `Auth:*` (PIN, czasy życia tokenów), `Storage:*` (katalog danych, limity),
`Firebase:CredentialsPath` / `GOOGLE_APPLICATION_CREDENTIALS`, `Admin:Key`, `Dispatcher:Key`, `RateLimiting:*`.
Bez poświadczeń Firebase API działa normalnie, a dyspozytor zwraca `push.skipped = true`.

## Testy

```bash
dotnet test                                   # NUnit + WebApplicationFactory (bez sieci, bez Firebase)
scripts/smoke.sh http://localhost:5080        # smoke przez curl na działającym API
ADMIN_KEY=... scripts/smoke.sh https://szkolenia.strefakodera.pl/maui/podstawowe   # + sprzątanie po sobie
```

## Wdrożenie

Hosting: **MyDevil** (FreeBSD, konto współdzielone z blogiem — skrypty dotykają tylko `~/apps/fieldreport-api`,
strony `szkolenia.strefakodera.pl`, portu z opisem `fieldreport-api` i własnych linii crona).
Artefakt: `dotnet publish -c Release` **framework-dependent, bez RID** (host ma `/usr/local/bin/dotnet` z runtime 9).
Za HTTPS odpowiada MyDevil (strona typu `proxy` → `localhost:<port>`, certyfikat Let's Encrypt z automatycznym
odnawianiem po stronie MyDevil, `sslonly on`). DNS w Cloudflare: rekord A **DNS only** (bez proxy CF).

Układ na hoście:

```
~/apps/fieldreport-api/
  app/                   artefakt (JEDYNY katalog czyszczony przez deploy: rsync --delete)
  bin/run.sh             start|stop|restart|status|health|watchdog (+ setup-mydevil.sh, env.template)
  fieldreport-api.env    konfiguracja, chmod 600 (port, PathBase, Admin__Key, GOOGLE_APPLICATION_CREDENTIALS)
  data/                  data.json, zdjęcia, klucz podpisu JWT
  secrets/firebase-sa.json   klucz konta serwisowego (wgrywany ręcznie)
  logs/api.log           log (rotacja 10 MB × 5)
  run/api.pid
cron: @reboot run.sh start  |  */5 * * * * run.sh watchdog  (start gdy nie działa, restart gdy 2× brak health)
```

### Pierwsza konfiguracja (raz)

```bash
ssh mydevil mkdir -p apps/fieldreport-api/bin
rsync -a deploy/mydevil/ mydevil:apps/fieldreport-api/bin/
ssh mydevil sh apps/fieldreport-api/bin/setup-mydevil.sh
```

### Wdrożenie z lokalnej maszyny

```bash
dotnet test
dotnet publish src/FieldReport.Api -c Release -o artifacts/publish
MYDEVIL_HOST=mydevil bash deploy/mydevil/deploy.sh      # alias z ~/.ssh/config
scripts/smoke.sh https://szkolenia.strefakodera.pl/maui/podstawowe
```

### Wdrożenie przez GitHub Actions

`Actions → deploy → Run workflow` (tylko z `main`): testy → publish → `deploy/mydevil/deploy.sh` → smoke publiczny.
Sekrety repozytorium: `MYDEVIL_HOST`, `MYDEVIL_USER`, `MYDEVIL_SSH_PORT`, `MYDEVIL_SSH_KEY` (klucz prywatny,
którego publiczna część jest w `~/.ssh/authorized_keys` na MyDevil), opcjonalnie `FIELDREPORT_ADMIN_KEY`
(smoke sprząta po sobie). CI (`ci.yml`) buduje i testuje każdy push do `main` i PR.

### Push (Firebase) na serwerze

```bash
scp firebase-sa.json mydevil:apps/fieldreport-api/secrets/firebase-sa.json
ssh mydevil 'chmod 600 apps/fieldreport-api/secrets/firebase-sa.json && apps/fieldreport-api/bin/run.sh restart'
curl -s https://szkolenia.strefakodera.pl/maui/podstawowe/api/health   # firebaseConfigured: true
```

### Operacje

```bash
ssh mydevil apps/fieldreport-api/bin/run.sh status        # restart | stop | start | health
ssh mydevil tail -n 100 apps/fieldreport-api/logs/api.log
ssh mydevil grep ^Admin__Key apps/fieldreport-api/fieldreport-api.env
curl -X POST -H "X-Admin-Key: <klucz>" https://szkolenia.strefakodera.pl/maui/podstawowe/api/admin/reset
```
