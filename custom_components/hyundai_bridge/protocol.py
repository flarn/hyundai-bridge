"""Validate the versioned, vendor-independent bridge contract."""

import json
import math
import re
from dataclasses import dataclass
from datetime import datetime
from typing import Any

TOPIC_ID = re.compile(r"^[A-Za-z0-9_-]+$")
BOOLEAN_FIELDS = {
    "isChargePortOpen",
    "isSunroofOpen",
    "isCharging",
    "isPluggedIn",
    "isLocked",
    "isClimateOn",
    "isDefrostOn",
    "isFrontLeftDoorOpen",
    "isFrontRightDoorOpen",
    "isRearLeftDoorOpen",
    "isRearRightDoorOpen",
    "isTrunkOpen",
    "isHoodOpen",
}
PERCENT_FIELDS = {
    "batteryPercent",
    "auxiliaryBatteryPercent",
    "acChargeLimitPercent",
    "dcChargeLimitPercent",
}
NUMBER_FIELDS = {
    "chargingPowerKw",
    "remainingChargeTimeMinutes",
    "estimatedRangeKm",
    "odometerKm",
    "cabinTemperatureCelsius",
    "outsideTemperatureCelsius",
    "targetTemperatureCelsius",
    "latitude",
    "longitude",
}


@dataclass(frozen=True)
class VehicleInfo:
    """Only normalized metadata crosses this boundary."""

    vehicle_id: str
    vin: str
    name: str | None
    model: str | None
    state_fields: frozenset[str]
    commands: frozenset[str]
    climate: dict[str, Any] | None
    charge_limits: dict[str, Any] | None


def document(payload: str | bytes) -> dict[str, Any]:
    value = json.loads(payload)
    if not isinstance(value, dict):
        raise ValueError("Expected a JSON object")
    return value


def text(value: Any) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError("Expected nonempty text")
    return value


def topic_id(value: Any) -> str:
    value = text(value)
    if not TOPIC_ID.fullmatch(value):
        raise ValueError("Invalid topic identifier")
    return value


def number(value: Any) -> int | float:
    if (
        isinstance(value, bool)
        or not isinstance(value, (int, float))
        or not math.isfinite(value)
    ):
        raise ValueError("Expected a finite number")
    return value


def timestamp(value: Any) -> str | None:
    if value is None:
        return None
    date = datetime.fromisoformat(text(value))
    if date.tzinfo is None:
        raise ValueError("Timestamp requires timezone")
    return value


def strings(value: Any) -> frozenset[str]:
    if not isinstance(value, list):
        raise ValueError("Expected a list of names")
    return frozenset(text(item) for item in value)


def bounds(value: Any, minimum: str, maximum: str, step: str) -> dict[str, Any] | None:
    if value is None:
        return None
    if not isinstance(value, dict):
        raise ValueError("Expected capability bounds")
    low, high, increment = (number(value.get(key)) for key in (minimum, maximum, step))
    if low >= high or increment <= 0 or increment > high - low:
        raise ValueError("Invalid capability bounds")
    return value


def parse_manifest(payload: str | bytes) -> tuple[str, str, dict[str, VehicleInfo]]:
    data = document(payload)
    if type(data.get("schemaVersion")) is not int or data["schemaVersion"] != 1:
        raise ValueError("Unsupported contract version")
    bridge_id = topic_id(data.get("bridgeId"))
    name = text(data.get("name"))
    if not isinstance(data.get("vehicles"), list):
        raise ValueError("Expected vehicle list")
    vehicles: dict[str, VehicleInfo] = {}
    vins: set[str] = set()
    for entry in data["vehicles"]:
        if not isinstance(entry, dict) or not isinstance(
            entry.get("capabilities"), dict
        ):
            raise ValueError("Invalid vehicle metadata")
        vehicle_id = topic_id(entry.get("vehicleId"))
        vin = text(entry.get("vin"))
        if vehicle_id in vehicles or vin in vins:
            raise ValueError("Duplicate vehicle identity")
        caps = entry["capabilities"]
        climate = bounds(
            caps.get("climate"),
            "minTemperatureCelsius",
            "maxTemperatureCelsius",
            "temperatureStepCelsius",
        )
        if (
            climate is not None
            and type(climate.get("supportsDefrost", False)) is not bool
        ):
            raise ValueError("Invalid defrost capability")
        limits = bounds(
            caps.get("chargeLimits"), "minPercent", "maxPercent", "stepPercent"
        )
        if limits is not None and (
            any(
                type(limits[key]) is not int
                for key in ("minPercent", "maxPercent", "stepPercent")
            )
            or not 0 <= limits["minPercent"] < limits["maxPercent"] <= 100
        ):
            raise ValueError("Invalid charge-limit capability")
        vehicles[vehicle_id] = VehicleInfo(
            vehicle_id,
            vin,
            text(entry["name"]) if entry.get("name") is not None else None,
            text(entry["model"]) if entry.get("model") is not None else None,
            strings(caps.get("stateFields")),
            strings(caps.get("commands")),
            climate,
            limits,
        )
        vins.add(vin)
    return bridge_id, name, vehicles


def parse_state(payload: str | bytes, vehicle: VehicleInfo) -> dict[str, Any]:
    data = document(payload)
    if data.get("vehicleId") != vehicle.vehicle_id or data.get("vin") != vehicle.vin:
        raise ValueError("State identity does not match manifest")
    for field in BOOLEAN_FIELDS:
        if data.get(field) is not None and type(data[field]) is not bool:
            raise ValueError("Invalid boolean state")
    for field in PERCENT_FIELDS:
        if data.get(field) is not None and (
            type(data[field]) is not int or not 0 <= data[field] <= 100
        ):
            raise ValueError("Invalid percentage state")
    for field in NUMBER_FIELDS:
        if data.get(field) is not None:
            number(data[field])
    for field in ("odometerKm", "estimatedRangeKm"):
        if data.get(field) is not None and data[field] < 0:
            raise ValueError("Invalid distance state")
    for field in ("chargingPowerKw", "remainingChargeTimeMinutes"):
        if data.get(field) is not None and data[field] < 0:
            raise ValueError("Invalid charging measurement")
    for field, maximum in (("latitude", 90), ("longitude", 180)):
        if data.get(field) is not None and abs(data[field]) > maximum:
            raise ValueError("Invalid coordinates")
    timestamp(data.get("vehicleUpdatedAt"))
    if timestamp(data.get("bridgeUpdatedAt")) is None:
        raise ValueError("Missing bridge observation time")
    return data


def parse_availability(payload: str | bytes) -> dict[str, Any]:
    data = document(payload)
    reachable = data.get("apiReachable")
    if reachable is not None and type(reachable) is not bool:
        raise ValueError("Invalid API availability")
    freshness = data.get("dataFreshness", "unknown")
    if freshness not in ("unknown", "current", "stale"):
        raise ValueError("Invalid data freshness")
    return {"apiReachable": reachable, "dataFreshness": freshness}


def parse_result(payload: str | bytes) -> dict[str, Any]:
    data = document(payload)
    text(data.get("commandId"))
    text(data.get("command"))
    if data.get("status") not in ("accepted", "completed", "failed"):
        raise ValueError("Invalid command status")
    message = data.get("message")
    if message is not None and (not isinstance(message, str) or len(message) > 256):
        raise ValueError("Invalid command message")
    return {
        "commandId": data["commandId"],
        "command": data["command"],
        "status": data["status"],
        "message": message,
    }
