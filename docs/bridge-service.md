# .NET bridge service

The service owns Hyundai credentials and publishes the [MQTT v1 contract](mqtt-v1.md). HA uses its existing MQTT connection and discovers the bridge's retained manifest. No MQTT entities or MQTT Discovery payloads are produced.

## Current adapter boundary

The production Hyundai adapter supports EU authentication, session renewal and vehicle discovery. Discovered vehicles advertise **empty state and command capabilities** until their actual data and operations are implemented and verified. An account with no linked vehicles produces an empty manifest; HA can discover the bridge but cannot create vehicle entities yet.

Normalized state publication and command execution are verified using synthetic adapters **inside the test assembly only**. There is no production demo mode or fake command success. GSPA state retrieval, forced vehicle refresh and physical remote commands remain vehicle-dependent work. The service currently polls discovery, not vehicle status.

## Configuration and running

Requires .NET 11 RC1 (SDK `11.0.100-rc.1.26425.128`, pinned in `global.json`) on Linux/macOS, or Docker. The application reads process environment variables; it does not load `.env` itself.

| Variable | Required / default |
|---|---|
| `HYUNDAI_USERNAME`, `HYUNDAI_PASSWORD` | EU MyHyundai credentials |
| `HYUNDAI_REGION` | `EU` only; defaults to `EU` |
| `HYUNDAI_SESSION_DIRECTORY` | Absolute private data directory; `/data` in Docker |
| `BRIDGE_ID` | `home`; ASCII letters, digits, `_` or `-`; unique on the broker |
| `MQTT_HOST` | Existing MQTT broker hostname |
| `MQTT_PORT` | `1883` |
| `MQTT_TLS` | `false`; set `true` with the broker's TLS port |
| `MQTT_USERNAME`, `MQTT_PASSWORD` | Optional broker credentials |

Each credential variable also accepts a `_FILE` alternative, such as `HYUNDAI_PASSWORD_FILE=/run/secrets/hyundai_password`. Configure either the value or its file, not both. Mounted secret files must be readable by container user `app` (UID 1654). TLS uses normal certificate/hostname validation; insecure certificate bypasses are not supported.

```sh
dotnet run --project src/HyundaiBridge -- --serve
```

No arguments also starts the service. `--discover` retains the one-shot read-only discovery command and does not require MQTT configuration. Operational logs are structured JSON on stderr. The service logs operation names and failure types, never credential values, token payloads or exact location. SIGTERM/Ctrl+C stops polling and publishes retained `offline` when the broker is reachable.

### Docker / Compose

For the NUC Docker host and published image, see [NUC deployment](nuc-deployment.md).

The supplied [compose.yaml](../compose.yaml) uses your existing broker, a private named data volume and `restart: unless-stopped`. It runs as `app`, with a read-only root filesystem, temporary `/tmp`, dropped capabilities and no new privileges. No HA add-on or additional production broker is required. Use a reachable LAN/DNS address, or attach the service to the broker's existing Docker network.

```sh
docker compose build
docker compose up -d
```

Supply the required variables through your deployment's secret/environment configuration. Compose can read a local `.env`; keep it outside Git. Environment values are visible to privileged container inspection. To use Docker secrets, mount the files and replace the corresponding credential entries in Compose with `_FILE` paths. The example intentionally does not provision account secrets.

The data volume contains confidential session tokens, the command journal and a process lock. Directory/file modes are 0700/0600. Files are plaintext: protect the volume and its backups. One process owns a data directory; do not run `--discover` against that directory while the daemon is running. A missing command journal is normal until the first supported command is recorded. A corrupt journal stops startup rather than discarding duplicate-execution protection.

## Outage, polling and command behavior

* Poll immediately at startup, then every **10 minutes**. Failures back off to 10/20/40/60 minutes, honoring a longer Hyundai `Retry-After`; successful polling resets the interval. There is no automatic forced vehicle refresh.
* Keep the last normalized observation in memory across API failures and MQTT reconnects. Preserve its `vehicleUpdatedAt` and `bridgeUpdatedAt`; reconnect is not a new observation. API failure changes per-vehicle reachability, not vehicle values. Freshness stays `unknown` until the adapter provides evidence; no age threshold invents `current`.
* Retain manifest, state and availability. Use a retained `offline` Last Will and publish `online` after MQTT subscriptions/cache replay. Reconnect delays are 2/4/8/16/32/60 seconds; each connection attempt has a 10-second timeout. A broker restart replays the cache. After a bridge restart, retained broker state can bootstrap HA; fresh in-memory state requires a successful adapter read. No separate persistent location cache is created.
* Controls are non-retained, UUID-correlated JSON. Reject malformed/oversized payloads, unknown actions and unsupported values. MQTT 5 `Retain As Published` identifies and rejects live retained controls as well as retained replay. The bounded ingress queue holds 64 commands; controls dropped on overload/disconnection have an unknown outcome to HA, without automatic resend.
* Serialize backend operations, including polling, through one gate. The **90-second** command budget includes queue and gate waiting. Expired queued controls do not execute; queued controls from a lost MQTT connection are discarded. No physical control is automatically retried.
* Save an execution marker atomically before invoking the adapter. UUID redelivery replays the saved result; reusing an ID for another request fails. Persist the latest 2048 terminal command IDs and all in-progress markers. Clients must always generate fresh IDs; replay outside that bounded history is not guaranteed to deduplicate.
* Persist a **10-minute per-vehicle forced-refresh cooldown**, including failed attempts, before dispatch. This is ready for the real refresh adapter; it does not make refresh supported today.
* Publish `accepted` only when the adapter reports actual upstream acceptance. Publish `completed` only after the adapter confirms completion. Update state only with an actual normalized observation. Timeout/restart during execution reports `failed` with an explicit unknown outcome. A non-cooperative timed-out operation retains the backend gate until it ends, preventing overlapping requests.

Broker ACLs should restrict bridge manifests/state publication and command publication to the appropriate bridge/HA clients. Vehicle topic IDs must be globally unique on a broker, as specified by the contract.

## Verification

```sh
dotnet test HyundaiBridge.slnx --configuration Release
```

Unit tests cover serialization, command parsing/validation, discovery capabilities, polling/backoff, persisted deduplication/cooldown, unknown restart outcomes, bounded execution, invalid cache updates and API outage preservation. Existing authentication/session tests remain part of the same suite.

For real transport verification, start the disposable loopback Mosquitto from [HA test instructions](home-assistant.md). Then:

```sh
HYUNDAI_TEST_MQTT_PORT=18884 dotnet test HyundaiBridge.slnx --configuration Release
HYUNDAI_TEST_MQTT_PORT=18884 .venv/bin/pytest -q
```

The .NET broker test checks retained documents, retained-control rejection, correlation, deduplication, Last Will and reconnect. The HA pipeline test starts a .NET test adapter and exercises `.NET → Mosquitto → native HA entities → HA unlock service → .NET → accepted/completed → observed state`. Its .NET companion is skipped in standalone runs and started by Python; this is an explicit optional broker dependency, not a disabled production path. Build the Release tests before running Python's pipeline test.

Container verification uses dummy credentials and an internal Docker network without Internet access: API failure/backoff, non-root/private storage, read-only filesystem, broker restart, abrupt Last Will, graceful stop/start and durable volume. These checks do not constitute Hyundai vehicle verification or a production HA deployment.
