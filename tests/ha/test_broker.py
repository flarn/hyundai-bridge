"""Optional real MQTT smoke test against a disposable local broker.

Never point this test at a production broker: it writes synthetic retained data.
"""

import asyncio
import json
import os
from functools import partial

import pytest
from homeassistant.helpers import entity_registry as er
from paho.mqtt.publish import multiple
from pytest_homeassistant_custom_component.common import MockConfigEntry

pytestmark = pytest.mark.skipif(
    not os.environ.get("HYUNDAI_TEST_MQTT_PORT"),
    reason="Requires an isolated loopback broker (see docs/home-assistant.md)",
)


async def wait_until(check):
    async with asyncio.timeout(5):
        while not check():
            await asyncio.sleep(0.01)


async def test_retained_bootstrap_reload_and_offline(
    hass, manifest, vehicle_state, socket_enabled, mock_hass_config
):
    port = int(os.environ["HYUNDAI_TEST_MQTT_PORT"])

    async def publish(messages):
        await hass.async_add_executor_job(
            partial(multiple, messages, hostname="127.0.0.1", port=port)
        )

    # Publish state first and disconnect the fixture publisher before HA starts.
    # Only the broker can then supply the retained snapshot to new subscriptions.
    await publish(
        [
            {
                "topic": "hyundai/v1/example-ev/state",
                "payload": json.dumps(vehicle_state),
                "qos": 1,
                "retain": True,
            },
            {
                "topic": "hyundai/v1/example-ev/availability",
                "payload": '{"apiReachable":true,"dataFreshness":"stale"}',
                "qos": 1,
                "retain": True,
            },
            {
                "topic": "hyundai/v1/bridges/home/manifest",
                "payload": json.dumps(manifest),
                "qos": 1,
                "retain": True,
            },
            {
                "topic": "hyundai/v1/bridges/home/availability",
                "payload": "online",
                "qos": 1,
                "retain": True,
            },
        ]
    )
    mqtt_entry = MockConfigEntry(
        domain="mqtt",
        title="Test MQTT",
        version=1,
        minor_version=2,
        data={"broker": "127.0.0.1", "port": port, "protocol": "3.1.1"},
        options={"birth_message": {}},
    )
    mqtt_entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(mqtt_entry.entry_id)

    # Discovery uses the integration's manifest subscription, with no MQTT
    # entity discovery messages or manual source injection.
    def discovered_flow():
        return next(
            (
                flow
                for flow in hass.config_entries.flow.async_progress()
                if flow["handler"] == "hyundai_bridge"
            ),
            None,
        )

    await wait_until(lambda: discovered_flow() is not None)
    result = await hass.config_entries.flow.async_configure(
        discovered_flow()["flow_id"], {}
    )
    entry = result["result"]

    def battery():
        entity_id = er.async_get(hass).async_get_entity_id(
            "sensor", "hyundai_bridge", "KMH00000000000001_batteryPercent"
        )
        return hass.states.get(entity_id) if entity_id else None

    await wait_until(lambda: battery() is not None and battery().state == "73")
    assert battery().attributes["data_freshness"] == "stale"
    assert await hass.config_entries.async_reload(entry.entry_id)
    await wait_until(lambda: battery() is not None and battery().state == "73")
    assert await hass.config_entries.async_reload(mqtt_entry.entry_id)
    await wait_until(lambda: battery() is not None and battery().state == "73")
    await publish(
        [
            {
                "topic": "hyundai/v1/bridges/home/availability",
                "payload": "offline",
                "qos": 1,
                "retain": True,
            }
        ]
    )
    await wait_until(lambda: battery().state == "unavailable")
    await publish(
        [
            {
                "topic": "hyundai/v1/bridges/home/availability",
                "payload": "online",
                "qos": 1,
                "retain": True,
            }
        ]
    )
    await wait_until(lambda: battery().state == "73")
