"""Configure a bridge, using Home Assistant's existing MQTT connection."""

import asyncio

import voluptuous as vol
from homeassistant.components import mqtt
from homeassistant.config_entries import ConfigFlow
from homeassistant.core import callback
from homeassistant.helpers.service_info.mqtt import MqttServiceInfo

from .const import CONF_BRIDGE_ID, DOMAIN, MANIFEST_TIMEOUT, PREFIX
from .protocol import parse_manifest, topic_id


class HyundaiBridgeConfigFlow(ConfigFlow, domain=DOMAIN):
    VERSION = 1

    def __init__(self) -> None:
        self._bridge_id: str | None = None
        self._name: str | None = None

    async def async_step_mqtt(self, discovery_info: MqttServiceInfo):
        try:
            bridge_id, name, _ = parse_manifest(discovery_info.payload)
            if discovery_info.topic != f"{PREFIX}/bridges/{bridge_id}/manifest":
                raise ValueError("Manifest topic mismatch")
        except (ValueError, TypeError):
            return self.async_abort(reason="invalid_manifest")
        await self.async_set_unique_id(bridge_id)
        self._abort_if_unique_id_configured()
        self._bridge_id, self._name = bridge_id, name
        return await self.async_step_confirm()

    async def async_step_confirm(self, user_input=None):
        if user_input is not None:
            return self.async_create_entry(
                title=self._name, data={CONF_BRIDGE_ID: self._bridge_id}
            )
        return self.async_show_form(
            step_id="confirm", description_placeholders={"name": self._name}
        )

    async def async_step_user(self, user_input=None):
        errors = {}
        if user_input is not None:
            try:
                bridge_id = topic_id(user_input[CONF_BRIDGE_ID])
            except (ValueError, TypeError):
                errors[CONF_BRIDGE_ID] = "invalid_bridge_id"
            else:
                await self.async_set_unique_id(bridge_id)
                self._abort_if_unique_id_configured()
                if not await mqtt.async_wait_for_mqtt_client(self.hass):
                    errors["base"] = "mqtt_unavailable"
                else:
                    received = self.hass.loop.create_future()

                    @callback
                    def manifest_received(message):
                        try:
                            found_id, name, _ = parse_manifest(message.payload)
                            if found_id == bridge_id and not received.done():
                                received.set_result(name)
                        except (ValueError, TypeError):
                            return

                    unsubscribe = await mqtt.async_subscribe(
                        self.hass,
                        f"{PREFIX}/bridges/{bridge_id}/manifest",
                        manifest_received,
                        qos=1,
                    )
                    try:
                        async with asyncio.timeout(MANIFEST_TIMEOUT):
                            name = await received
                    except TimeoutError:
                        errors["base"] = "bridge_unavailable"
                    else:
                        return self.async_create_entry(
                            title=name, data={CONF_BRIDGE_ID: bridge_id}
                        )
                    finally:
                        unsubscribe()
        return self.async_show_form(
            step_id="user",
            errors=errors,
            data_schema=vol.Schema({vol.Required(CONF_BRIDGE_ID, default="home"): str}),
        )
