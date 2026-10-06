"""Native charging and plug-status entities."""

from homeassistant.components.binary_sensor import (
    BinarySensorDeviceClass,
    BinarySensorEntity,
    BinarySensorEntityDescription,
)

from .entity import BridgeEntity, setup_entities

DESCRIPTIONS = (
    BinarySensorEntityDescription(
        key="isCharging",
        translation_key="charging",
        device_class=BinarySensorDeviceClass.BATTERY_CHARGING,
    ),
    BinarySensorEntityDescription(
        key="isPluggedIn",
        translation_key="plugged_in",
        device_class=BinarySensorDeviceClass.PLUG,
    ),
)


async def async_setup_entry(hass, entry, async_add_entities):
    setup_entities(hass, entry, async_add_entities, vehicle_entities)


def vehicle_entities(runtime, info):
    return [
        VehicleBinarySensor(runtime, info, description)
        for description in DESCRIPTIONS
        if description.key in info.state_fields
    ]


class VehicleBinarySensor(BridgeEntity, BinarySensorEntity):
    def __init__(self, runtime, info, description):
        super().__init__(runtime, info, description.key, (description.key,))
        self.entity_description = description

    @property
    def is_on(self):
        return self.state_data.get(self.entity_description.key)
