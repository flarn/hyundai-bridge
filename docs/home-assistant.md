# Home Assistant integration

`custom_components/hyundai_bridge` consumes the [MQTT v1 contract](mqtt-v1.md) and exposes native Home Assistant entities. Tested with Home Assistant **2026.9.4** and Python 3.14.6. No Hyundai account, tokens, endpoint knowledge or Python Hyundai library is present in HA.

**Current delivery:** the HA consumer works against a v1 producer. The .NET service authenticates, discovers a linked vehicle and retrieves cached Hyundai state every ten minutes without waking the vehicle. It also publishes auxiliary battery, eight opening fields, charging power/time and source observation time when observed. Hyundai remote controls remain pending and unadvertised. HA entity verification uses synthetic data; production installation of the HA component is a separate deployment step.

## Install

### HACS

The public GitHub repository includes root `hacs.json` metadata and a single
integration under `custom_components/hyundai_bridge`.

1. Open **HACS → ⋮ → Custom repositories**.
2. Add `https://github.com/flarn/hyundai-bridge` with type **Integration**.
3. Download **Hyundai Bridge** from HACS and restart Home Assistant.
4. Configure HA's MQTT integration for the same broker as the bridge.
5. Confirm the discovered **Hyundai Bridge** under **Settings → Devices & services**,
   or add it manually with bridge identifier `home`.

Only the HA component is installed by HACS; the .NET Docker service stays on its
existing host. No Hyundai credentials are configured in HA. With no GitHub releases,
HACS downloads/tracks the default branch. A new download/update requires restarting
HA to load changed integration code.

References: [HACS custom repositories](https://www.hacs.dev/docs/faq/custom_repositories/),
[integration layout](https://www.hacs.dev/docs/publish/integration/),
[private repository limitation](https://www.hacs.dev/docs/faq/private_repositories/).

### Manual installation

1. Copy `custom_components/hyundai_bridge` into `<HA config>/custom_components/hyundai_bridge`, preserving its files and translations. Restart Home Assistant.
2. Configure HA's standard MQTT integration for the broker the .NET bridge will use. Hyundai credentials belong exclusively in the .NET service.
3. With a v1 bridge publishing its retained manifest, **Settings → Devices & services** shows **Hyundai Bridge** as discovered. Confirm it. Alternatively, choose **Add integration → Hyundai Bridge** and enter the bridge identifier (`home` in the contract example).
4. Each advertised vehicle appears as one Hyundai device via a Hyundai Bridge device. VIN identifies the device; entity registry unique IDs combine VIN and the normalized feature. User names/entity IDs survive integration reloads and restarts.

This is custom integration discovery through the bridge manifest, not MQTT entity discovery. A normal HA user uses native devices, entities and services. No topic editing, YAML MQTT entities or Hyundai login configuration is required.

## Entities and dashboard

Entities appear only for advertised capabilities. Supported scope: battery, 12-V battery, range, odometer, charging, plugged-in, four doors, trunk, hood, charge port, sunroof, charging power (kW), remaining charging time (minutes), vehicle status timestamp, four tire pressures, aggregate low-tire-pressure warning, climate target temperature, remote climate activation, cabin fan activity/level, battery minimum/maximum temperature, remaining battery energy, four side windows, lock, climate, AC/DC charge limits, refresh/start-charging/stop-charging buttons, GPS tracker and optional measured cabin/outside temperature. A vehicle without EV capability does not receive EV controls. No custom frontend card is required.

Use standard Tile cards for battery/range, lock, charge limits, charging status and buttons; use HA's standard climate card or climate Tile features for remote temperature control. Entity names are translated into English and Swedish. Vehicle names come from metadata, so the integration works across Hyundai models.

Read-only climate target and cabin fan entities require only their state fields. Fan activity does not certify remote HVAC activation or heating/cooling action. Remote climate activation is a separate read-only binary sensor; the fan level is a unitless integer measurement, not RPM/percent. These observations do not enable remote commands. A target of OFF is unknown temperature; a numerical target is not measured cabin temperature. Temperature capabilities initially appear only after a supported numerical observation. Remaining battery energy uses the energy-storage device class and measurement state class, not cumulative charged energy.

Climate reports observed remote on/off state and target temperature. `heat_cool` represents temperature control being enabled; no real-time heating/cooling action is invented. Defrost appears as a native preset only when supported. The example bounds are **synthetic**, not Hyundai limits; a real bridge must publish verified bounds for its vehicle.

Changing one charge limit preserves the last observed opposite limit. Both limits must be known. Turning climate on preserves the observed target/defrost setting; missing observations yield an explicit service error. Setting an explicit supported temperature or preset sends the requested remote climate start.

## Availability and commands

`bridge_online`, `api_reachable` and `data_freshness` attributes are independent. Vehicle/bridge timestamps distinguish the vehicle observation from its cached retrieval. An API outage or stale observation leaves the last valid values readable. Bridge/broker disconnection makes entities unavailable; reconnect retrieves retained metadata/state. The component does not age out observations on an invented time limit.

Controls use native HA services (`lock.lock`, `climate.set_temperature`, `number.set_value`, `button.press`, etc.). They publish a non-retained command with a UUID and wait up to 120 seconds for a correlated terminal result. `accepted` keeps the request pending. `failed` raises a service error. Timeout or disconnection reports an **unknown outcome**, without automatic resubmission. `last_command` exposes the status; only new state observations change lock/climate/charge-limit values.

Command completion and actual Hyundai operation are different verification levels. These tests verify HA and transport behavior; they do not certify physical commands. The .NET transport implements persisted deduplication, refresh cooldown and bounded execution. The Hyundai adapter must still verify physical completion and observations. See [bridge service](bridge-service.md).

## Run tests

```sh
python3.14 -m venv .venv
.venv/bin/pip install -r requirements-test.txt
.venv/bin/pytest -q
.venv/bin/ruff check custom_components tests/ha --select E,F,I
.venv/bin/ruff format --check custom_components tests/ha
```

Tests load the real HA integration/platforms/state machine and registries, with HA's MQTT client fixture. They cover entity mapping, multiple vehicle capabilities, nullable/invalid data, config flow, stable identity/reload, connection loss and native services through accepted/completed/failed/timeout responses. The optional broker and .NET pipeline tests are skipped unless explicitly configured.

To run the broker smoke test, use a **disposable local broker**. It writes synthetic retained documents and must never target a production broker. The test accepts only a loopback port, not an arbitrary hostname.

```sh
docker run --detach --rm --name hyundai-ha-test-broker \
  -p 127.0.0.1:18884:1883 \
  --mount "type=bind,source=$(pwd)/tests/ha/mosquitto.conf,target=/mosquitto/config/mosquitto.conf,readonly" \
  eclipse-mosquitto:2.0.22
HYUNDAI_TEST_MQTT_PORT=18884 dotnet test HyundaiBridge.slnx --configuration Release
HYUNDAI_TEST_MQTT_PORT=18884 .venv/bin/pytest -q tests/ha/test_broker.py tests/ha/test_dotnet_pipeline.py
docker stop hyundai-ha-test-broker
```

The smoke test publishes retained state before HA starts, disconnects its publisher, discovers/configures the custom integration through MQTT, verifies native battery state, reloads the integration/MQTT connection and checks offline/online transitions. It uses real paho/Mosquitto transport. No messages are sent to a real vehicle and no production HA deployment is included.

The additional pipeline test starts the real .NET MQTT publisher with a synthetic test adapter, consumes its retained documents in HA and calls the native unlock service. It verifies pending acceptance, confirmed completion and the subsequent observed lock state through actual Mosquitto transport. No Hyundai requests or credentials are used.
