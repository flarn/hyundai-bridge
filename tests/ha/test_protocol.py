"""Boundary validation and additive contract compatibility."""

import json

import pytest

from custom_components.hyundai_bridge.protocol import (
    parse_availability,
    parse_manifest,
    parse_result,
    parse_state,
)


def test_state_fixture_preserves_nulls_and_timestamps(manifest, vehicle_state):
    _, _, vehicles = parse_manifest(json.dumps(manifest))
    data = {
        **vehicle_state,
        "batteryPercent": None,
        "futureOptionalField": "ignored by entities",
    }
    assert parse_state(json.dumps(data), vehicles["example-ev"]) == data


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("batteryPercent", 101),
        ("batteryPercent", True),
        ("auxiliaryBatteryPercent", 101),
        ("auxiliaryBatteryPercent", True),
        ("isFrontLeftDoorOpen", 1),
        ("isFrontRightDoorOpen", "false"),
        ("isRearLeftDoorOpen", 2),
        ("isRearRightDoorOpen", 0),
        ("isTrunkOpen", "closed"),
        ("isHoodOpen", 1),
        ("isLocked", 1),
        ("isChargePortOpen", 2),
        ("isSunroofOpen", "closed"),
        ("chargingPowerKw", -1),
        ("chargingPowerKw", float("inf")),
        ("remainingChargeTimeMinutes", -1),
        ("remainingChargeTimeMinutes", True),
        ("estimatedRangeKm", -1),
        ("latitude", 91),
        ("longitude", -181),
        ("odometerKm", float("nan")),
        ("bridgeUpdatedAt", None),
        ("vehicleUpdatedAt", "2026-10-06T20:40:00"),
        ("vin", "wrong"),
    ],
)
def test_invalid_state_rejected(manifest, vehicle_state, field, value):
    _, _, vehicles = parse_manifest(json.dumps(manifest))
    with pytest.raises(ValueError):
        parse_state(json.dumps({**vehicle_state, field: value}), vehicles["example-ev"])


@pytest.mark.parametrize("mutation", ["version", "duplicate", "topic", "bounds"])
def test_invalid_manifest_rejected(manifest, mutation):
    if mutation == "version":
        manifest["schemaVersion"] = 2
    elif mutation == "duplicate":
        manifest["vehicles"].append(manifest["vehicles"][0])
    elif mutation == "topic":
        manifest["vehicles"][0]["vehicleId"] = "bad/#"
    else:
        manifest["vehicles"][0]["capabilities"]["chargeLimits"]["stepPercent"] = 0
    with pytest.raises(ValueError):
        parse_manifest(json.dumps(manifest))


def test_invalid_availability_and_result_rejected():
    with pytest.raises(ValueError):
        parse_availability('{"apiReachable": "true"}')
    with pytest.raises(ValueError):
        parse_result('{"commandId": "id", "command": "lock", "status": "submitted"}')
