# Hyundai Bridge

Hyundai EU / MyHyundai → .NET → versioned MQTT → native Home Assistant custom integration.

Hyundai integration for supported European vehicles generally. Model names, VINs and capabilities come from the backend; IONIQ 9 is one intended verification vehicle.

**Status:** research and phase 2 authentication/discovery implementation. Live EU login, session reuse after restart and CCI token renewal were verified on 2026-10-06. Discovery returned zero available vehicles, so VIN/model acceptance remains pending. State retrieval, MQTT, Home Assistant entities, commands and Docker packaging are subsequent phases, not delivered features.

See [API research](docs/myhyundai-eu-api.md) and [implementation/verification plan](docs/implementation-plan.md).

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
