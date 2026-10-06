"""Shared native entity identity, availability and manifest updates."""

from homeassistant.core import callback
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers.dispatcher import async_dispatcher_connect
from homeassistant.helpers.entity import DeviceInfo, Entity

from .const import DOMAIN
from .protocol import VehicleInfo
from .runtime import BridgeRuntime


class BridgeEntity(Entity):
    _attr_should_poll = False
    _attr_has_entity_name = True

    def __init__(
        self,
        runtime: BridgeRuntime,
        info: VehicleInfo,
        key: str,
        fields: tuple[str, ...] = (),
        commands: tuple[str, ...] = (),
    ) -> None:
        self.runtime = runtime
        self.initial_info = info
        self.vehicle_id = info.vehicle_id
        self.fields = fields
        self.commands = commands
        self._attr_unique_id = f"{info.vin}_{key}"

    @property
    def info(self) -> VehicleInfo:
        return self.runtime.vehicles.get(self.vehicle_id, self.initial_info)

    @property
    def state_data(self) -> dict:
        return self.runtime.states.get(self.vehicle_id, {})

    @property
    def available(self) -> bool:
        return (
            self.runtime.connected
            and self.runtime.online
            and self.vehicle_id in self.runtime.vehicles
            and all(field in self.info.state_fields for field in self.fields)
            and all(command in self.info.commands for command in self.commands)
            and (not self.fields or self.vehicle_id in self.runtime.states)
        )

    @property
    def device_info(self) -> DeviceInfo:
        bridge = dr.async_get(self.runtime.hass).async_get_device_by_identifier(
            (DOMAIN, f"bridge:{self.runtime.bridge_id}"), self.runtime.entry_id
        )
        return DeviceInfo(
            identifiers={(DOMAIN, self.info.vin)},
            manufacturer="Hyundai",
            model=self.info.model,
            name=self.info.name or self.info.model or self.info.vin,
            serial_number=self.info.vin,
            via_device_id=bridge.id if bridge else None,
        )

    @property
    def extra_state_attributes(self) -> dict:
        status = self.runtime.availability.get(self.vehicle_id, {})
        command = self.runtime.last_commands.get(self.vehicle_id)
        return {
            "bridge_online": self.runtime.connected and self.runtime.online,
            "api_reachable": status.get("apiReachable"),
            "data_freshness": status.get("dataFreshness", "unknown"),
            "vehicle_updated_at": self.state_data.get("vehicleUpdatedAt"),
            "bridge_updated_at": self.state_data.get("bridgeUpdatedAt"),
            "last_command": dict(command) if command else None,
        }

    async def async_added_to_hass(self) -> None:
        await super().async_added_to_hass()
        self.async_on_remove(
            async_dispatcher_connect(self.hass, self.runtime.signal, self._updated)
        )

    @callback
    def _updated(self) -> None:
        self.async_write_ha_state()


@callback
def setup_entities(hass, entry, async_add_entities, vehicle_entities) -> None:
    """Each platform adds newly advertised entities while preserving registry ids."""
    known = set()

    @callback
    def add_new() -> None:
        new = []
        for info in entry.runtime_data.vehicles.values():
            for entity in vehicle_entities(entry.runtime_data, info):
                if entity.unique_id not in known:
                    known.add(entity.unique_id)
                    new.append(entity)
        if new:
            async_add_entities(new)

    entry.async_on_unload(
        async_dispatcher_connect(hass, entry.runtime_data.signal, add_new)
    )
    add_new()
