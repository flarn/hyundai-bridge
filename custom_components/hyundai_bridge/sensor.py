"""Native sensors backed by advertised normalized measurements."""

from homeassistant.components.sensor import (
    SensorDeviceClass,
    SensorEntity,
    SensorEntityDescription,
    SensorStateClass,
)
from homeassistant.const import PERCENTAGE, UnitOfLength, UnitOfTemperature

from .entity import BridgeEntity, setup_entities

DESCRIPTIONS = (
    SensorEntityDescription(
        key="batteryPercent",
        translation_key="battery",
        device_class=SensorDeviceClass.BATTERY,
        native_unit_of_measurement=PERCENTAGE,
        state_class=SensorStateClass.MEASUREMENT,
    ),
    SensorEntityDescription(
        key="estimatedRangeKm",
        translation_key="range",
        device_class=SensorDeviceClass.DISTANCE,
        native_unit_of_measurement=UnitOfLength.KILOMETERS,
        state_class=SensorStateClass.MEASUREMENT,
    ),
    SensorEntityDescription(
        key="odometerKm",
        translation_key="odometer",
        device_class=SensorDeviceClass.DISTANCE,
        native_unit_of_measurement=UnitOfLength.KILOMETERS,
        state_class=SensorStateClass.TOTAL_INCREASING,
    ),
    SensorEntityDescription(
        key="outsideTemperatureCelsius",
        translation_key="outside_temperature",
        device_class=SensorDeviceClass.TEMPERATURE,
        native_unit_of_measurement=UnitOfTemperature.CELSIUS,
        state_class=SensorStateClass.MEASUREMENT,
    ),
    SensorEntityDescription(
        key="cabinTemperatureCelsius",
        translation_key="cabin_temperature",
        device_class=SensorDeviceClass.TEMPERATURE,
        native_unit_of_measurement=UnitOfTemperature.CELSIUS,
        state_class=SensorStateClass.MEASUREMENT,
    ),
)


async def async_setup_entry(hass, entry, async_add_entities):
    setup_entities(hass, entry, async_add_entities, vehicle_entities)


def vehicle_entities(runtime, info):
    return [
        VehicleSensor(runtime, info, description)
        for description in DESCRIPTIONS
        if description.key in info.state_fields
    ]


class VehicleSensor(BridgeEntity, SensorEntity):
    def __init__(self, runtime, info, description):
        super().__init__(runtime, info, description.key, (description.key,))
        self.entity_description = description

    @property
    def native_value(self):
        return self.state_data.get(self.entity_description.key)
