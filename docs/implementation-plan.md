# Implementation and verification plan

## Repository findings

The original Home Automation workspace contains scripts/configuration and frontend work, but no established .NET service or HA custom integration to extend. Preserve its existing changes. This dedicated repository uses one .NET executable, one test project and, in phase 5, a `custom_components/hyundai_bridge` directory. No domain/infrastructure project split, mediator or generic framework.

## Boundary

`Hyundai EU → Hyundai adapter → normalized domain → MQTT v1 → native HA entities`

Only the adapter understands CCI/GSPA, tokens, stamps, PINs and backend DTOs. HA receives vehicle metadata, capabilities, nullable normalized state, timestamps, connectivity and correlated command results. Hyundai credentials never enter HA. MQTT Discovery is excluded.

MQTT topics are `hyundai/v1/{vehicleId}/state`, `/availability`, `/command/{action}` and `/command-result`, plus bridge availability/retained vehicle metadata for config flow. Commands are non-retained and carry a command id; serialize per vehicle, reject malformed or retained controls and deduplicate QoS redelivery. Command results materially improve correctness because Hyundai acceptance is asynchronous. Retained state must be republished after MQTT reconnect; use an offline Last Will.

The metadata/capability/availability schemas are now documented in [MQTT v1](mqtt-v1.md) and tested by the HA consumer. Unsupported fields stay null. Do not embed raw Hyundai field names, endpoint names or errors. Version-breaking changes require a new topic version.

On 2026-10-06 the user confirmed the vehicle is not linked yet and explicitly requested continuing from the Home Assistant side. Phase 5 therefore precedes real state/command implementation. Synthetic contract fixtures verify the HA boundary; they do not replace the vehicle acceptance checks in phases 3/6.

## Phases

| Phase | Deliverable | Acceptance and current status |
|---|---|---|
| 1 | Current EU API note and plan | Source inspected at pinned commits; documented unknowns. Done. |
| 2 | CCI password login, session persistence/refresh, normalized discovery | Live EU login, restart/session reuse and token renewal verified. Discovery returns zero vehicles; linked VIN/model acceptance remains pending. |
| 3 | GSPA cryptography and cached normalized state | Read real data, verify units/null/sentinel mapping and vehicle/bridge timestamps. Not started. |
| 4 | MQTT state/metadata/availability transport | v1 schema/fixtures and real Mosquitto HA-consumer bootstrap/reload verified. .NET publisher/Last Will, reconnect and real broker verification implemented. Actual Hyundai state source remains pending. |
| 5 | Config Flow and native HA platforms | Implemented and tested in HA 2026.9.4: custom manifest discovery, native entities, VIN identity, per-vehicle capabilities, availability and services. No production deployment or visual Tile-card inspection. |
| 6 | Refresh, lock, unlock, climate start/stop, charge start/stop, limits | Implement and verify each operation against the actual API before the next. Separate acceptance, completion and refreshed observation. Command transport implemented; actual Hyundai operations not started. |
| 7 | Polling, cooldowns, outage cache, Docker, logging, final tests | Polling/backoff, memory cache, command journal/cooldown, Docker and outage/restart tests implemented. Final live vehicle verification remains pending. |

Phase 2 deliberately provides a read-only `--discover` acceptance command. It does not create placeholder command handlers or pretend that an HA device exists. CCI tokens are acquired/refreshed now; the CCS exchange and GSPA user-id/stamp logic belong to phase 3, where they are first needed.

Initial HA scope: battery/range/odometer sensors, charging/plugged-in binary sensors, lock, climate, AC/DC numbers, refresh/charge buttons and GPS tracker, only when backed by verified data/capabilities. ICE/PHEV/EV vehicles must not all receive EV controls. Additional temperature/door sensors only with reliable measurements. Climate on/off and target must represent observed remote state; leave HVAC action unknown unless backed by data. Defrost maps through a supported native feature only after API verification.

Bridge connectivity, API reachability and vehicle timestamp freshness remain distinct. An API failure preserves the last observation. A ten-minute poll is not evidence of a current vehicle observation; no arbitrary freshness threshold should hide valid stored data.

## Next required evidence

Once a vehicle is linked, re-run `--discover` and verify VIN/model before implementing phase 3. Use the implemented .NET producer against the established contract; advertise only capabilities backed by actual adapter implementation and evidence. Credentials are supplied per process; session tokens are stored outside the checkout. Never commit raw tokens or VIN/location fixtures from a real account without sanitizing them.

## Local verification — 2026-10-06

Release test suite: 34 passing cases covering protocol-level RSA password encryption, token expiry/rotation, restart reuse, serialized refresh, bounded 401 handling, no password retries on outage/403/429, rate-limit metadata, changed authentication schemas, normalized multi-model discovery, private file modes and atomic/cancelled session writes. The additional regression case follows the observed Hyundai identity redirect. CLI configuration failure was checked separately: exit 2, no vehicle JSON on stdout and structured JSON error on stderr.

These phase 2 checks are synthetic transport tests and local filesystem/CLI checks. They did not verify a Hyundai vehicle, MQTT broker, HA runtime or Docker deployment. The subsequent HA verification is recorded below.

## Live verification — 2026-10-06

The first authorize request redirected to the Hyundai web identity host on port 8080. The original redirect allowlist incorrectly rejected that host before submitting credentials; it was corrected narrowly and covered by a regression test.

After correction, the supplied EU account authenticated successfully. Discovery returned a valid empty vehicle list (exit 4). A second process reused the saved session and returned the same empty list without authenticating again. A controlled renewal test marked the local session cache expired; the real CCI refresh endpoint returned renewed credentials, which the bridge persisted before discovery. Discovery still returned zero vehicles. Session directory/file modes were verified as 0700/0600; no password, account identifier or token is recorded in this evidence.

Authentication/session lifecycle is now live verified. The phase 2 linked-vehicle acceptance gate remains pending. Token renewal was exercised deliberately, not after natural token expiry, and no vehicle was woken or remotely controlled.

## HA consumer verification — 2026-10-06

The HA 2026.9.4 test runtime loads all seven native platforms. **50 cases pass**, including the real broker test. Tests cover 15 entities for a synthetic EV, one vehicle device via the bridge, another model with limited capabilities, registry customization after reload, nullable/invalid observations, API outage versus bridge/broker loss, Config Flow and native service commands. Accepted responses remain pending, terminal errors propagate, timeouts have unknown outcomes and state changes only with new observations. Ruff lint/format and JSON/diff checks also pass.

A separate test uses actual paho transport and Mosquitto 2.0.22 in a disposable Docker container bound to loopback. It publishes synthetic retained state before HA startup, verifies custom integration discovery from the manifest and native state, reloads the integration/MQTT connection and checks offline/online transitions. This verifies the consumer and transport, not Hyundai API behavior or a deployed HA system. See [HA instructions](home-assistant.md).

## .NET transport and operation verification — 2026-10-07

The daemon now publishes v1 manifests, normalized retained state and availability, with MQTT 5 retained-control rejection, an offline Last Will and cache replay after reconnect. Command transport validates capabilities/arguments, correlates UUIDs, persists execution markers/results and forced-refresh cooldown, serializes backend operations and reports unknown outcomes on timeout/restart. No control is automatically retried; observations are published before releasing the backend gate for the next poll. Production discovery advertises empty state/control capabilities, so unsupported Hyundai operations are not exposed.

**60 .NET cases pass** against disposable Mosquitto, with the HA-only companion test skipped in that standalone run. **51 HA cases pass**, including the real `.NET → Mosquitto → HA → native unlock → .NET` pipeline, which starts that companion and verifies accepted/pending, completed and newly observed state. After the final observation-ordering change, the .NET suite and affected HA pipeline passed again. Existing authentication/session coverage remains green. Ruff, JSON documents, CLI configuration errors, Compose validation and diff checks pass.

The final Docker image builds and runs as UID 1654 with a read-only root filesystem and owner-only durable storage. A second process using the same volume is rejected. Dummy-credential testing on an internal network without Internet access confirms structured API failure/backoff while MQTT remains available, broker restart/cache replay, abrupt offline Last Will and graceful stop/start. Temporary test containers/network/volume are removed after verification. No production HA deployment, real vehicle status, forced refresh or physical remote command was performed.

## .NET 11 RC baseline — 2026-10-07

At the user's request, both .NET projects now target `net11.0`. `global.json` pins SDK `11.0.100-rc.1.26425.128`, enables prereleases and disables SDK roll-forward. Hosting/logging packages use matching `11.0.0-rc.1.26425.128` versions. Docker uses explicit `sdk:11.0.100-rc.1` and `runtime:11.0.0-rc.1` tags.

Restore, Release compilation and **60 .NET tests** pass under .NET 11 RC1. The affected real HA/.NET/Mosquitto pipeline passes separately, including its .NET companion. The Docker image builds, reports runtime `11.0.0-rc.1.26425.128`, starts the daemon, connects to MQTT, handles the simulated API outage and stops gracefully. Temporary test resources were removed. No Hyundai vehicle requests or production deployment were performed.
