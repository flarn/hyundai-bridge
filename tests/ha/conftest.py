"""Synthetic normalized data; no vehicle or Hyundai credentials are used."""

import asyncio
import json
from pathlib import Path
from unittest.mock import Mock

import pytest
from pytest_homeassistant_custom_component.common import (
    MockConfigEntry,
    async_fire_mqtt_message,
)

ROOT = Path(__file__).resolve().parents[2]


@pytest.fixture
def hass_config_dir(tmp_path):
    (tmp_path / "custom_components").symlink_to(
        ROOT / "custom_components", target_is_directory=True
    )
    return str(tmp_path)


@pytest.fixture
async def mqtt_mock(mqtt_mock_entry, mqtt_client_mock, mock_hass_config):
    client = await mqtt_mock_entry()
    yield client
    # The HA fixture's fake paho disconnect does not close its fake socket.
    # Emulate that callback so the actual MQTT client's timers are cleaned up.
    mqtt_client_mock.on_socket_close(
        mqtt_client_mock, None, Mock(fileno=Mock(return_value=-1))
    )


@pytest.fixture(autouse=True)
def custom_integrations(enable_custom_integrations):
    """Allow HA to load the integration from this repository."""


@pytest.fixture
def manifest():
    return json.loads((ROOT / "contract/examples/manifest.json").read_text())


@pytest.fixture
def vehicle_state():
    return json.loads((ROOT / "contract/examples/state.json").read_text())


async def send(
    hass,
    suffix,
    payload,
    *,
    retained=False,
    vehicle_id="example-ev",
    wait_for_tasks=True,
):
    topic = f"hyundai/v1/{vehicle_id}/{suffix}"
    async_fire_mqtt_message(hass, topic, json.dumps(payload), qos=1, retain=retained)
    if wait_for_tasks:
        await hass.async_block_till_done()
    else:
        # Flush dispatcher callbacks without waiting for the pending service
        # whose acknowledgement this test has yet to send.
        await asyncio.sleep(0)


async def announce(hass, manifest, *, retained=False):
    async_fire_mqtt_message(
        hass,
        "hyundai/v1/bridges/home/manifest",
        json.dumps(manifest),
        qos=1,
        retain=retained,
    )
    async_fire_mqtt_message(
        hass, "hyundai/v1/bridges/home/availability", "online", qos=1, retain=retained
    )
    await hass.async_block_till_done()


@pytest.fixture
async def bridge(hass, mqtt_mock, manifest, vehicle_state):
    entry = MockConfigEntry(
        domain="hyundai_bridge",
        title="Hyundai Bridge",
        unique_id="home",
        data={"bridge_id": "home"},
    )
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await announce(hass, manifest, retained=True)
    await send(hass, "state", vehicle_state, retained=True)
    await send(hass, "availability", {"apiReachable": True, "dataFreshness": "stale"})
    return entry
