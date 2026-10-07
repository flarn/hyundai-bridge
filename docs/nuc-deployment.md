# NUC deployment

Target: Docker host `ha-bridges`, `192.168.1.67`, following the existing stacks under `/opt/ha-bridges`. Suggested stack directory: `/opt/ha-bridges/hyundai-bridge`.

## Initial deployment — 2026-10-07

Source commit: `93a9bdf`. Runtime: .NET 11 RC1.

Image built locally for `linux/amd64` and pushed through the existing Docker registry trust configuration:

```text
registry.local/hyundai-bridge:20261007-net11rc1-93a9bdf
registry.local/hyundai-bridge@sha256:1f4606f9fd4d9ac17194b1063d7ed145aee34e460df1a628047925b8f15ef51b
```

**Deployed through authenticated Arcane on 2026-10-07 at 06:28 UTC.** Project `hyundai-bridge`, directory `/opt/ha-bridges/hyundai-bridge`, container `hyundai-bridge-hyundai-bridge-1`. The production Compose file uses the digest above and omits `build`. It uses the existing MQTT broker; no broker or published port was added. No existing stack was changed or restarted.

Verified on the NUC:

- Exact published image, .NET `11.0.0-rc.1.26425.128`, user/group 1654, read-only root filesystem, `unless-stopped` restart policy.
- `/data` permissions 0700 and `session.json` permissions 0600, both owned by 1654, in the persistent `hyundai-bridge_bridge-data` volume.
- Real Hyundai authentication succeeded; discovery returned zero vehicles, as expected for the currently unlinked account.
- A separate authenticated MQTT subscription received retained `hyundai/v1/bridges/home/availability = online` and the v1 manifest with an empty vehicle list. The bridge ID was unused before installation.
- A controlled restart of only this container published `offline`, then `online`. Subsequent vehicle discovery succeeded without another authentication event, reusing the stored session.
- Container memory was approximately 22–25 MB after startup/restart. All eight containers remained running.

The HA custom integration has not been installed on the production HA instance in this deployment. Vehicle state and commands still require a linked-car acceptance test; deployment does not claim those features are verified.

The project credentials are stored outside Git in Arcane's `.env`. Its host file permissions are not yet verified or tightened to 0600: the Arcane manager image has no `/bin/sh`. A narrowly scoped temporary helper to check/set these permissions is awaiting user approval. The private token-volume permissions above were verified inside the bridge container.

## Connection dashboard — 2026-10-07

Source commit `386e835`, .NET/ASP.NET Core 11 RC1:

```text
registry.local/hyundai-bridge:20261007-connection-ui
registry.local/hyundai-bridge@sha256:eb799186d12a3cdd577a94e8299c7bba28f87b0af9ec5b63bb88dbaafc402c92
```

Deployed through Arcane at 06:57 UTC by changing only this project's image and adding
the HTTP port binding `192.168.1.67:8076:8080`. Credentials, MQTT settings and the
existing data volume were preserved. The container uses the exact new digest, user
`app` and the existing restart policy. All eight containers remained running.

The read-only dashboard is available at **http://192.168.1.67:8076/** on the home
network, as requested. Browser verification showed a real discovery response with
HTTP 200 in approximately 282 ms, zero linked vehicles, connected MQTT, a reused
stored session, zero new logins/renewals, and the next poll ten minutes later.
Repeated page updates left the Hyundai request count at one. Only aggregate
connection/session/request metadata is exposed; no vehicle/account identifiers or
credentials. This does not verify vehicle state or remote commands.

Rollback: restore the initial image digest above and remove the HTTP port binding
in this Arcane project's Compose configuration, then redeploy only this project.
Keep the existing credentials and data volume.

## 60-second discovery polling (superseded) — 2026-10-07

This release mistakenly applied the requested vehicle-status interval to discovery,
waiting 60 seconds after each successful list read. It is superseded by the correction
below. API-failure backoff remained 10/20/40/60 minutes (or a longer `Retry-After`),
and the forced-refresh cooldown remains ten minutes. The adapter still retrieves
the vehicle list; real vehicle status and controls require later implementation
and linked-vehicle verification. Push is deferred.

```text
registry.local/hyundai-bridge:20261007-poll-60s
registry.local/hyundai-bridge@sha256:2c282b8c08eedad0ab57ea76d2d297e24d34059dc286e686d98b80de281c64d8
```

Deployed through Arcane, changing only this project's image digest. The final
container started at 07:22:10 UTC. Port binding, credentials and persistent volume
were preserved. The dashboard showed successful HTTP 200 discovery, connected
MQTT, a reused session with zero new logins/renewals, and a next poll 60 seconds
later. Its interval label now reads `Cache · 1 min normalt`.

Two successful discovery calls were observed at 07:22:10 and 07:23:10 UTC,
followed by a next scheduled read at 07:24:10 UTC. No HTTP/network failures or
429 responses occurred during this check. Arcane showed all eight containers
running and this service using the new image digest. Local .NET validation:
62 tests passed; the two broker-dependent tests were skipped without a test broker.

Rollback: restore the connection-dashboard digest above in this project's Compose
image and redeploy. Preserve the credentials, port binding and data volume.

## Discovery interval correction (superseded) — 2026-10-07

The user clarified that one-minute polling applies to vehicle status, not the
vehicle list. Discovery is now restored to ten minutes, independent of vehicle
count. The scheduler names this explicitly as `DiscoveryInterval`; the dashboard
label reads `Fordonslista · 10 min`. The requested one-minute cached-status cadence
is documented for the pending state adapter. It is not implemented by changing
discovery cadence when a car appears.

```text
registry.local/hyundai-bridge:20261007-discovery-10m
registry.local/hyundai-bridge@sha256:56ca4365859fde20825bc192f628ce0131cfc5cf2af15f41df7edf2a22a296f5
```

Deployed through Arcane by changing only this project's image digest. Successful
discovery was observed at 07:32:44 UTC, with the next read scheduled at 07:42:44 UTC.
MQTT was connected and the stored session reused, with zero HTTP/network errors
or 429 responses. Local .NET tests: 62 passed; the two broker-dependent tests were
skipped without a test broker. Vehicle-state retrieval and controls remain pending.

Rollback: restore the connection-dashboard image (`eb799186…`) and redeploy this
project, preserving credentials, port binding and data volume. That version also
uses ten-minute discovery; the superseded minute-discovery image is not the
recommended rollback.

## Startup-only discovery (superseded retry policy) — 2026-10-07

Vehicle discovery now runs at startup only. After a successful response, including
an empty garage, no further list read is scheduled until the bridge restarts.
Startup failures keep the existing bounded 10/20/40/60-minute backoff and honor a
longer `Retry-After`. MQTT continues running after discovery succeeds.

```text
registry.local/hyundai-bridge:20261007-startup-discovery
registry.local/hyundai-bridge@sha256:e77a60324d720c58eb95bf2fddbdf6eddc6221491eaeaba6c160888b70593c42
```

Deployed through Arcane by changing only this project's image digest. The bridge
started at 17:48:50 UTC and successful discovery at 17:48:51 UTC found one linked
vehicle. The status endpoint reported one HTTP 200 discovery request, zero errors,
connected MQTT, a reused stored session and `nextPollAt: null`. Browser verification
showed `Fordonslista · Vid uppstart` and `Ingen planerad`. Local .NET tests: 64 passed;
the two broker-dependent tests were skipped without a test broker. Real vehicle
state retrieval and controls remain pending; the requested 60-second vehicle-state
cadence is separate from discovery.

Rollback: restore the previous ten-minute-discovery image digest (`56ca4365…`) and
redeploy only this project. Preserve credentials, port binding and data volume.

## At most five startup attempts — 2026-10-07

Startup discovery is limited to five attempts in total (the initial attempt plus
four retries). Failed attempts wait 10/20/40/60 minutes, or a longer Hyundai
`Retry-After`. A fifth failure clears the next scheduled read; the bridge stays
running with MQTT connected and must be restarted to retry discovery. Logs and
the dashboard indicate that discovery has stopped. A successful response still
stops further list reads immediately.

```text
registry.local/hyundai-bridge:20261007-discovery-5-attempts
registry.local/hyundai-bridge@sha256:dc15ffa9ad87584f91b6ee2ae444ba8bce61b42478a51415884a4cafaca419e9
```

Deployed through Arcane by changing only the image digest. Startup at 17:53:41 UTC
and discovery at 17:53:42 UTC returned one vehicle with HTTP 200, connected MQTT,
zero errors and no next scheduled list read. The deployed dashboard shows the
five-attempt limit. Local tests: 65 passed, two existing broker-dependent tests
skipped without a test broker. Failure exhaustion was tested locally, not induced
against the production Hyundai account.

Rollback: restore the preceding startup-only image (`e77a6032…`), preserving
credentials, port binding and persistent volume. Vehicle status and controls
remain pending.

## Current release: cached vehicle status and formatted JSON — 2026-10-07

The bridge reads each discovered vehicle's cached status immediately after startup
discovery, then every ten minutes after a successful read. No vehicle wake or remote
control endpoint is called. Discovery retains its five-attempt startup-only policy.
Status failures preserve the last state and back off per vehicle; HTTP 429 delays
all vehicles for the account. Observed state is published retained using the existing
v1 contract, with read-only capabilities inferred from actual non-null fields.

```text
registry.local/hyundai-bridge:20261007-status-json
registry.local/hyundai-bridge@sha256:131f70070711322d35eaa0ee2c97ffefd5cf1c2668d9d48136c0c29e0f0a788a
```

Deployed through Arcane by changing only this project's image. Final startup was
18:38:16 UTC. The running image digest was verified in the container overview.
Discovery, CCS token exchange and stored-status retrieval all returned HTTP 200;
one IONIQ 9 was found, MQTT was connected and no failures were registered.
The next status read was scheduled for 18:48:16 UTC, with no further discovery due.
Credentials, port binding and the persistent session volume were preserved.

The dashboard shows the latest successful response as indented, scrollable JSON,
with identifiers, credentials and location masked before diagnostic storage. Browser
verification confirmed the real IONIQ 9 response and preserved washer-fluid
data. Repeated dashboard updates made no extra Hyundai requests. Raw battery SoC
was 80.5%; the integer v1 state contract reports 81%. Range was 408 km, odometer
112.4 km, doors locked, charging/plugged-in false and AC/DC charge limits 80%.
The cached vehicle timestamp was 16:53:13 UTC; fetching it does not make it current.

Before the JSON enhancement, a read-only production MQTT subscription verified the
retained manifest, normalized state and availability with empty command capabilities.
Local validation passed 80 .NET tests with a disposable broker and 51 HA tests,
including the .NET/MQTT/HA path. After the diagnostic enhancement, 79 .NET tests
passed (three environment-dependent checks skipped without the broker/HA companion);
the four diagnostic tests passed again after correcting identifier masking.
The production HA custom integration and remote commands are not deployed or verified.
See [status research](vehicle-status-research.md) for the adapter evidence and limitations.

Rollback: restore the preceding cached-status image below, preserving configuration
and storage, then redeploy only this project. It retains ten-minute cached polling
but predates the formatted response panel.

```text
registry.local/hyundai-bridge@sha256:11a7d6bdc039a8e7a3eb2cca73eea7f10091db244ef2c452c866bddcd74e0bbf
```

## Install on the Docker host

Copy the repository's `compose.yaml` into the stack directory. Create an owner-only `.env` (0600) with the existing MQTT broker connection and Hyundai credentials, following `.env.example`. Set:

```text
HYUNDAI_BRIDGE_IMAGE=registry.local/hyundai-bridge@sha256:131f70070711322d35eaa0ee2c97ffefd5cf1c2668d9d48136c0c29e0f0a788a
BRIDGE_ID=home
```

Verify that this bridge ID is unused on the production broker before starting. Use a broker address reachable from the container, or attach the service to the broker's existing Docker network. Credential values must stay outside Git and logs. The image supports `_FILE` credentials when mounted secret files are preferred; see [configuration](bridge-service.md).

Pull the published image; do not build on the Docker VM:

```sh
cd /opt/ha-bridges/hyundai-bridge
sudo docker compose config --quiet
sudo docker compose pull hyundai-bridge
sudo docker compose up -d --no-build hyundai-bridge
sudo docker compose ps
```

The named volume stores private session tokens and the command journal. The example Compose exposes the status page on loopback port 8076; use the Docker host's LAN address instead for home-network access. Do not run another bridge process against the same data directory or MQTT bridge ID.

## Verify before recording deployment as complete

Check that the container stays running without a restart loop, uses the published amd64 image/.NET 11 RC runtime, and runs as UID 1654. Verify owner-only storage and read-only root filesystem. Structured logs must show MQTT connectivity and real Hyundai discovery. Verify retained bridge availability and manifest on the configured broker. An empty vehicle list is expected until the car is linked and is not proof of a vehicle connection.

For rollback of this first installation, stop only this service:

```sh
sudo docker compose stop hyundai-bridge
```

Keep the volume and secret configuration. Do not remove volumes or change unrelated services. HA custom integration installation is a separate deployment step; its existing source is `custom_components/hyundai_bridge`.
