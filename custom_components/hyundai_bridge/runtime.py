"""One MQTT transport connection per configured bridge."""

import asyncio
import json
import logging
from uuid import uuid4

from homeassistant.components import mqtt
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.dispatcher import async_dispatcher_send

from .const import COMMAND_TIMEOUT, COMMANDS, DOMAIN, PREFIX
from .protocol import (
    VehicleInfo,
    parse_availability,
    parse_manifest,
    parse_result,
    parse_state,
)

_LOGGER = logging.getLogger(__name__)


class BridgeRuntime:
    """Vendor-independent state, subscriptions and command completion."""

    def __init__(self, hass: HomeAssistant, bridge_id: str, entry_id: str) -> None:
        self.hass = hass
        self.entry_id = entry_id
        self.bridge_id = bridge_id
        self.name = "Hyundai Bridge"
        self.signal = f"{DOMAIN}_{bridge_id}"
        self.vehicles: dict[str, VehicleInfo] = {}
        self.states: dict[str, dict] = {}
        self.availability: dict[str, dict] = {}
        self.last_commands: dict[str, dict] = {}
        self.online = False
        self.connected = mqtt.is_connected(hass)
        self._identities: dict[str, str] = {}
        self._unsubscribes: list = []
        self._vehicle_subscriptions: dict[str, list] = {}
        self._stopped = False
        self._pending: dict[str, tuple[str, str, asyncio.Future]] = {}

    async def async_start(self) -> None:
        self._unsubscribes.append(
            mqtt.async_subscribe_connection_status(self.hass, self._connection_changed)
        )
        for topic, handler in (
            (f"{PREFIX}/bridges/{self.bridge_id}/manifest", self._manifest),
            (
                f"{PREFIX}/bridges/{self.bridge_id}/availability",
                self._bridge_availability,
            ),
        ):
            self._unsubscribes.append(
                await mqtt.async_subscribe(self.hass, topic, handler, qos=1)
            )

    @callback
    def async_stop(self) -> None:
        self._stopped = True
        for unsubscribe in self._unsubscribes:
            unsubscribe()
        self._unsubscribes.clear()
        for subscriptions in self._vehicle_subscriptions.values():
            for unsubscribe in subscriptions:
                unsubscribe()
        self._vehicle_subscriptions.clear()
        self._cancel_commands()

    @callback
    def _notify(self) -> None:
        async_dispatcher_send(self.hass, self.signal)

    @callback
    def _connection_changed(self, connected: bool) -> None:
        self.connected = connected
        if not connected:
            self.online = False
            self._cancel_commands()
        self._notify()

    @callback
    def _cancel_commands(self) -> None:
        for vehicle_id, command, future in self._pending.values():
            if not future.done():
                future.set_exception(
                    HomeAssistantError(
                        "Bridge connection lost; command outcome is unknown"
                    )
                )
                result = self.last_commands.get(vehicle_id)
                if result and result["command"] == command:
                    result["status"] = "unknown"

    async def _manifest(self, message) -> None:
        if self._stopped:
            return
        try:
            bridge_id, name, vehicles = parse_manifest(message.payload)
            if bridge_id != self.bridge_id:
                raise ValueError("Mismatched bridge")
            if any(
                self._identities.get(key, info.vin) != info.vin
                for key, info in vehicles.items()
            ):
                raise ValueError("Vehicle identity reassigned")
            identities = {
                **self._identities,
                **{key: info.vin for key, info in vehicles.items()},
            }
            if len(identities) != len(set(identities.values())):
                raise ValueError("VIN assigned to another vehicle id")
        except (ValueError, TypeError):
            _LOGGER.warning("Ignoring invalid bridge manifest")
            return
        self.name, self.vehicles = name, vehicles
        self._identities.update({key: info.vin for key, info in vehicles.items()})
        for vehicle_id in self._vehicle_subscriptions.keys() - vehicles.keys():
            for unsubscribe in self._vehicle_subscriptions.pop(vehicle_id):
                unsubscribe()
        # Subscribe only after knowing identity; retained state cannot be lost
        # just because it arrived before the retained manifest at startup.
        for vehicle_id in vehicles.keys() - self._vehicle_subscriptions.keys():
            subscriptions = self._vehicle_subscriptions[vehicle_id] = []
            for suffix, handler in (
                ("state", self._state),
                ("availability", self._vehicle_availability),
                ("command-result", self._command_result),
            ):
                unsubscribe = await mqtt.async_subscribe(
                    self.hass, f"{PREFIX}/{vehicle_id}/{suffix}", handler, qos=1
                )
                if self._stopped:
                    unsubscribe()
                    return
                subscriptions.append(unsubscribe)
        self._notify()

    @callback
    def _bridge_availability(self, message) -> None:
        if message.payload not in ("online", "offline"):
            _LOGGER.warning("Ignoring invalid bridge availability")
            return
        self.online = message.payload == "online"
        if not self.online:
            self._cancel_commands()
        self._notify()

    def _vehicle_id(self, message) -> str | None:
        parts = message.topic.split("/")
        return parts[2] if len(parts) == 4 and parts[2] in self.vehicles else None

    @callback
    def _state(self, message) -> None:
        if (vehicle_id := self._vehicle_id(message)) is None:
            return
        try:
            self.states[vehicle_id] = parse_state(
                message.payload, self.vehicles[vehicle_id]
            )
        except (ValueError, TypeError):
            _LOGGER.warning("Ignoring invalid vehicle state")
            return
        self._notify()

    @callback
    def _vehicle_availability(self, message) -> None:
        if (vehicle_id := self._vehicle_id(message)) is None:
            return
        try:
            self.availability[vehicle_id] = parse_availability(message.payload)
        except (ValueError, TypeError):
            _LOGGER.warning("Ignoring invalid vehicle availability")
            return
        self._notify()

    @callback
    def _command_result(self, message) -> None:
        if (vehicle_id := self._vehicle_id(message)) is None or message.retain:
            return
        try:
            result = parse_result(message.payload)
        except (ValueError, TypeError):
            _LOGGER.warning("Ignoring invalid command result")
            return
        pending = self._pending.get(result["commandId"])
        previous = self.last_commands.get(vehicle_id)
        if pending is not None:
            expected_vehicle, command, future = pending
            if expected_vehicle != vehicle_id or command != result["command"]:
                return
            if not future.done() and result["status"] != "accepted":
                future.set_result(result)
        elif (
            previous is None
            or previous["commandId"] != result["commandId"]
            or previous["command"] != result["command"]
        ):
            return
        if (
            previous
            and previous["commandId"] == result["commandId"]
            and previous["status"] in ("completed", "failed")
        ):
            return
        self.last_commands[vehicle_id] = result
        self._notify()

    async def async_command(
        self, vehicle_id: str, command: str, arguments: dict | None = None
    ) -> None:
        info = self.vehicles.get(vehicle_id)
        if not self.connected or not self.online or info is None:
            raise HomeAssistantError("Hyundai Bridge is unavailable")
        if command not in COMMANDS or command not in info.commands:
            raise HomeAssistantError(
                "This vehicle does not support the requested command"
            )
        if any(item[0] == vehicle_id for item in self._pending.values()):
            raise HomeAssistantError("A vehicle command is already in progress")
        command_id = str(uuid4())
        future = self.hass.loop.create_future()
        self._pending[command_id] = (vehicle_id, command, future)
        self.last_commands[vehicle_id] = {
            "commandId": command_id,
            "command": command,
            "status": "submitted",
            "message": None,
        }
        self._notify()
        try:
            async with asyncio.timeout(COMMAND_TIMEOUT):
                await mqtt.async_publish(
                    self.hass,
                    f"{PREFIX}/{vehicle_id}/command/{command}",
                    json.dumps(
                        {**(arguments or {}), "commandId": command_id}, allow_nan=False
                    ),
                    qos=1,
                    retain=False,
                )
                result = await future
            if result["status"] == "failed":
                raise HomeAssistantError(
                    result.get("message") or "Vehicle command failed"
                )
        except TimeoutError as error:
            self.last_commands[vehicle_id]["status"] = "unknown"
            raise HomeAssistantError(
                "No completion received from the bridge; command outcome is unknown"
            ) from error
        except asyncio.CancelledError:
            self.last_commands[vehicle_id]["status"] = "unknown"
            raise
        except HomeAssistantError:
            if self.last_commands[vehicle_id]["status"] != "failed":
                self.last_commands[vehicle_id]["status"] = "unknown"
            raise
        finally:
            self._pending.pop(command_id, None)
            if not future.done():
                future.cancel()
            # Retrieve any exception set during a simultaneous publish failure/unload.
            if future.done() and not future.cancelled():
                future.exception()
            self._notify()
