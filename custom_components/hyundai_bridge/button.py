"""Native buttons for explicit bridge-supported vehicle actions."""

from homeassistant.components.button import ButtonEntity, ButtonEntityDescription

from .entity import BridgeEntity, setup_entities

DESCRIPTIONS = (
    ButtonEntityDescription(
        key="refresh", translation_key="refresh", icon="mdi:refresh"
    ),
    ButtonEntityDescription(
        key="charging/start", translation_key="start_charging", icon="mdi:ev-station"
    ),
    ButtonEntityDescription(
        key="charging/stop", translation_key="stop_charging", icon="mdi:stop"
    ),
)


async def async_setup_entry(hass, entry, async_add_entities):
    setup_entities(hass, entry, async_add_entities, vehicle_entities)


def vehicle_entities(runtime, info):
    return [
        VehicleButton(runtime, info, description)
        for description in DESCRIPTIONS
        if description.key in info.commands
    ]


class VehicleButton(BridgeEntity, ButtonEntity):
    def __init__(self, runtime, info, description):
        super().__init__(runtime, info, description.key, commands=(description.key,))
        self.entity_description = description

    async def async_press(self):
        await self.runtime.async_command(self.vehicle_id, self.entity_description.key)
