"""Real .NET → Mosquitto → HA state and native command round trip.

The adapter lives in the .NET test assembly and uses synthetic observations.
"""

import asyncio
import os

import pytest
from homeassistant.helpers import entity_registry as er
from pytest_homeassistant_custom_component.common import MockConfigEntry

from .conftest import ROOT
from .test_broker import wait_until

pytestmark = pytest.mark.skipif(
    not os.environ.get("HYUNDAI_TEST_MQTT_PORT"),
    reason="Requires an isolated loopback broker and built .NET tests",
)


async def test_dotnet_state_and_native_unlock(
    hass, socket_enabled, mock_hass_config, tmp_path
):
    process = await asyncio.create_subprocess_exec(
        "dotnet",
        "test",
        "HyundaiBridge.slnx",
        "--configuration",
        "Release",
        "--no-build",
        "--no-restore",
        "--filter",
        "FullyQualifiedName~HomeAssistantRoundTrip",
        cwd=ROOT,
        env={**os.environ, "HYUNDAI_TEST_HA_SIGNALS": str(tmp_path)},
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
    )
    service = None
    try:
        await wait_until(lambda: (tmp_path / "ready").exists())
        mqtt_entry = MockConfigEntry(
            domain="mqtt",
            title="Test MQTT",
            version=1,
            minor_version=2,
            data={
                "broker": "127.0.0.1",
                "port": int(os.environ["HYUNDAI_TEST_MQTT_PORT"]),
                "protocol": "3.1.1",
            },
            options={"birth_message": {}},
        )
        mqtt_entry.add_to_hass(hass)
        assert await hass.config_entries.async_setup(mqtt_entry.entry_id)

        def flow():
            return next(
                (
                    x
                    for x in hass.config_entries.flow.async_progress()
                    if x["handler"] == "hyundai_bridge"
                ),
                None,
            )

        await wait_until(lambda: flow() is not None)
        result = await hass.config_entries.flow.async_configure(flow()["flow_id"], {})
        entry = result["result"]

        def state(platform, feature):
            entity_id = er.async_get(hass).async_get_entity_id(
                platform, "hyundai_bridge", f"KMH00000000000001_{feature}"
            )
            return hass.states.get(entity_id) if entity_id else None

        await wait_until(
            lambda: state("sensor", "batteryPercent") is not None
            and state("sensor", "batteryPercent").state == "73"
            and state("lock", "lock") is not None
            and state("lock", "lock").state == "locked"
        )
        service = asyncio.create_task(
            hass.services.async_call(
                "lock",
                "unlock",
                {"entity_id": state("lock", "lock").entity_id},
                blocking=True,
            )
        )
        await wait_until(
            lambda: entry.runtime_data.last_commands.get("example-ev", {}).get("status")
            == "accepted"
        )
        assert not service.done()
        assert state("lock", "lock").state == "locked"
        (tmp_path / "release").touch()
        await asyncio.wait_for(service, 5)
        await wait_until(lambda: state("lock", "lock").state == "unlocked")
        assert entry.runtime_data.last_commands["example-ev"]["status"] == "completed"
    finally:
        (tmp_path / "release").touch()
        if service is not None and not service.done():
            service.cancel()
            await asyncio.gather(service, return_exceptions=True)
        (tmp_path / "done").touch()
        try:
            output, _ = await asyncio.wait_for(process.communicate(), 10)
        except TimeoutError:
            process.kill()
            output, _ = await process.communicate()
        assert process.returncode == 0, output.decode()
        assert (tmp_path / "ready").exists(), output.decode()
