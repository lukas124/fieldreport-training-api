# FieldReport API (szkoleniowe)

Backend do aplikacji **FieldReport** ze szkolenia „Podstawy .NET MAUI” (StrefaKodera.pl).
ASP.NET Core **net9.0**, Swagger, JWT (access + jednorazowy refresh), **PostgreSQL** (EF Core + Npgsql), zdjęcia,
rejestracja urządzeń i wysyłka push przez **Firebase Admin SDK** (FCM HTTP v1).

| Gdzie | Adres |
|---|---|
| Publicznie | `https://mobilny.dev/maui/podstawowe/` (Swagger: `.../swagger`, health: `.../api/health`) |
| Inne ścieżki domeny | `https://mobilny.dev/…` → 301 `https://strefakodera.pl/szkolenia` |
| Stary adres | `https://szkolenia.strefakodera.pl/…` → 301 na ten sam adres w `mobilny.dev` |
| Lokalnie | `http://localhost:5080` (emulator Android: `http://10.0.2.2:5080`) |

## Kontrakt

| Metoda | Endpoint | Opis |
|---|---|---|
| POST | `/api/auth/login` | `{ email, pin }` → `{ accessToken, refreshToken, expiresIn, technicianName }` (dowolny poprawny e-mail + PIN `1234`) |
| POST | `/api/auth/refresh` | `{ refreshToken }` → nowa para tokenów (refresh jednorazowy) |
| GET | `/api/reports` | wszystkie zgłoszenia technika (od ostatnio zmienionego) |
| GET | `/api/reports/{id}` | szczegóły |
| POST | `/api/reports` | nowe zgłoszenie, nagłówek **`Idempotency-Key`** (wymagany; `id` nadaje serwer) → `201` ze zgłoszeniem; powtórka z tym samym kluczem → `201` z tym samym zgłoszeniem; ten sam klucz + inna treść → `422`; ten sam klucz przetwarzany równolegle → `409`; brak nagłówka → `400`; limit `Storage:MaxReportsPerTechnician` (domyślnie 500) → `409` |
| PUT | `/api/reports/{id}` | aktualizacja istniejącego zgłoszenia (np. status) → `200`; brak zgłoszenia → `404` |
| PUT / GET | `/api/reports/{id}/photo` | zdjęcie (multipart, pole `photo`; JPEG/PNG/WebP/HEIC, maks. 5 MB) |
| POST | `/api/devices` | `{ token, platform: android\|ios }` — rejestracja do push |
| DELETE | `/api/devices/{token}` | wyrejestrowanie (idempotentne, `204`) |
| POST | `/api/dispatcher/reports` | dyspozytor: nowe zgłoszenie (`technicianEmail`; pusty = wszyscy) + push `report_assigned` |
| PATCH | `/api/dispatcher/reports/{id}/status` | dyspozytor: status + push `report_status_changed` |
| GET | `/api/dispatcher/reports`, `/api/dispatcher/technicians` | podgląd (id zgłoszeń, urządzenia) |
| POST | `/api/admin/reset[?technicianEmail=]` | reset danych, nagłówek `X-Admin-Key` |
| GET | `/api/health` | wersja, czas, `databaseConnected`, `firebaseConfigured` (503, gdy brak bazy) |

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

## Nowe zgłoszenie: nagłówek `Idempotency-Key`

Standard IETF „The Idempotency-Key HTTP Header Field”. Problem: aplikacja wysłała `POST`, serwer zapisał
zgłoszenie, ale odpowiedź zginęła (zasięg) — aplikacja nie wie, czy się udało, więc ponawia. Bez klucza powstałby duplikat.

- **Aplikacja** generuje klucz (UUID) **raz** — przy zapisie niewysłanego zgłoszenia w telefonie — i wysyła go
  przy **każdej** próbie (także po restarcie aplikacji): `Idempotency-Key: "8e03978e-40d5-43e8-bc93-6894a57f9324"`
  (wg standardu napis w cudzysłowie; serwer przyjmuje też wartość bez cudzysłowu, maks. 100 znaków).
- **Serwer** trzyma klucze w tabeli `IdempotencyKeys`: technik + klucz (klucz główny, unikalny), skrót SHA-256 treści
  żądania, wynik (id utworzonego zgłoszenia), data. Pierwsze żądanie zapisuje zgłoszenie i klucz w jednej transakcji
  (`SaveChanges`). Powtórka z tą samą treścią zwraca zapisany wynik (`201`, to samo zgłoszenie, bez drugiego zapisu);
  ta sama wartość klucza z inną treścią → `422`; dwa równoległe żądania z tym samym kluczem → drugie dostaje `409`
  (naruszenie unikalności klucza w bazie) i może ponowić. Brak nagłówka → `400` (ProblemDetails).
- Klucz jest osobny dla każdego technika. **Sprzątanie:** klucze starsze niż 24 h są usuwane przy okazji każdego
  `POST /api/reports` (jedno `DELETE … WHERE CreatedAt < teraz − 24 h`, bez crona); po tym czasie ten sam klucz działa jak nowy.
- Aktualizacja istniejącego zgłoszenia to `PUT /api/reports/{id}` (ten sam stan przy powtórce — PUT jest idempotentny
  z natury), zdjęcie: `PUT /api/reports/{id}/photo` z `id` z odpowiedzi `POST`.

```bash
curl -X POST "$BASE/api/reports" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: "8e03978e-40d5-43e8-bc93-6894a57f9324"' \
  -d '{"title":"Awaria pompy","priority":"High","status":"New","createdAt":"2026-09-26T08:15:00+00:00"}'
```

## Baza danych

PostgreSQL przez EF Core (Npgsql). Tabele: `Technicians`, `Reports` (klucz: e-mail technika + id), `Devices`,
`RefreshTokens`, `IdempotencyKeys` (klucz: e-mail technika + klucz). Zdjęcia leżą na dysku (`Storage:DataDirectory/photos`), w bazie tylko ich nazwa i typ.

Schemat tworzy `Database.EnsureCreated()` przy starcie API — **bez migracji EF**. To najprostsze rozwiązanie dla
danych szkoleniowych: przy zmianie modelu usuwa się tabele (albo całą bazę) i API zakłada je od nowa, puste.
Projekt produkcyjny używałby migracji (`dotnet ef migrations add …`), żeby zmieniać schemat bez utraty danych.

Wyjątek: tabela `IdempotencyKeys` doszła, gdy baza produkcyjna już istniała, a `EnsureCreated` nie zmienia istniejącej
bazy. Dlatego przy starcie, po `EnsureCreated`, API wykonuje jedno `CREATE TABLE IF NOT EXISTS "IdempotencyKeys" …`
(`FieldReportDbContext.CreateIdempotencyKeysTableIfMissing`): w starej bazie zakłada brakującą tabelę, w nowej (gdzie
założył ją już `EnsureCreated`) nic nie robi. Danych nie usuwa — prościej niż wprowadzać migracje EF dla jednej tabeli.

Connection string tylko ze środowiska: `ConnectionStrings__FieldReport` (lokalnie ustawiony w
`Properties/launchSettings.json` na bazę deweloperską, na serwerze w pliku env).

## Uruchomienie lokalne

```bash
# jednorazowo: PostgreSQL i baza deweloperska
brew install postgresql@17 && brew services start postgresql@17
psql -d postgres -c "CREATE ROLE fieldreport LOGIN PASSWORD 'fieldreport' CREATEDB;"
psql -d postgres -c "CREATE DATABASE fieldreport OWNER fieldreport;"

dotnet run --project src/FieldReport.Api                                   # http://localhost:5080, Admin key: dev-admin-key
dotnet run --project src/FieldReport.Api --launch-profile siec-sali        # 0.0.0.0:5080 — telefony w sieci sali
dotnet run --project src/FieldReport.Api --launch-profile demo-odswiezania # access token 60 s (demo 401 → refresh)

# push lokalnie: klucz konta serwisowego POZA repo
export GOOGLE_APPLICATION_CREDENTIALS=~/sciezka/poza/repo/firebase-sa.json
```

Konfiguracja (`appsettings.json`, nadpisywana zmiennymi `Sekcja__Klucz`): `PathBase`, `Hosting:Port`,
`RedirectOutsidePathBase`, `Hosting:ListenOnAllInterfaces`, `Auth:*` (PIN, czasy życia tokenów), `Storage:*` (katalog danych, limity),
`Firebase:CredentialsPath` / `GOOGLE_APPLICATION_CREDENTIALS`, `Admin:Key`, `Dispatcher:Key`, `RateLimiting:*`.
Bez poświadczeń Firebase API działa normalnie, a dyspozytor zwraca `push.skipped = true`.

## Testy

```bash
dotnet test                                   # NUnit + WebApplicationFactory na lokalnym PostgreSQL (każda klasa testów: własna baza)
                                              # inny serwer bazy: FIELDREPORT_TEST_DB="Host=…;Username=…;Password=…"
scripts/smoke.sh http://localhost:5080        # smoke przez curl na działającym API
ADMIN_KEY=... scripts/smoke.sh https://mobilny.dev/maui/podstawowe   # + sprzątanie po sobie
```

## Wdrożenie

Hosting: **MyDevil** (FreeBSD, konto współdzielone z blogiem — skrypty dotykają tylko `~/apps/fieldreport-api`,
stron `mobilny.dev` i `szkolenia.strefakodera.pl`, bazy `p1028_fieldrepor`, portu z opisem `fieldreport-api` i własnych linii crona).
Artefakt: `dotnet publish -c Release` **framework-dependent, bez RID** (host ma `/usr/local/bin/dotnet` z runtime 9).
Za HTTPS odpowiada MyDevil (strona `mobilny.dev` typu `proxy` → `localhost:<port>`, certyfikat Let's Encrypt z
automatycznym odnawianiem po stronie MyDevil, `sslonly on`). DNS w Cloudflare: rekordy A **DNS only** (bez proxy CF).
Proxy MyDevil przekazuje całą domenę, więc 301 dla ścieżek spoza `/maui/podstawowe` robi samo API
(`RedirectOutsidePathBase`). Stary adres `szkolenia.strefakodera.pl` to strona php z jednym `.htaccess`
(`redirect-old-domain.sh`). Baza: PostgreSQL MyDevil `p1028_fieldrepor` na `pgsql88.mydevil.net`.

Układ na hoście:

```
~/apps/fieldreport-api/
  app/                   artefakt (JEDYNY katalog czyszczony przez deploy: rsync --delete)
  bin/run.sh             sh run.sh start|stop|restart|status|health|watchdog (+ setup-mydevil.sh, env.template)
                         (zawsze przez `sh`: MyDevil nie wykonuje plików z $HOME bez `devil binexec on`)
  fieldreport-api.env    konfiguracja, chmod 600 (port, PathBase, ConnectionStrings__FieldReport, Admin__Key, …)
  data/                  zdjęcia (photos/), klucz podpisu JWT
  secrets/firebase-sa.json   klucz konta serwisowego (wgrywany ręcznie)
  logs/api.log           log (rotacja 10 MB × 5)
  run/api.pid
cron: @reboot sh run.sh start  |  */5 * * * * sh run.sh watchdog  (start gdy nie działa, restart gdy 2× brak health)
```

### Pierwsza konfiguracja (raz)

```bash
ssh mydevil mkdir -p apps/fieldreport-api/bin
rsync -a deploy/mydevil/ mydevil:apps/fieldreport-api/bin/
ssh mydevil devil pgsql db add fieldreport          # pyta o hasło → wpisz je w ConnectionStrings__FieldReport w env
ssh mydevil sh apps/fieldreport-api/bin/setup-mydevil.sh
# po przełączeniu DNS mobilny.dev na 185.36.169.6: ponowny setup wyda certyfikat, potem stary adres → 301
ssh mydevil sh apps/fieldreport-api/bin/redirect-old-domain.sh
```

### Wdrożenie z lokalnej maszyny

```bash
dotnet test
dotnet publish src/FieldReport.Api -c Release -o artifacts/publish
MYDEVIL_HOST=mydevil bash deploy/mydevil/deploy.sh      # alias z ~/.ssh/config
scripts/smoke.sh https://mobilny.dev/maui/podstawowe
```

### Wdrożenie przez GitHub Actions

`Actions → deploy → Run workflow` (tylko z `main`): testy → publish → `deploy/mydevil/deploy.sh` → smoke publiczny.
Sekrety repozytorium: `MYDEVIL_HOST`, `MYDEVIL_USER`, `MYDEVIL_SSH_PORT`, `MYDEVIL_SSH_KEY` (klucz prywatny,
którego publiczna część jest w `~/.ssh/authorized_keys` na MyDevil), opcjonalnie `FIELDREPORT_ADMIN_KEY`
(smoke sprząta po sobie). CI (`ci.yml`) buduje i testuje każdy push do `main` i PR.

### Push (Firebase) na serwerze

```bash
scp firebase-sa.json mydevil:apps/fieldreport-api/secrets/firebase-sa.json
ssh mydevil 'chmod 600 apps/fieldreport-api/secrets/firebase-sa.json && sh apps/fieldreport-api/bin/run.sh restart'
curl -s https://mobilny.dev/maui/podstawowe/api/health   # firebaseConfigured: true
```

### Operacje

```bash
ssh mydevil sh apps/fieldreport-api/bin/run.sh status        # restart | stop | start | health
ssh mydevil tail -n 100 apps/fieldreport-api/logs/api.log
ssh mydevil grep ^Admin__Key apps/fieldreport-api/fieldreport-api.env
curl -X POST -H "X-Admin-Key: <klucz>" https://mobilny.dev/maui/podstawowe/api/admin/reset
```
