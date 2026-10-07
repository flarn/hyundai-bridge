# Hyundai EU push research — 2026-10-07

Scope: Hyundai/MyHyundai Europe, including Swedish accounts, across models. Public documentation, source code and community reports were inspected. No linked vehicle was available for live reception tests. This note describes candidates, not implemented bridge behavior.

## Findings

| Route | Evidence | Access and readiness |
|---|---|---|
| EU Service Hub MQTT | Receive implementation in open community PR #1332 | Best consumer-account candidate; undocumented, not merged or verified on our vehicle |
| Pleos Fleet webhooks | Official periodic-data and vehicle-event specifications | Separate Fleet agreement and approval; private-account eligibility and cost unknown |
| Pleos Vehicle Data API | Official EU private-owner API documentation | Private keys available; inspected API documents HTTP reads, not a push subscription |
| App notifications / GCM / FCM | Official app terms and historical community attempts | Genuine alerts, but no established current EU receiver supplying complete vehicle state found |

## Consumer account: Service Hub MQTT

[PR #1332](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/pull/1332) remains open as of this research. It implements receive-only MQTT for the EU CCI clients; commands stay on GSPA HTTP. HA coordinator wiring is explicitly a separate follow-up. Reported test and Docker results are upstream reports, not our vehicle acceptance.

Inspected commit: `6a51503f472938bd5d0e599e8bf53ab9af408ad6`.

The [Service Hub adapter](https://github.com/blka/hyundai_kia_connect_api/blob/6a51503f472938bd5d0e599e8bf53ab9af408ad6/hyundai_kia_connect_api/mqtt_service_hub.py) uses this sequence:

1. Discover broker through `GET /api/v3/servicehub/device/host`.
2. Register mobile client through `POST /api/v3/servicehub/device/register`.
3. Resolve per-vehicle push identity and register protocols through `POST /api/v3/servicehub/device/protocol`.
4. Connect and subscribe using returned identities/credentials.

Its Hyundai EU bootstrap host is `egw-svchub-ccs-h-eu.eu-central.hmgmobility.com:31010`; the MQTT broker is discovered separately. Preserve the returned `tid` session header. Device client ID, regular vehicle ID and MQTT vehicle ID are distinct.

Status topics include `service/phone/_/vss/{mqttVehicleId}` and `service/phone/_/connection/{mqttVehicleId}`. Upstream reports broker rejection of QoS 1 and certain result topics; subscriptions use QoS 0. These are reverse-engineered behavior, not Hyundai guarantees.

The [transport](https://github.com/blka/hyundai_kia_connect_api/blob/6a51503f472938bd5d0e599e8bf53ab9af408ad6/hyundai_kia_connect_api/mqtt_client.py) uses outbound MQTT/TLS, certificate verification and reconnect backoff. Inference for the NUC: a receiver following this approach would not need a public inbound webhook endpoint.

Crucially, [VehicleManager](https://github.com/blka/hyundai_kia_connect_api/blob/6a51503f472938bd5d0e599e8bf53ab9af408ad6/hyundai_kia_connect_api/VehicleManager.py) dispatches status callbacks with vehicle ID and topic type, without applying payload fields to normalized state. The inspected [status tests](https://github.com/blka/hyundai_kia_connect_api/blob/6a51503f472938bd5d0e599e8bf53ab9af408ad6/tests/test_mqtt_service_hub.py) use synthetic JSON, not recorded battery/lock/charging messages. We therefore cannot claim complete status delivery, parked-car cadence, model compatibility or reduced vehicle wakeups yet.

## Official APIs: keep Fleet and private Vehicle Data separate

[Fleet onboarding](https://document.pleos.ai/en/api-reference/fleet-api/getting-started) requires a usage agreement and API approval, then allows registering webhook events. Hyundai Connected Mobility [markets Fleet integration for Europe](https://connected-mobility.hyundai.com/what-we-do/data-services).

The [periodic-data schema](https://document.pleos.ai/en/api-reference/fleet-api/fleet-api-webhook/webhook-event-type/periodic-data) includes battery SOC, charging, range, odometer and location, with observation timestamps. Its table specifies 10-second collection / 30-second transmission; this is not evidence of availability or cadence for our parked vehicle. [Vehicle events](https://document.pleos.ai/en/api-reference/fleet-api/fleet-api-webhook/webhook-event-type/vehicle-event) include driver-door lock, plug, hood and trunk. Fields are optional. This is genuine documented push data, rather than just command notifications.

Separately, the [Vehicle Data API overview](https://document.pleos.ai/en/api-reference/vehicle-data-api/intro) describes private-owner access in Europe, with [Sweden listed](https://document.pleos.ai/en/api-reference/vehicle-data-api/faq). [Private key issuance](https://document.pleos.ai/en/api-reference/vehicle-data-api/getting-started/for-private-users/request-my-vehicle-api-key) requires Playground and manufacturer sign-in, but no project or API usage approval. This key does not establish Fleet webhook entitlement. No push subscription was found in the inspected Vehicle Data API documentation; its published vehicle endpoints retrieve data via HTTP. No key or API was tested here.

## Other projects and discussions

- Inspected EU status paths in [Bluelinky](https://github.com/Hacksore/bluelinky/blob/master/src/vehicles/european.vehicle.ts), [evcc](https://github.com/evcc-io/evcc/blob/master/vehicle/bluelink/api.go) and [egmp-alternate-app](https://github.com/JFerretti/egmp-alternate-app/blob/main/src/api/regions/europe.ts) retrieve status through HTTP. Push-device registration alone does not implement a receiver.
- [BlueDeck](https://github.com/tracer99/BlueDeck) lists status push under future ideas.
- Historical [GCM discussion #144](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/issues/144), including the [maintainer response](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/issues/144#issuecomment-1326674470), describes removed registration code and obsolete dependencies.
- [EU Bluelink terms](https://dmassets.hyundai.com/is/content/hyundaiautoever/Bluelink_Terms-of-Use_012025_USpdf), section 3.1.1, confirm app alerts including climate, charging and lock status. They do not document an external status subscription.
- Inspected [HA forum discussion](https://community.home-assistant.io/t/hyundai-bluelink-integration/319129?page=6) and [GoingElectric discussion](https://www.goingelectric.de/forum/viewtopic.php?amp=true&lang=en&start=690&t=69885) discuss notification and cached/forced-refresh behavior, without establishing a usable current receiver.

## Recommendation and verification boundary

Prioritize Service Hub MQTT for a bounded live test once a vehicle is linked. Initially evaluate push as a trigger for a cached HTTP state read, with infrequent polling as fallback. Coalesce events and apply existing rate limits; a push event must not automatically force-wake the vehicle. Direct payload mapping requires real sanitized messages and verified field timestamps first.

Observe normal lock, plug and charging changes; measure received events against cached state and observation times. Check parked behavior, missing/duplicate events, reconnects, token renewal and coexistence with MyHyundai. Do not call an event or command acceptance completed state without supporting vehicle data.

All Hyundai registration, topics and payload mapping would remain inside the .NET adapter. The existing normalized MQTT v1 contract and native HA entities can remain unchanged. This research adds no push implementation or deployment.
