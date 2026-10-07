# NUC deployment

Target: Docker host `ha-bridges`, `192.168.1.67`, following the existing stacks under `/opt/ha-bridges`. Suggested stack directory: `/opt/ha-bridges/hyundai-bridge`.

## Prepared release — 2026-10-07

Source commit: `93a9bdf`. Runtime: .NET 11 RC1.

Image built locally for `linux/amd64` and pushed through the existing Docker registry trust configuration:

```text
registry.local/hyundai-bridge:20261007-net11rc1-93a9bdf
registry.local/hyundai-bridge@sha256:1f4606f9fd4d9ac17194b1063d7ed145aee34e460df1a628047925b8f15ef51b
```

**Not deployed yet.** SSH for `anton@192.168.1.67` rejected the available key; the accessible Arcane manager on port 3552 requires login. Host resource capacity, existing MQTT connection settings and container startup must be checked through authenticated host access before claiming deployment success. No existing stack was changed.

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
