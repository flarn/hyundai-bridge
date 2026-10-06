"""Native HA services send controls and wait for correlated completion."""

import asyncio
import json
from unittest.mock import patch

import pytest
from homeassistant.components import mqtt
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.dispatcher import async_dispatcher_send

from .conftest import send


async def start_service(hass, mqtt_mock, domain, service, entity_id, **arguments):
    mqtt_mock.async_publish.reset_mock()
    task = asyncio.create_task(
        hass.services.async_call(
            domain, service, {"entity_id": entity_id, **arguments}, blocking=True
        )
    )
    async with asyncio.timeout(1):
        while mqtt_mock.async_publish.call_args is None:
            if task.done():
                await task
            await asyncio.sleep(0)
    call = mqtt_mock.async_publish.call_args
    assert call is not None
    assert call.args[2:4] == (1, False)
    return task, call.args[0], json.loads(call.args[1])


async def finish(hass, task, topic, payload, *, status="completed", message=None):
    command = topic.split("/command/", 1)[1]
    await send(
        hass,
        "command-result",
        {
            "commandId": payload["commandId"],
            "command": command,
            "status": status,
            "message": message,
        },
        wait_for_tasks=False,
    )
    await task


@pytest.mark.parametrize(
    ("domain", "service", "entity_id", "command", "arguments", "expected"),
    [
        ("lock", "lock", "lock.example_hyundai", "lock", {}, {}),
        ("lock", "unlock", "lock.example_hyundai", "unlock", {}, {}),
        ("button", "press", "button.example_hyundai_refresh", "refresh", {}, {}),
        (
            "button",
            "press",
            "button.example_hyundai_start_charging",
            "charging/start",
            {},
            {},
        ),
        (
            "button",
            "press",
            "button.example_hyundai_stop_charging",
            "charging/stop",
            {},
            {},
        ),
        (
            "climate",
            "turn_on",
            "climate.example_hyundai",
            "climate/start",
            {},
            {"temperatureCelsius": 21, "defrost": False},
        ),
        ("climate", "turn_off", "climate.example_hyundai", "climate/stop", {}, {}),
        (
            "climate",
            "set_temperature",
            "climate.example_hyundai",
            "climate/start",
            {"temperature": 22.5},
            {"temperatureCelsius": 22.5, "defrost": False},
        ),
        (
            "climate",
            "set_preset_mode",
            "climate.example_hyundai",
            "climate/start",
            {"preset_mode": "defrost"},
            {"temperatureCelsius": 21, "defrost": True},
        ),
        (
            "number",
            "set_value",
            "number.example_hyundai_ac_charge_limit",
            "charge-limit",
            {"value": 70},
            {"acPercent": 70, "dcPercent": 90},
        ),
        (
            "number",
            "set_value",
            "number.example_hyundai_dc_charge_limit",
            "charge-limit",
            {"value": 100},
            {"acPercent": 80, "dcPercent": 100},
        ),
    ],
)
async def test_native_services(
    hass, mqtt_mock, bridge, domain, service, entity_id, command, arguments, expected
):
    before = hass.states.get(entity_id).state
    task, topic, payload = await start_service(
        hass, mqtt_mock, domain, service, entity_id, **arguments
    )
    assert topic == f"hyundai/v1/example-ev/command/{command}"
    assert {k: v for k, v in payload.items() if k != "commandId"} == expected
    await finish(hass, task, topic, payload)
    if domain != "button":
        assert hass.states.get(entity_id).state == before


async def test_accepted_is_not_completion_and_state_is_observed(
    hass, mqtt_mock, bridge, vehicle_state
):
    task, topic, payload = await start_service(
        hass, mqtt_mock, "lock", "unlock", "lock.example_hyundai"
    )
    result = {
        "commandId": payload["commandId"],
        "command": "unlock",
        "status": "accepted",
        "message": None,
    }
    await send(hass, "command-result", result, wait_for_tasks=False)
    assert not task.done()
    assert hass.states.get("lock.example_hyundai").state == "locked"
    # Wrong correlation and retained command results cannot acknowledge a control.
    await send(
        hass,
        "command-result",
        {**result, "commandId": "another-command", "status": "completed"},
        wait_for_tasks=False,
    )
    await send(
        hass,
        "command-result",
        {**result, "command": "lock", "status": "completed"},
        wait_for_tasks=False,
    )
    await send(
        hass,
        "command-result",
        {**result, "status": "completed"},
        retained=True,
        wait_for_tasks=False,
    )
    assert not task.done()
    await finish(hass, task, topic, payload)
    assert hass.states.get("lock.example_hyundai").state == "locked"
    # QoS duplication cannot regress a terminal command to accepted.
    await send(hass, "command-result", result)
    assert bridge.runtime_data.last_commands["example-ev"]["status"] == "completed"
    await send(hass, "state", {**vehicle_state, "isLocked": False})
    assert hass.states.get("lock.example_hyundai").state == "unlocked"


async def test_failed_command_is_visible_and_second_command_is_rejected(
    hass, mqtt_mock, bridge
):
    task, topic, payload = await start_service(
        hass, mqtt_mock, "button", "press", "button.example_hyundai_refresh"
    )
    with pytest.raises(HomeAssistantError, match="already in progress"):
        await bridge.runtime_data.async_command("example-ev", "lock")
    with pytest.raises(HomeAssistantError, match="Vehicle offline"):
        await finish(
            hass, task, topic, payload, status="failed", message="Vehicle offline"
        )
    assert bridge.runtime_data.last_commands["example-ev"]["status"] == "failed"
    assert not bridge.runtime_data._pending


async def test_command_timeout_has_unknown_outcome_and_is_not_resent(
    hass, mqtt_mock, bridge
):
    with patch("custom_components.hyundai_bridge.runtime.COMMAND_TIMEOUT", 0.02):
        task, _, _ = await start_service(
            hass, mqtt_mock, "lock", "unlock", "lock.example_hyundai"
        )
        with pytest.raises(HomeAssistantError, match="outcome is unknown"):
            await task
    assert mqtt_mock.async_publish.call_count == 1
    assert bridge.runtime_data.last_commands["example-ev"]["status"] == "unknown"
    assert hass.states.get("lock.example_hyundai").state == "locked"


async def test_broker_disconnect_cancels_pending_and_keeps_observation(
    hass, mqtt_mock, bridge
):
    task, _, _ = await start_service(
        hass, mqtt_mock, "lock", "unlock", "lock.example_hyundai"
    )
    async_dispatcher_send(hass, mqtt.MQTT_CONNECTION_STATE, False)
    with pytest.raises(HomeAssistantError, match="connection lost"):
        await task
    await hass.async_block_till_done()
    assert hass.states.get("lock.example_hyundai").state == "unavailable"
    assert bridge.runtime_data.states["example-ev"]["isLocked"] is True


@pytest.mark.parametrize(
    ("domain", "service", "entity_id", "arguments"),
    [
        (
            "number",
            "set_value",
            "number.example_hyundai_ac_charge_limit",
            {"value": 75},
        ),
        (
            "climate",
            "set_temperature",
            "climate.example_hyundai",
            {"temperature": 21.25},
        ),
    ],
)
async def test_unsupported_increments_do_not_publish(
    hass, mqtt_mock, bridge, domain, service, entity_id, arguments
):
    mqtt_mock.async_publish.reset_mock()
    with pytest.raises(HomeAssistantError, match="supported values"):
        await hass.services.async_call(
            domain, service, {"entity_id": entity_id, **arguments}, blocking=True
        )
    mqtt_mock.async_publish.assert_not_called()


async def test_missing_climate_observation_does_not_invent_defaults(
    hass, mqtt_mock, bridge, vehicle_state
):
    await send(hass, "state", {**vehicle_state, "targetTemperatureCelsius": None})
    mqtt_mock.async_publish.reset_mock()
    with pytest.raises(HomeAssistantError, match="observed target"):
        await hass.services.async_call(
            "climate",
            "turn_on",
            {"entity_id": "climate.example_hyundai"},
            blocking=True,
        )
    await send(hass, "state", {**vehicle_state, "isDefrostOn": None})
    with pytest.raises(HomeAssistantError, match="defrost state"):
        await hass.services.async_call(
            "climate",
            "turn_on",
            {"entity_id": "climate.example_hyundai"},
            blocking=True,
        )
    mqtt_mock.async_publish.assert_not_called()


async def test_missing_other_charge_limit_disables_numbers(
    hass, mqtt_mock, bridge, vehicle_state
):
    await send(hass, "state", {**vehicle_state, "dcChargeLimitPercent": None})
    assert (
        hass.states.get("number.example_hyundai_ac_charge_limit").state == "unavailable"
    )


async def test_unload_cancels_pending_and_unsubscribes(hass, mqtt_mock, bridge):
    task, _, _ = await start_service(
        hass, mqtt_mock, "lock", "unlock", "lock.example_hyundai"
    )
    runtime = bridge.runtime_data
    assert await hass.config_entries.async_unload(bridge.entry_id)
    with pytest.raises(HomeAssistantError, match="connection lost"):
        await task
    assert not runtime._pending
    assert not runtime._unsubscribes
    assert not runtime._vehicle_subscriptions


async def test_publish_timeout_is_bounded(hass, mqtt_mock, bridge):
    async def stalled_publish(*args, **kwargs):
        await asyncio.Future()

    with (
        patch("custom_components.hyundai_bridge.runtime.COMMAND_TIMEOUT", 0.01),
        patch(
            "homeassistant.components.mqtt.async_publish", side_effect=stalled_publish
        ),
    ):
        with pytest.raises(HomeAssistantError, match="outcome is unknown"):
            await bridge.runtime_data.async_command("example-ev", "lock")
    assert not bridge.runtime_data._pending
    assert bridge.runtime_data.last_commands["example-ev"]["status"] == "unknown"
