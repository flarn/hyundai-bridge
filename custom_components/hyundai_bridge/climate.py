"""Remote temperature control, not invented real-time HVAC telemetry."""

import math

from homeassistant.components.climate import (
    ClimateEntity,
    ClimateEntityFeature,
    HVACMode,
)
from homeassistant.const import ATTR_TEMPERATURE, UnitOfTemperature
from homeassistant.exceptions import HomeAssistantError

from .entity import BridgeEntity, setup_entities


async def async_setup_entry(hass, entry, async_add_entities):
    setup_entities(hass, entry, async_add_entities, vehicle_entities)


def vehicle_entities(runtime, info):
    if (
        {"isClimateOn", "targetTemperatureCelsius"} <= info.state_fields
        and {"climate/start", "climate/stop"} <= info.commands
        and info.climate is not None
    ):
        return [VehicleClimate(runtime, info)]
    return []


class VehicleClimate(BridgeEntity, ClimateEntity):
    _attr_name = None
    _attr_temperature_unit = UnitOfTemperature.CELSIUS
    _attr_hvac_modes = [HVACMode.OFF, HVACMode.HEAT_COOL]

    def __init__(self, runtime, info):
        super().__init__(
            runtime,
            info,
            "climate",
            ("isClimateOn", "targetTemperatureCelsius"),
            ("climate/start", "climate/stop"),
        )

    @property
    def available(self):
        return super().available and self.info.climate is not None

    @property
    def climate_bounds(self):
        return self.info.climate or self.initial_info.climate

    @property
    def min_temp(self):
        return self.climate_bounds["minTemperatureCelsius"]

    @property
    def max_temp(self):
        return self.climate_bounds["maxTemperatureCelsius"]

    @property
    def target_temperature_step(self):
        return self.climate_bounds["temperatureStepCelsius"]

    @property
    def supports_defrost(self):
        return (
            self.climate_bounds.get("supportsDefrost", False)
            and "isDefrostOn" in self.info.state_fields
        )

    @property
    def supported_features(self):
        features = (
            ClimateEntityFeature.TARGET_TEMPERATURE
            | ClimateEntityFeature.TURN_ON
            | ClimateEntityFeature.TURN_OFF
        )
        return (
            features | ClimateEntityFeature.PRESET_MODE
            if self.supports_defrost
            else features
        )

    @property
    def hvac_mode(self):
        enabled = self.state_data.get("isClimateOn")
        return (
            None if enabled is None else HVACMode.HEAT_COOL if enabled else HVACMode.OFF
        )

    @property
    def target_temperature(self):
        return self.state_data.get("targetTemperatureCelsius")

    @property
    def current_temperature(self):
        return (
            self.state_data.get("cabinTemperatureCelsius")
            if "cabinTemperatureCelsius" in self.info.state_fields
            else None
        )

    @property
    def preset_modes(self):
        return ["none", "defrost"] if self.supports_defrost else None

    @property
    def preset_mode(self):
        defrost = self.state_data.get("isDefrostOn")
        return (
            None
            if not self.supports_defrost or defrost is None
            else "defrost"
            if defrost
            else "none"
        )

    async def _start(self, temperature=None, defrost=None):
        temperature = self.target_temperature if temperature is None else temperature
        if temperature is None:
            raise HomeAssistantError(
                "An observed target temperature is required before turning climate on"
            )
        if (
            not math.isfinite(temperature)
            or not self.min_temp <= temperature <= self.max_temp
            or not math.isclose(
                (temperature - self.min_temp) / self.target_temperature_step,
                round((temperature - self.min_temp) / self.target_temperature_step),
            )
        ):
            raise HomeAssistantError(
                "Temperature is outside this vehicle's supported values"
            )
        arguments = {"temperatureCelsius": temperature}
        if self.supports_defrost:
            defrost = self.state_data.get("isDefrostOn") if defrost is None else defrost
            if defrost is None:
                raise HomeAssistantError(
                    "Observed defrost state is required to preserve the climate preset"
                )
            arguments["defrost"] = defrost
        await self.runtime.async_command(self.vehicle_id, "climate/start", arguments)

    async def async_set_temperature(self, **kwargs):
        if kwargs.get("hvac_mode", HVACMode.HEAT_COOL) != HVACMode.HEAT_COOL:
            raise HomeAssistantError("Set temperature requires remote climate on mode")
        if ATTR_TEMPERATURE not in kwargs:
            raise HomeAssistantError("A target temperature is required")
        await self._start(kwargs[ATTR_TEMPERATURE])

    async def async_set_hvac_mode(self, hvac_mode):
        if hvac_mode == HVACMode.OFF:
            await self.async_turn_off()
        elif hvac_mode == HVACMode.HEAT_COOL:
            await self.async_turn_on()
        else:
            raise HomeAssistantError("This vehicle supports remote climate on/off only")

    async def async_turn_on(self):
        await self._start()

    async def async_turn_off(self):
        await self.runtime.async_command(self.vehicle_id, "climate/stop")

    async def async_set_preset_mode(self, preset_mode):
        if not self.supports_defrost or preset_mode not in ("none", "defrost"):
            raise HomeAssistantError("Unsupported climate preset")
        await self._start(defrost=preset_mode == "defrost")
