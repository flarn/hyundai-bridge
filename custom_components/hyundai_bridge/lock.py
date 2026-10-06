"""Native vehicle lock with observed state and correlated commands."""

from homeassistant.components.lock import LockEntity

from .entity import BridgeEntity, setup_entities


async def async_setup_entry(hass, entry, async_add_entities):
    setup_entities(hass, entry, async_add_entities, vehicle_entities)


def vehicle_entities(runtime, info):
    if "isLocked" in info.state_fields and {"lock", "unlock"} <= info.commands:
        return [VehicleLock(runtime, info)]
    return []


class VehicleLock(BridgeEntity, LockEntity):
    _attr_name = None

    def __init__(self, runtime, info):
        super().__init__(runtime, info, "lock", ("isLocked",), ("lock", "unlock"))

    @property
    def is_locked(self):
        return self.state_data.get("isLocked")

    async def async_lock(self, **kwargs):
        await self.runtime.async_command(self.vehicle_id, "lock")

    async def async_unlock(self, **kwargs):
        await self.runtime.async_command(self.vehicle_id, "unlock")
