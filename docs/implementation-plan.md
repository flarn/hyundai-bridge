# Implementation and verification plan

## Repository findings

The original Home Automation workspace contains scripts/configuration and frontend work, but no established .NET service or HA custom integration to extend. Preserve its existing changes. This dedicated repository uses one .NET executable, one test project and, in phase 5, a `custom_components/hyundai_bridge` directory. No domain/infrastructure project split, mediator or generic framework.

## Boundary

`Hyundai EU → Hyundai adapter → normalized domain → MQTT v1 → native HA entities`

Only the adapter understands CCI/GSPA, tokens, stamps, PINs and backend DTOs. HA receives vehicle metadata, capabilities, nullable normalized state, timestamps, connectivity and correlated command results. Hyundai credentials never enter HA. MQTT Discovery is excluded.

Planned MQTT topics are `hyundai/v1/{vehicleId}/state`, `/availability`, `/command/{action}` and `/command-result`, plus bridge availability/retained vehicle metadata for config flow. Commands are non-retained and carry a command id; serialize per vehicle, reject malformed or retained controls and deduplicate QoS redelivery. Command results materially improve correctness because Hyundai acceptance is asynchronous. Retained state must be republished after MQTT reconnect; use an offline Last Will.

The exact metadata/capability/availability schemas are finalized and contract-tested in phase 4 before HA implementation. Unsupported fields stay null. Do not embed raw Hyundai field names, endpoint names or errors. Version-breaking changes require a new topic version.

## Phases

| Phase | Deliverable | Acceptance and current status |
|---|---|---|
| 1 | Current EU API note and plan | Source inspected at pinned commits; documented unknowns. Done. |
| 2 | CCI password login, session persistence/refresh, normalized discovery | Live EU login, restart/session reuse and token renewal verified. Discovery returns zero vehicles; linked VIN/model acceptance remains pending. |
| 3 | GSPA cryptography and cached normalized state | Read real data, verify units/null/sentinel mapping and vehicle/bridge timestamps. Not started. |
| 4 | MQTT state/metadata/availability transport | Real broker confirms versioned retained JSON, reconnect and Last Will. Not started. |
| 5 | Config Flow and native HA platforms | Reuse HA MQTT connection; stable VIN-based device/entity ids. One device per vehicle via bridge. Test against HA core and inspect standard Tile cards. Not started. |
| 6 | Refresh, lock, unlock, climate start/stop, charge start/stop, limits | Implement and verify each operation against the actual API before the next. Separate acceptance, completion and refreshed observation. Not started. |
| 7 | Polling, cooldowns, outage cache, Docker, logging, final tests | Restart/token rotation/broker outage/API outage/rate-limit tests and real container verification. Not started. |

Phase 2 deliberately provides a read-only `--discover` acceptance command. It does not create placeholder command handlers or pretend that an HA device exists. CCI tokens are acquired/refreshed now; the CCS exchange and GSPA user-id/stamp logic belong to phase 3, where they are first needed.

Initial HA scope: battery/range/odometer sensors, charging/plugged-in binary sensors, lock, climate, AC/DC numbers, refresh/charge buttons and GPS tracker, only when backed by verified data/capabilities. ICE/PHEV/EV vehicles must not all receive EV controls. Additional temperature/door sensors only with reliable measurements. Climate on/off and target must represent observed remote state; leave HVAC action unknown unless backed by data. Defrost maps through a supported native feature only after API verification.

Bridge connectivity, API reachability and vehicle timestamp freshness remain distinct. An API failure preserves the last observation. A ten-minute poll is not evidence of a current vehicle observation; no arbitrary freshness threshold should hide valid stored data.

## Next required evidence

Make a linked Hyundai vehicle available to the account. If it is already visible in the official MyHyundai app, investigate the discovery response before assuming it is unlinked. Re-run `--discover` and verify VIN/model before proceeding to phase 3. Credentials are supplied per process; session tokens are stored outside the checkout. Never commit raw tokens or VIN/location fixtures from a real account without sanitizing them.

## Local verification — 2026-10-06

Release test suite: 34 passing cases covering protocol-level RSA password encryption, token expiry/rotation, restart reuse, serialized refresh, bounded 401 handling, no password retries on outage/403/429, rate-limit metadata, changed authentication schemas, normalized multi-model discovery, private file modes and atomic/cancelled session writes. The additional regression case follows the observed Hyundai identity redirect. CLI configuration failure was checked separately: exit 2, no vehicle JSON on stdout and structured JSON error on stderr.

These are synthetic transport tests and local filesystem/CLI checks. No Hyundai vehicle, MQTT broker, HA runtime or Docker deployment has been verified.

## Live verification — 2026-10-06

The first authorize request redirected to the Hyundai web identity host on port 8080. The original redirect allowlist incorrectly rejected that host before submitting credentials; it was corrected narrowly and covered by a regression test.

After correction, the supplied EU account authenticated successfully. Discovery returned a valid empty vehicle list (exit 4). A second process reused the saved session and returned the same empty list without authenticating again. A controlled renewal test marked the local session cache expired; the real CCI refresh endpoint returned renewed credentials, which the bridge persisted before discovery. Discovery still returned zero vehicles. Session directory/file modes were verified as 0700/0600; no password, account identifier or token is recorded in this evidence.

Authentication/session lifecycle is now live verified. The phase 2 linked-vehicle acceptance gate remains pending. Token renewal was exercised deliberately, not after natural token expiry, and no vehicle was woken or remotely controlled.
