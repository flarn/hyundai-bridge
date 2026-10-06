"""Native Home Assistant integration over a normalized bridge transport."""

from homeassistant.components import mqtt
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryNotReady
from homeassistant.helpers import device_registry as dr

from .const import CONF_BRIDGE_ID, DOMAIN, PLATFORMS
from .runtime import BridgeRuntime

type HyundaiConfigEntry = ConfigEntry[BridgeRuntime]


async def async_setup_entry(hass: HomeAssistant, entry: HyundaiConfigEntry) -> bool:
    if not await mqtt.async_wait_for_mqtt_client(hass):
        raise ConfigEntryNotReady("Home Assistant MQTT connection is not configured")
    runtime = BridgeRuntime(hass, entry.data[CONF_BRIDGE_ID], entry.entry_id)
    entry.runtime_data = runtime
    try:
        await runtime.async_start()
        entry.async_on_unload(runtime.async_stop)
        dr.async_get(hass).async_get_or_create(
            config_entry_id=entry.entry_id,
            identifiers={(DOMAIN, f"bridge:{runtime.bridge_id}")},
            name=entry.title,
            manufacturer="Hyundai Bridge",
            model=".NET Bridge",
        )
        await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    except Exception:
        runtime.async_stop()
        raise
    return True


async def async_unload_entry(hass: HomeAssistant, entry: HyundaiConfigEntry) -> bool:
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
