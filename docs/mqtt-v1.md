# MQTT v1 contract

This is the boundary between the .NET service and `hyundai_bridge`. It contains no Hyundai endpoint, token, protocol or raw error information. Transport changes do not change native HA identities. The example documents in `contract/examples` are synthetic test data, not observations or verified capabilities of a real vehicle.

## Topics

| Topic | Payload | Retained | QoS |
|---|---|---|---|
| `hyundai/v1/bridges/{bridgeId}/manifest` | bridge name/id, vehicle metadata and verified capabilities | yes | 1 |
| `hyundai/v1/bridges/{bridgeId}/availability` | `online` / `offline` | yes; offline Last Will | 1 |
| `hyundai/v1/{vehicleId}/state` | one complete normalized state snapshot | yes | 1 |
| `hyundai/v1/{vehicleId}/availability` | `apiReachable`: boolean/null; `dataFreshness`: current/stale/unknown | yes | 1 |
| `hyundai/v1/{vehicleId}/command/{command}` | command id plus command arguments | **no** | 1 |
| `hyundai/v1/{vehicleId}/command-result` | correlated command outcome | no | 1 |

Bridge and vehicle ids are nonempty topic-safe strings (letters, digits, underscore, hyphen). Vehicle ids must be stable and unique across bridge instances sharing a broker. VIN is the stable HA device/entity identity, independent of the topic id. A bridge may publish an empty vehicle list. Metadata for each advertised vehicle must include id, VIN and `capabilities`. Name/model may be absent/null, matching normalized discovery. HA displays the available name, model or VIN and leaves unknown model metadata unset; never invent VIN/model information.

`schemaVersion` in the manifest is exactly 1. Unknown additive properties are ignored. Unsupported required structure, wrong ids, wrong scalar types, nonfinite numbers or invalid timestamps are rejected without replacing the last valid snapshot. State values may be absent/null; that means unknown, not false/zero. A state message is a complete snapshot, so missing values do not inherit values from a prior snapshot. New major versions need a new topic version.

## Capabilities and entities

`capabilities.stateFields` names supported normalized fields, including currently unknown/null values. `capabilities.commands` names verified supported commands. An advertised capability is a producer assertion based on real adapter support; the test examples do not justify advertising capabilities for real vehicles.

Native sensors/binary sensors/tracker require their respective fields. A native lock also requires both lock/unlock commands. Climate requires observed `isClimateOn`, target temperature, both climate commands, and actual temperature bounds/step in `capabilities.climate`. Optional measured cabin/outside temperature fields are separate from HVAC target temperature. Optional defrost is a climate preset (`none` / `defrost`) when the producer advertises support and an observed `isDefrostOn` field. `heat_cool` means remote temperature control is on; HA does not claim that the car is actively heating or cooling.

Charge numbers require both AC/DC fields, the charge-limit command and actual bounds/step in `capabilities.chargeLimits`. Updating one limit includes the last observed opposite limit; unknown opposite values prevent the command. Minimum/maximum/step values come from the manifest, not a Hyundai assumption in HA. Refresh/charging buttons require their command capabilities. Unsupported features never generate entities.

The initial scalar fields are those in `contract/examples/state.json`: battery percentage, EV range in km, odometer in km, charging/plug/lock booleans, AC/DC charge limits, climate on/target/defrost, optional measured temperatures, optional GPS coordinates, vehicle observation time and bridge retrieval time.

Optional additive v1 fields: `auxiliaryBatteryPercent` (integer 0–100, 12-V battery state of charge), and the booleans `isFrontLeftDoorOpen`, `isFrontRightDoorOpen`, `isRearLeftDoorOpen`, `isRearRightDoorOpen`, `isTrunkOpen`, `isHoodOpen`. True means open, false closed, null/absent unknown. They create a native battery sensor and six opening binary sensors only when advertised. Existing consumers can ignore these additions; no major version or identity changes are required.

## Commands

Each command carries a new UUID `commandId`. No optimistic state updates are performed.

```json
{"commandId":"...","temperatureCelsius":21,"defrost":false}
```

```json
{"commandId":"...","acPercent":80,"dcPercent":90}
```

No-argument commands contain only `commandId`. Result payload:

```json
{"commandId":"...","command":"lock","status":"accepted","message":null}
```

Statuses: accepted, completed, failed. `completed` requires the adapter's verified completion, never HTTP acceptance alone. `message` is an optional short normalized human-readable failure explanation, never an upstream response or secret. The command/topic and result must match vehicle id, command and command id. Invalid/unrelated results cannot complete another request.

HA service calls wait up to 120 seconds for completion. An accepted result keeps the call pending. Failure raises a native HA service error; timeout or lost transport reports that the outcome is unknown. HA does not blindly resubmit. Updated vehicle state is a separate observation, not fabricated from a command result. A late terminal result may still update the displayed command status.

The producer must reject retained controls, deduplicate command ids under QoS redelivery, serialize per-vehicle controls and honor its refresh cooldown. These are bridge requirements; the HA consumer is not a Hyundai control implementation.

## Availability and lifecycle

MQTT connectivity and bridge Last Will determine transport availability. API failure and stale data retain readable last-known state while exposing `api_reachable`, `data_freshness`, `vehicle_updated_at` and `bridge_updated_at` attributes. No HA freshness cutoff is invented. `dataFreshness=current` must mean the producer verified a current vehicle observation; a recent cached read alone is insufficient. Until that determination is possible, publish unknown.

HA uses its existing MQTT connection and subscribes to retained metadata/state after each restart. Advertised vehicles/capabilities can arrive after setup and create entities. Removed capabilities/vehicles make existing entities unavailable; user registry customizations are preserved. A different VIN must use a different vehicle id, preventing identity reassignment.

Home Assistant's integration discovery callback listens only to the versioned manifest. It creates a `hyundai_bridge` config flow; no `homeassistant/...` MQTT Discovery payloads or MQTT entities are used. Normal entity use needs no MQTT topic or Hyundai credential knowledge.
