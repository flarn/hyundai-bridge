"""Configuration and custom manifest discovery, without credentials in HA."""

import asyncio
import json
from unittest.mock import patch

from homeassistant.config_entries import SOURCE_MQTT, SOURCE_USER
from homeassistant.data_entry_flow import FlowResultType
from homeassistant.helpers.service_info.mqtt import MqttServiceInfo
from pytest_homeassistant_custom_component.common import async_fire_mqtt_message


async def test_discovery_confirmation(hass, mqtt_mock, manifest):
    with patch("custom_components.hyundai_bridge.async_setup_entry", return_value=True):
        result = await hass.config_entries.flow.async_init(
            "hyundai_bridge",
            context={"source": SOURCE_MQTT},
            data=MqttServiceInfo(
                topic="hyundai/v1/bridges/home/manifest",
                payload=json.dumps(manifest),
                qos=1,
                retain=True,
                subscribed_topic="hyundai/v1/bridges/+/manifest",
                timestamp=0,
            ),
        )
        assert result["type"] == FlowResultType.FORM
        assert result["step_id"] == "confirm"
        result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
        assert result["type"] == FlowResultType.CREATE_ENTRY
        assert result["data"] == {"bridge_id": "home"}
        assert result["result"].unique_id == "home"


async def test_manual_flow_reads_retained_manifest(hass, mqtt_mock, manifest):
    form = await hass.config_entries.flow.async_init(
        "hyundai_bridge", context={"source": SOURCE_USER}
    )
    assert form["type"] == FlowResultType.FORM
    assert list(form["data_schema"].schema) == ["bridge_id"]
    with patch("custom_components.hyundai_bridge.async_setup_entry", return_value=True):
        task = asyncio.create_task(
            hass.config_entries.flow.async_configure(
                form["flow_id"], {"bridge_id": "home"}
            )
        )
        # Wait for the flow's specific subscription, not its pending manifest.
        async with asyncio.timeout(1):
            while not any(
                call.args and call.args[0] == "hyundai/v1/bridges/home/manifest"
                for call in mqtt_mock.async_subscribe.call_args_list
            ):
                await asyncio.sleep(0)
        async_fire_mqtt_message(
            hass, "hyundai/v1/bridges/home/manifest", json.dumps(manifest), retain=True
        )
        result = await task
        assert result["type"] == FlowResultType.CREATE_ENTRY


async def test_duplicate_discovery_is_aborted(hass, bridge, manifest):
    result = await hass.config_entries.flow.async_init(
        "hyundai_bridge",
        context={"source": SOURCE_MQTT},
        data=MqttServiceInfo(
            topic="hyundai/v1/bridges/home/manifest",
            payload=json.dumps(manifest),
            qos=1,
            retain=True,
            subscribed_topic="hyundai/v1/bridges/+/manifest",
            timestamp=0,
        ),
    )
    assert result["reason"] == "already_configured"


async def test_invalid_discovery_is_aborted(hass, mqtt_mock, manifest):
    result = await hass.config_entries.flow.async_init(
        "hyundai_bridge",
        context={"source": SOURCE_MQTT},
        data=MqttServiceInfo(
            topic="hyundai/v1/bridges/wrong/manifest",
            payload=json.dumps(manifest),
            qos=1,
            retain=True,
            subscribed_topic="hyundai/v1/bridges/+/manifest",
            timestamp=0,
        ),
    )
    assert result["reason"] == "invalid_manifest"


async def test_invalid_id_and_unreachable_bridge(hass, mqtt_mock):
    result = await hass.config_entries.flow.async_init(
        "hyundai_bridge", context={"source": SOURCE_USER}
    )
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {"bridge_id": "bad/+/id"}
    )
    assert result["errors"] == {"bridge_id": "invalid_bridge_id"}
    with patch("custom_components.hyundai_bridge.config_flow.MANIFEST_TIMEOUT", 0.01):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {"bridge_id": "missing"}
        )
    assert result["errors"] == {"base": "bridge_unavailable"}


async def test_missing_mqtt_has_native_config_error(hass):
    result = await hass.config_entries.flow.async_init(
        "hyundai_bridge", context={"source": SOURCE_USER}
    )
    with patch(
        "homeassistant.components.mqtt.async_wait_for_mqtt_client", return_value=False
    ):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {"bridge_id": "home"}
        )
    assert result["errors"] == {"base": "mqtt_unavailable"}
