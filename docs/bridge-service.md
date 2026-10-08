# .NET bridge service

The service owns Hyundai credentials and publishes the [MQTT v1 contract](mqtt-v1.md). HA uses its existing MQTT connection and discovers the bridge's retained manifest. No MQTT entities or MQTT Discovery payloads are produced.

## Current adapter boundary

The production Hyundai adapter supports EU authentication, discovery and ten-minute
cached state reads. This release adds explicit forced refresh and PIN-authenticated
climate start/stop (including defrost) and charging start/stop for supported CCS2
vehicles. Lock/unlock and charge-limit writes remain disabled. Hyundai-specific
commands and PIN handling stay inside the adapter; HA uses the existing normalized
v1 command contract and native climate/button entities.

Command code is tested with scripted HTTP responses and native HA service calls.
Actual vehicle command acceptance/results still require live verification; do not
confuse those tests or capability advertisement with physical acceptance.

## Configuration and running

Requires .NET 11 RC1 (SDK `11.0.100-rc.1.26425.128`, pinned in `global.json`) on Linux/macOS, or Docker. The application reads process environment variables; it does not load `.env` itself.

| Variable | Required / default |
|---|---|
| `HYUNDAI_USERNAME`, `HYUNDAI_PASSWORD` | EU MyHyundai credentials |
| `HYUNDAI_PIN` | Optional MyHyundai remote-control PIN; without it, only explicit refresh is enabled |
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

* Retrieve the vehicle list at startup only, with at most five attempts in total and the existing 10/20/40/60-minute failure backoff (or a longer `Retry-After`). A successful empty garage also stops discovery until restart.
* After discovery, retrieve each vehicle's cached status immediately, then every **10 minutes** after a successful read. Never call a wake/forced-refresh endpoint automatically. Each vehicle has its own failure backoff of 10/20/40/60 minutes; another vehicle's failure does not discard successful state. An HTTP 429 postpones all vehicles for the account, honoring a longer `Retry-After`. MQTT continues running independently. The dashboard distinguishes the next discovery attempt from the next vehicle-status read.
* Keep the last normalized observation in memory across API failures and MQTT reconnects. Preserve its `vehicleUpdatedAt` and `bridgeUpdatedAt`; reconnect is not a new observation. API failure changes per-vehicle reachability, not vehicle values. Freshness stays `unknown` until the adapter provides evidence; no age threshold invents `current`.
* Retain manifest, state and availability. Use a retained `offline` Last Will and publish `online` after MQTT subscriptions/cache replay. Reconnect delays are 2/4/8/16/32/60 seconds; each connection attempt has a 10-second timeout. A broker restart replays the cache. After a bridge restart, retained broker state can bootstrap HA; fresh in-memory state requires a successful adapter read. No separate persistent location cache is created.
* Controls are non-retained, UUID-correlated JSON. Reject malformed/oversized payloads, unknown actions and unsupported values. MQTT 5 `Retain As Published` identifies and rejects live retained controls as well as retained replay. The bounded ingress queue holds 64 commands; controls dropped on overload/disconnection have an unknown outcome to HA, without automatic resend.
* Serialize backend operations, including polling, through one gate. The **90-second** command budget includes queue and gate waiting. Expired queued controls do not execute; queued controls from a lost MQTT connection are discarded. No physical control is automatically retried.
* Save an execution marker atomically before invoking the adapter. UUID redelivery replays the saved result; reusing an ID for another request fails. Persist the latest 2048 terminal command IDs and all in-progress markers. Clients must always generate fresh IDs; replay outside that bounded history is not guaranteed to deduplicate.
* Persist a **10-minute per-vehicle forced-refresh cooldown**, including failed attempts, before dispatch. Refresh sends one prewakeup request, then waits for an advanced vehicle timestamp; unchanged cached data never completes refresh.
* Publish `accepted` only when the adapter reports actual upstream acceptance. Publish `completed` only after the adapter confirms completion. Update state only with an actual normalized observation. Timeout/restart during execution reports `failed` with an explicit unknown outcome. A non-cooperative timed-out operation retains the backend gate until it ends, preventing overlapping requests.

* The adapter polls a submitted command result at five-second intervals, at most twelve times within the existing 90-second total budget. PIN control tokens are reused until expiry in memory. A rejected PIN blocks further PIN submissions until configuration is checked and the bridge restarted; PIN is never automatically retried. HTTP 429 pauses account requests for at least ten minutes or a longer `Retry-After`.
* Climate uses a conservative EU range of 17–27 °C in 0.5 °C steps. These upstream-derived limits still require confirmation for each vehicle. Defrost is the native climate `defrost` preset; changing it sends a climate-start request. Neither target temperature nor fan activity is invented as measured cabin temperature or heating/cooling action.

Broker ACLs should restrict bridge manifests/state publication and command publication to the appropriate bridge/HA clients. Vehicle topic IDs must be globally unique on a broker, as specified by the contract.

## Connection status page

The daemon includes a read-only status page at `/` and diagnostic JSON at `/api/status`.
It shows the result/time of the last Hyundai poll, next scheduled poll (including backoff),
vehicle count, MQTT connectivity, session source/expiry and login/renewal counts.
HTTP statistics include request count, mean duration, HTTP/network/timeout failures,
429 responses and the latest 20 requests with operation, timestamp, status and duration.
Redirects are recorded as HTTP responses; application/schema failures appear as poll
failures even when their HTTP response was 200. These are observations, not a live
connection guarantee or Hyundai's remaining API quota.

Statistics are in memory and reset on restart. Browser updates read only local statistics;
they neither poll Hyundai nor wake vehicles. No credentials, tokens, account identifiers,
VINs, coordinates, request URLs or raw exception text are served. The latest successful
vehicle-status response is displayed as formatted JSON, with identifiers, credentials
and location masked before diagnostic storage. This response stays outside the MQTT contract.
The page has no commands or configuration writes.

Outside Docker, HTTP defaults to `127.0.0.1:8080`; `ASPNETCORE_URLS` can select the listener.
The image listens on container port 8080. The example Compose publishes only
`127.0.0.1:8076`; change that host address to the Docker host's LAN IP to access it from
your home network. The page has no login; keep it on the trusted home network and do
not expose it to the Internet. The MQTT contract and HA integration are unchanged.

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

The status dashboard includes the latest successful cached vehicle-status response as formatted JSON. Identifiers, tokens/password fields and location are masked before entering diagnostic storage. This diagnostic response is not part of the normalized MQTT/HA contract. Viewing it does not contact Hyundai.
