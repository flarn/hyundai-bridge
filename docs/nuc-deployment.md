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

## Current release: 60-second polling — 2026-10-07

At the user's request, normal polling now waits 60 seconds after each successful
read. API-failure backoff remains 10/20/40/60 minutes (or a longer `Retry-After`),
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

## Install on the Docker host

Copy the repository's `compose.yaml` into the stack directory. Create an owner-only `.env` (0600) with the existing MQTT broker connection and Hyundai credentials, following `.env.example`. Set:

```text
HYUNDAI_BRIDGE_IMAGE=registry.local/hyundai-bridge@sha256:2c282b8c08eedad0ab57ea76d2d297e24d34059dc286e686d98b80de281c64d8
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
