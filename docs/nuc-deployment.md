# NUC deployment

Target: Docker host `ha-bridges`, `192.168.1.67`, following the existing stacks under `/opt/ha-bridges`. Suggested stack directory: `/opt/ha-bridges/hyundai-bridge`.

## Prepared release — 2026-10-07

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

## Install on the Docker host

Copy the repository's `compose.yaml` into the stack directory. Create an owner-only `.env` (0600) with the existing MQTT broker connection and Hyundai credentials, following `.env.example`. Set:

```text
HYUNDAI_BRIDGE_IMAGE=registry.local/hyundai-bridge@sha256:1f4606f9fd4d9ac17194b1063d7ed145aee34e460df1a628047925b8f15ef51b
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

The named volume stores private session tokens and the command journal. The service exposes no network ports. Do not run another bridge process against the same data directory or MQTT bridge ID.

## Verify before recording deployment as complete

Check that the container stays running without a restart loop, uses the published amd64 image/.NET 11 RC runtime, and runs as UID 1654. Verify owner-only storage and read-only root filesystem. Structured logs must show MQTT connectivity and real Hyundai discovery. Verify retained bridge availability and manifest on the configured broker. An empty vehicle list is expected until the car is linked and is not proof of a vehicle connection.

For rollback of this first installation, stop only this service:

```sh
sudo docker compose stop hyundai-bridge
```

Keep the volume and secret configuration. Do not remove volumes or change unrelated services. HA custom integration installation is a separate deployment step; its existing source is `custom_components/hyundai_bridge`.
