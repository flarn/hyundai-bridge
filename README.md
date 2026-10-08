# Hyundai Bridge

Hyundai EU / MyHyundai → .NET → versioned MQTT → native Home Assistant custom integration.

Hyundai integration for supported European vehicles generally. Model names, VINs and capabilities come from the backend; IONIQ 9 is one intended verification vehicle.

**Status:** the .NET service authenticates against MyHyundai EU, discovers a linked vehicle and publishes real cached vehicle state every ten minutes without waking the vehicle. Its Docker deployment and retained MQTT output have been verified. The Home Assistant custom integration exposes native entities and is tested against synthetic contract data. Production HA installation and Hyundai remote controls remain separate, unverified steps.

See [API research](docs/myhyundai-eu-api.md) and [implementation/verification plan](docs/implementation-plan.md).

## Home Assistant

Install through HACS using this public GitHub repository:

1. Open **HACS → ⋮ → Custom repositories**.
2. Add `https://github.com/flarn/hyundai-bridge`, type **Integration**.
3. Find **Hyundai Bridge** in HACS and download it, then restart Home Assistant.
4. Ensure HA's existing MQTT integration connects to the same broker as the .NET bridge.
5. In **Settings → Devices & services**, configure the discovered **Hyundai Bridge**. Alternatively, add the integration manually with bridge identifier `home`.

HACS installs only `custom_components/hyundai_bridge`; the .NET bridge remains a separate Docker service. HACS tracks the default branch until GitHub releases are published. No Hyundai credentials belong in HA.

For manual installation, copy `custom_components/hyundai_bridge` into your HA configuration's `custom_components` directory and restart. Each vehicle gets one Hyundai device with native entities according to its advertised capabilities. No MQTT entities or MQTT entity discovery payloads are used.

See [installation and tests](docs/home-assistant.md) and the [MQTT v1 contract](docs/mqtt-v1.md). The integration was tested with HA 2026.9.4. Current real state includes battery, 12-V battery, range, odometer, charging, plugged-in, eight opening states, charging power/time, vehicle observation time, tire pressures/warning, remote climate state, climate target/fan activity/level, battery temperatures/energy and side-window openings. Requested remote climate/defrost, charging and explicit refresh controls are implemented behind normalized capabilities; live vehicle verification is tracked separately.

```sh
python3.14 -m venv .venv
.venv/bin/pip install -r requirements-test.txt
.venv/bin/pytest -q
```

The optional Mosquitto smoke test uses a disposable loopback broker; instructions are in the HA document. Synthetic fixtures are not verified capabilities or observations of a real Hyundai.

## Bridge service

See [service configuration, Docker and verification](docs/bridge-service.md). The daemon publishes retained metadata/state/availability, reconnects with an offline Last Will, preserves observations through API outages and handles correlated commands with persisted deduplication/cooldown. Actual Hyundai state/control capabilities are enabled only after implementation and vehicle verification.

The bridge also serves a read-only connection dashboard with Hyundai request statistics,
session metadata, polling/backoff and MQTT status. The example Compose exposes it at
`http://127.0.0.1:8076`; see [status page configuration](docs/bridge-service.md#connection-status-page)
for LAN access. Viewing it does not make additional Hyundai requests.

```sh
docker compose build
docker compose up -d
```

## Run discovery

Requires .NET 11 RC1 (SDK `11.0.100-rc.1.26425.128`, pinned in `global.json`) and a European MyHyundai account with a linked vehicle. Set `HYUNDAI_USERNAME`, `HYUNDAI_PASSWORD`, `HYUNDAI_REGION=EU` and `HYUNDAI_SESSION_DIRECTORY` using your local secret manager or process environment. Do not put credentials in command arguments or Git. `.env.example` documents the variables; no automatic `.env` loading is performed.

```sh
dotnet run --project src/HyundaiBridge -- --discover
dotnet test HyundaiBridge.slnx
```

Discovery prints normalized vehicle metadata as JSON to stdout, including VIN where provided. Structured operational logs go to stderr. Exit codes: 0 = vehicles returned, 2 = configuration error, 3 = API/network/session error, 4 = no linked vehicles, 130 = cancellation. Discovery does not wake a vehicle or issue remote controls.

Successful CCI token sets are reused and refreshed with a two-minute expiry margin. A rejected refresh permits one full login; transport failures, rate limits and changed response schemas do not trigger repeated password login. A discovery 401 permits one refresh and one retry; a 403 is reported without a login loop.

Session tokens are confidential. On macOS/Linux, the dedicated session directory is restricted to its owner (0700) and its atomically replaced token file is 0600. Passwords are never persisted. Tokens are plaintext on disk: use an owner-only directory on an encrypted disk/volume, exclude it from shared backups and protect any future Docker volume equally. File permissions are not encryption. This phase supports macOS/Linux, not Windows secret storage. Run one process per session directory.

Accept any pending account terms through the official MyHyundai app/browser before retrying. An authentication failure is not automatically evidence of a bad password: consent, WAF blocking or a changed Hyundai flow can also be responsible.

Remote controls: configure `HYUNDAI_PIN` in the bridge for climate start/stop with
defrost and EV/PHEV charging start/stop. Explicit vehicle refresh is available
without PIN, subject to a ten-minute cooldown. Commands use the existing native
HA climate/buttons and require observed completion; live verification is recorded
separately from tests in the deployment notes.
