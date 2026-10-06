"""Native GPS tracker; no geocoding or location polling."""

from homeassistant.components.device_tracker.entity import TrackerEntity

from .entity import BridgeEntity, setup_entities


async def async_setup_entry(hass, entry, async_add_entities):
    setup_entities(hass, entry, async_add_entities, vehicle_entities)


def vehicle_entities(runtime, info):
    if {"latitude", "longitude"} <= info.state_fields:
        return [VehicleTracker(runtime, info)]
    return []


class VehicleTracker(BridgeEntity, TrackerEntity):
    _attr_translation_key = "location"

    def __init__(self, runtime, info):
        super().__init__(runtime, info, "location", ("latitude", "longitude"))

    @property
    def available(self):
        return (
            super().available
            and self.latitude is not None
            and self.longitude is not None
        )

    @property
    def latitude(self):
        return self.state_data.get("latitude")

    @property
    def longitude(self):
        return self.state_data.get("longitude")
