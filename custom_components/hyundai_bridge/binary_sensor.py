"""Native charging, plug and opening-status entities."""

from homeassistant.components.binary_sensor import (
    BinarySensorDeviceClass,
    BinarySensorEntity,
    BinarySensorEntityDescription,
)

from .entity import BridgeEntity, setup_entities

DESCRIPTIONS = (
    BinarySensorEntityDescription(
        key="isChargePortOpen",
        translation_key="charge_port",
        device_class=BinarySensorDeviceClass.OPENING,
    ),
    BinarySensorEntityDescription(
        key="isSunroofOpen",
        translation_key="sunroof",
        device_class=BinarySensorDeviceClass.WINDOW,
    ),
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
    BinarySensorEntityDescription(
        key="isFrontLeftDoorOpen",
        translation_key="front_left_door",
        device_class=BinarySensorDeviceClass.DOOR,
    ),
    BinarySensorEntityDescription(
        key="isFrontRightDoorOpen",
        translation_key="front_right_door",
        device_class=BinarySensorDeviceClass.DOOR,
    ),
    BinarySensorEntityDescription(
        key="isRearLeftDoorOpen",
        translation_key="rear_left_door",
        device_class=BinarySensorDeviceClass.DOOR,
    ),
    BinarySensorEntityDescription(
        key="isRearRightDoorOpen",
        translation_key="rear_right_door",
        device_class=BinarySensorDeviceClass.DOOR,
    ),
    BinarySensorEntityDescription(
        key="isTrunkOpen",
        translation_key="trunk",
        device_class=BinarySensorDeviceClass.OPENING,
    ),
    BinarySensorEntityDescription(
        key="isHoodOpen",
        translation_key="hood",
        device_class=BinarySensorDeviceClass.OPENING,
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
