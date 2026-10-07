# Hyundai Bridge

Hyundai EU / MyHyundai → .NET → versioned MQTT → native Home Assistant custom integration.

Hyundai integration for supported European vehicles generally. Model names, VINs and capabilities come from the backend; IONIQ 9 is one intended verification vehicle.

**Status:** EU authentication/discovery and the Home Assistant custom integration are implemented. Live login, session reuse and CCI token renewal were verified on 2026-10-06. The account has no linked vehicle yet. At the user's request, development continued from the HA side: native entities, Config Flow, versioned state consumption and correlated commands are tested against synthetic contract data. The .NET MQTT producer, command transport, outage cache, polling/backoff and Docker service are now implemented. Real vehicle state and physical commands remain pending; this is not yet a working end-to-end car connection.

See [API research](docs/myhyundai-eu-api.md) and [implementation/verification plan](docs/implementation-plan.md).

## Home Assistant

Install `custom_components/hyundai_bridge` into your HA configuration's `custom_components` directory and restart. With the existing HA MQTT integration configured, a v1 bridge manifest discovers **Hyundai Bridge** through Config Flow. Each vehicle gets one Hyundai device with native sensors, binary sensors, lock, climate, numbers, buttons and tracker according to its advertised capabilities. No MQTT entities or MQTT entity discovery payloads are used.

See [installation and tests](docs/home-assistant.md) and the [MQTT v1 contract](docs/mqtt-v1.md). The integration was tested with HA 2026.9.4. It does not require Hyundai credentials. The .NET producer advertises only implemented capabilities: discovery currently has no state/control features, so installation alone cannot supply live car data yet.

```sh
python3.14 -m venv .venv
.venv/bin/pip install -r requirements-test.txt
.venv/bin/pytest -q
```

The optional Mosquitto smoke test uses a disposable loopback broker; instructions are in the HA document. Synthetic fixtures are not verified capabilities or observations of a real Hyundai.

## Bridge service

See [service configuration, Docker and verification](docs/bridge-service.md). The daemon publishes retained metadata/state/availability, reconnects with an offline Last Will, preserves observations through API outages and handles correlated commands with persisted deduplication/cooldown. Actual Hyundai state/control capabilities are enabled only after implementation and vehicle verification.

```sh
docker compose build
docker compose up -d
```

## Run discovery

Requires .NET 10 and a European MyHyundai account with a linked vehicle. Set `HYUNDAI_USERNAME`, `HYUNDAI_PASSWORD`, `HYUNDAI_REGION=EU` and `HYUNDAI_SESSION_DIRECTORY` using your local secret manager or process environment. Do not put credentials in command arguments or Git. `.env.example` documents the variables; no automatic `.env` loading is performed.

```sh
dotnet run --project src/HyundaiBridge -- --discover
dotnet test HyundaiBridge.slnx
```

Discovery prints normalized vehicle metadata as JSON to stdout, including VIN where provided. Structured operational logs go to stderr. Exit codes: 0 = vehicles returned, 2 = configuration error, 3 = API/network/session error, 4 = no linked vehicles, 130 = cancellation. Discovery does not wake a vehicle or issue remote controls.

Successful CCI token sets are reused and refreshed with a two-minute expiry margin. A rejected refresh permits one full login; transport failures, rate limits and changed response schemas do not trigger repeated password login. A discovery 401 permits one refresh and one retry; a 403 is reported without a login loop.

Session tokens are confidential. On macOS/Linux, the dedicated session directory is restricted to its owner (0700) and its atomically replaced token file is 0600. Passwords are never persisted. Tokens are plaintext on disk: use an owner-only directory on an encrypted disk/volume, exclude it from shared backups and protect any future Docker volume equally. File permissions are not encryption. This phase supports macOS/Linux, not Windows secret storage. Run one process per session directory.

Accept any pending account terms through the official MyHyundai app/browser before retrying. An authentication failure is not automatically evidence of a bad password: consent, WAF blocking or a changed Hyundai flow can also be responsible.
