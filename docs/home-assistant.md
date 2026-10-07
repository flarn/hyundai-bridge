# Home Assistant integration

`custom_components/hyundai_bridge` consumes the [MQTT v1 contract](mqtt-v1.md) and exposes native Home Assistant entities. Tested with Home Assistant **2026.9.4** and Python 3.14.6. No Hyundai account, tokens, endpoint knowledge or Python Hyundai library is present in HA.

**Current delivery:** the HA consumer works against a v1 producer. The .NET service currently authenticates and discovers vehicles; its MQTT publisher and command transport are implemented, while the Hyundai state/control adapter remains pending. The account has no linked vehicle yet. Installing this component alone therefore does not supply live car data. All HA verification below uses synthetic vehicle data on an isolated broker.

## Install

1. Copy `custom_components/hyundai_bridge` into `<HA config>/custom_components/hyundai_bridge`, preserving its files and translations. Restart Home Assistant.
2. Configure HA's standard MQTT integration for the broker the .NET bridge will use. Hyundai credentials belong exclusively in the .NET service.
3. With a v1 bridge publishing its retained manifest, **Settings → Devices & services** shows **Hyundai Bridge** as discovered. Confirm it. Alternatively, choose **Add integration → Hyundai Bridge** and enter the bridge identifier (`home` in the contract example).
4. Each advertised vehicle appears as one Hyundai device via a Hyundai Bridge device. VIN identifies the device; entity registry unique IDs combine VIN and the normalized feature. User names/entity IDs survive integration reloads and restarts.

This is custom integration discovery through the bridge manifest, not MQTT entity discovery. A normal HA user uses native devices, entities and services. No topic editing, YAML MQTT entities or Hyundai login configuration is required.

## Entities and dashboard

Entities appear only for advertised capabilities. Supported scope: battery, range, odometer, charging, plugged-in, lock, climate, AC/DC charge limits, refresh/start-charging/stop-charging buttons, GPS tracker and optional measured cabin/outside temperature. A vehicle without EV capability does not receive EV controls. This version adds no door sensors or custom frontend card.

Use standard Tile cards for battery/range, lock, charge limits, charging status and buttons; use HA's standard climate card or climate Tile features for remote temperature control. Entity names are translated into English and Swedish. Vehicle names come from metadata, so the integration works across Hyundai models.

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
