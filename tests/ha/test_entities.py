"""Exercise native entity state through HA's registry and state machine."""

from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import entity_registry as er
from pytest_homeassistant_custom_component.common import async_fire_mqtt_message

from .conftest import announce, send


async def test_native_entities_and_single_vehicle_device(hass, bridge):
    registry = er.async_get(hass)
    entries = er.async_entries_for_config_entry(registry, bridge.entry_id)
    assert len(entries) == 15
    assert all(entry.platform == "hyundai_bridge" for entry in entries)
    assert len({entry.device_id for entry in entries}) == 1
    vehicle = dr.async_get(hass).async_get_device_by_identifier(
        ("hyundai_bridge", "KMH00000000000001"), bridge.entry_id
    )
    assert (vehicle.manufacturer, vehicle.model, vehicle.serial_number) == (
        "Hyundai",
        "Example EV",
        "KMH00000000000001",
    )
    parent = dr.async_get(hass).async_get_device_by_identifier(
        ("hyundai_bridge", "bridge:home"), bridge.entry_id
    )
    assert vehicle.via_device_id == parent.id
    states = {
        entry.unique_id.removeprefix("KMH00000000000001_"): hass.states.get(
            entry.entity_id
        )
        for entry in entries
    }
    assert states["batteryPercent"].state == "73"
    assert states["batteryPercent"].attributes["device_class"] == "battery"
    assert states["batteryPercent"].attributes["state_class"] == "measurement"
    assert states["estimatedRangeKm"].state == "382"
    assert states["odometerKm"].state == "12345"
    assert states["isCharging"].state == "off"
    assert states["isPluggedIn"].state == "on"
    assert states["lock"].state == "locked"
    assert states["climate"].state == "off"
    assert states["climate"].attributes["temperature"] == 21
    assert states["climate"].attributes["current_temperature"] == 20
    assert "hvac_action" not in states["climate"].attributes
    assert states["ac_charge_limit"].state == "80"
    assert states["ac_charge_limit"].attributes["step"] == 10
    assert states["location"].attributes["latitude"] == 59.4
    assert states["batteryPercent"].attributes["data_freshness"] == "stale"


async def test_api_outage_preserves_observation_bridge_offline_does_not(hass, bridge):
    entity_id = er.async_get(hass).async_get_entity_id(
        "sensor", "hyundai_bridge", "KMH00000000000001_batteryPercent"
    )
    await send(hass, "availability", {"apiReachable": False, "dataFreshness": "stale"})
    assert hass.states.get(entity_id).state == "73"
    assert hass.states.get(entity_id).attributes["api_reachable"] is False
    async_fire_mqtt_message(hass, "hyundai/v1/bridges/home/availability", "offline")
    await hass.async_block_till_done()
    assert hass.states.get(entity_id).state == "unavailable"
    async_fire_mqtt_message(hass, "hyundai/v1/bridges/home/availability", "online")
    await hass.async_block_till_done()
    assert hass.states.get(entity_id).state == "73"


async def test_missing_values_are_unknown_and_invalid_state_keeps_last(
    hass, bridge, vehicle_state
):
    registry = er.async_get(hass)
    battery = registry.async_get_entity_id(
        "sensor", "hyundai_bridge", "KMH00000000000001_batteryPercent"
    )
    charging = registry.async_get_entity_id(
        "binary_sensor", "hyundai_bridge", "KMH00000000000001_isCharging"
    )
    invalid = {**vehicle_state, "batteryPercent": True}
    await send(hass, "state", invalid)
    assert hass.states.get(battery).state == "73"
    await send(
        hass, "state", {**vehicle_state, "batteryPercent": None, "isCharging": None}
    )
    assert hass.states.get(battery).state == "unknown"
    assert hass.states.get(charging).state == "unknown"
    # A complete snapshot does not silently merge absent fields with old ones.
    await send(
        hass,
        "state",
        {
            "vehicleId": vehicle_state["vehicleId"],
            "vin": vehicle_state["vin"],
            "bridgeUpdatedAt": vehicle_state["bridgeUpdatedAt"],
        },
    )
    assert hass.states.get(battery).state == "unknown"


async def test_manifest_capabilities_control_entities_and_identity(
    hass, bridge, manifest
):
    registry = er.async_get(hass)
    added = {
        "vehicleId": "example-ice",
        "vin": "KMH00000000000002",
        "name": "Second Hyundai",
        "model": "Example ICE",
        "capabilities": {"stateFields": ["odometerKm"], "commands": ["refresh"]},
    }
    manifest["vehicles"].append(added)
    await announce(hass, manifest)
    entries = er.async_entries_for_config_entry(registry, bridge.entry_id)
    assert len(entries) == 17
    assert {e.domain for e in entries if e.unique_id.startswith(added["vin"])} == {
        "sensor",
        "button",
    }
    battery = registry.async_get_entity_id(
        "sensor", "hyundai_bridge", "KMH00000000000001_batteryPercent"
    )
    manifest["vehicles"][0]["capabilities"]["stateFields"].remove("batteryPercent")
    await announce(hass, manifest)
    assert hass.states.get(battery).state == "unavailable"
    # Reassigning a topic to another VIN must not control the wrong car.
    manifest["vehicles"][0]["vin"] = "KMH00000000000003"
    await announce(hass, manifest)
    assert bridge.runtime_data.vehicles["example-ev"].vin == "KMH00000000000001"


async def test_reload_preserves_entity_id_and_registry_customization(
    hass, bridge, manifest, vehicle_state
):
    registry = er.async_get(hass)
    battery = registry.async_get_entity_id(
        "sensor", "hyundai_bridge", "KMH00000000000001_batteryPercent"
    )
    registry.async_update_entity(
        battery, new_entity_id="sensor.my_hyundai_battery", name="My battery"
    )
    previous = bridge.runtime_data
    assert await hass.config_entries.async_reload(bridge.entry_id)
    await announce(hass, manifest)
    await send(hass, "state", vehicle_state)
    assert previous._stopped
    assert not previous._vehicle_subscriptions
    assert hass.states.get("sensor.my_hyundai_battery").state == "73"
    assert registry.async_get("sensor.my_hyundai_battery").name == "My battery"


async def test_unknown_metadata_does_not_invent_model(hass, bridge, manifest):
    manifest["vehicles"].append(
        {
            "vehicleId": "unnamed",
            "vin": "KMH00000000000004",
            "name": None,
            "model": None,
            "capabilities": {"stateFields": ["odometerKm"], "commands": []},
        }
    )
    await announce(hass, manifest)
    vehicle = dr.async_get(hass).async_get_device_by_identifier(
        ("hyundai_bridge", "KMH00000000000004"), bridge.entry_id
    )
    assert vehicle.model is None
    assert vehicle.name == "KMH00000000000004"


async def test_auxiliary_battery_and_openings_are_native_nullable_entities(
    hass, bridge, manifest, vehicle_state
):
    fields = {
        "auxiliaryBatteryPercent": 83,
        "isFrontLeftDoorOpen": True,
        "isFrontRightDoorOpen": False,
        "isRearLeftDoorOpen": False,
        "isRearRightDoorOpen": True,
        "isTrunkOpen": True,
        "isHoodOpen": False,
    }
    manifest["vehicles"][0]["capabilities"]["stateFields"].extend(fields)
    await announce(hass, manifest)
    await send(hass, "state", {**vehicle_state, **fields})
    registry = er.async_get(hass)
    entity_ids = {}
    for field, value in fields.items():
        domain = "sensor" if field == "auxiliaryBatteryPercent" else "binary_sensor"
        entity_ids[field] = registry.async_get_entity_id(
            domain, "hyundai_bridge", "KMH00000000000001_" + field
        )
        state = hass.states.get(entity_ids[field])
        assert state.state == ("83" if domain == "sensor" else "on" if value else "off")
        expected_class = (
            "battery"
            if domain == "sensor"
            else "opening"
            if field in ("isTrunkOpen", "isHoodOpen")
            else "door"
        )
        assert state.attributes["device_class"] == expected_class
    battery = hass.states.get(entity_ids["auxiliaryBatteryPercent"])
    assert battery.attributes["unit_of_measurement"] == "%"
    assert battery.attributes["state_class"] == "measurement"
    entries = er.async_entries_for_config_entry(registry, bridge.entry_id)
    assert len({entry.device_id for entry in entries}) == 1
    await send(hass, "state", {**vehicle_state, **dict.fromkeys(fields)})
    for entity_id in entity_ids.values():
        assert hass.states.get(entity_id).state == "unknown"
