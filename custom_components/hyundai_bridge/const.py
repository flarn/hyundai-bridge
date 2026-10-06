"""Normalized transport constants; no Hyundai API knowledge."""

DOMAIN = "hyundai_bridge"
PREFIX = "hyundai/v1"
CONF_BRIDGE_ID = "bridge_id"
MANIFEST_TIMEOUT = 10
COMMAND_TIMEOUT = 120
PLATFORMS = [
    "sensor",
    "binary_sensor",
    "lock",
    "climate",
    "number",
    "button",
    "device_tracker",
]
COMMANDS = {
    "refresh",
    "lock",
    "unlock",
    "climate/start",
    "climate/stop",
    "charging/start",
    "charging/stop",
    "charge-limit",
}
