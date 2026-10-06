"""Native AC/DC limits; bounds come solely from normalized capabilities."""

import math

from homeassistant.components.number import NumberEntity, NumberMode
from homeassistant.const import PERCENTAGE
from homeassistant.exceptions import HomeAssistantError

from .entity import BridgeEntity, setup_entities


async def async_setup_entry(hass, entry, async_add_entities):
    setup_entities(hass, entry, async_add_entities, vehicle_entities)


def vehicle_entities(runtime, info):
    if (
        {"acChargeLimitPercent", "dcChargeLimitPercent"} <= info.state_fields
        and "charge-limit" in info.commands
        and info.charge_limits is not None
    ):
        return [ChargeLimit(runtime, info, "ac"), ChargeLimit(runtime, info, "dc")]
    return []


class ChargeLimit(BridgeEntity, NumberEntity):
    _attr_native_unit_of_measurement = PERCENTAGE
    _attr_mode = NumberMode.SLIDER
    _attr_icon = "mdi:battery-charging"

    def __init__(self, runtime, info, kind):
        super().__init__(
            runtime,
            info,
            f"{kind}_charge_limit",
            ("acChargeLimitPercent", "dcChargeLimitPercent"),
            ("charge-limit",),
        )
        self.kind = kind
        self._attr_translation_key = f"{kind}_charge_limit"

    @property
    def available(self):
        return (
            super().available
            and self.info.charge_limits is not None
            and self.state_data.get("acChargeLimitPercent") is not None
            and self.state_data.get("dcChargeLimitPercent") is not None
        )

    @property
    def native_min_value(self):
        return (self.info.charge_limits or self.initial_info.charge_limits)[
            "minPercent"
        ]

    @property
    def native_max_value(self):
        return (self.info.charge_limits or self.initial_info.charge_limits)[
            "maxPercent"
        ]

    @property
    def native_step(self):
        return (self.info.charge_limits or self.initial_info.charge_limits)[
            "stepPercent"
        ]

    @property
    def native_value(self):
        return self.state_data.get(f"{self.kind}ChargeLimitPercent")

    async def async_set_native_value(self, value):
        if (
            not math.isfinite(value)
            or not self.native_min_value <= value <= self.native_max_value
            or not math.isclose(
                (value - self.native_min_value) / self.native_step,
                round((value - self.native_min_value) / self.native_step),
            )
        ):
            raise HomeAssistantError(
                "Charge limit is outside this vehicle's supported values"
            )
        ac, dc = (
            self.state_data.get("acChargeLimitPercent"),
            self.state_data.get("dcChargeLimitPercent"),
        )
        if ac is None or dc is None:
            raise HomeAssistantError("Both observed charge limits are required")
        await self.runtime.async_command(
            self.vehicle_id,
            "charge-limit",
            {
                "acPercent": int(value) if self.kind == "ac" else ac,
                "dcPercent": int(value) if self.kind == "dc" else dc,
            },
        )
