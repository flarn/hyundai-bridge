# MyHyundai EU research — 2026-10-06

Scope: Hyundai, Europe, Swedish accounts; no model-specific routing. These are undocumented consumer endpoints, not a supported Hyundai developer contract. Upstream live reports are evidence for the implementation shape, not verification of this bridge or every Hyundai model.

Follow-up: [push status research, 2026-10-07](push-status-research.md) identifies an open EU Service Hub MQTT implementation and distinguishes official Pleos Fleet webhooks from private-owner Vehicle Data API access.

Sources inspected at fixed commits:

- [hyundai_kia_connect_api](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/tree/daca6876e98d33815b8c7611fc38f5ec24c8355a), especially `HyundaiCciApiEU.py`, `GspaApiEU.py`, authentication tests, CCS2 fixtures and `gspa/`.
- [kia_uvo](https://github.com/Hyundai-Kia-Connect/kia_uvo/tree/1ba15289d8fcf247a4919e6b1a001978bd42947d), especially native entity implementations, charge-limit validation and its polling defaults.

## Authentication and discovery

[Current Hyundai brand constants](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/blob/daca6876e98d33815b8c7611fc38f5ec24c8355a/hyundai_kia_connect_api/HyundaiCciApiEU.py) identify three hosts:

- IDP: `https://idpconnect-eu.hyundai.com`
- CCI: `https://cci-api-eu.hyundai.com/domain/api/`
- GSPA: `https://gspa-ccs-eu.hyundai.com/gspa/v1/`

The OneApp OAuth client is `4f4953b5-02e1-4dbc-8599-87e983ee1be5`; redirect URI `https://oneapp.hyundai.com/redirect`. It is a public app identifier, not a user secret. The vehicle API uses CCI/GSPA. Live inspection on 2026-10-06 shows that the authorize GET still redirects through `https://prd.eu-ccapi.hyundai.com:8080/web/v1/user/authorize`; allow this Hyundai identity page during cookie setup. This does not select the legacy vehicle API.

The inspected [password flow](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/blob/daca6876e98d33815b8c7611fc38f5ec24c8355a/hyundai_kia_connect_api/GspaApiEU.py#L357) establishes IDP cookies through OAuth authorize, retrieves an RSA JWK from `/auth/api/v1/accounts/certs`, and posts `/auth/account/signin` with hex RSA PKCS#1 v1.5 encrypted password and key id. Signin redirects with a code; do not follow or log that credential-bearing redirect. Exchange the code via CCI `POST v1/auth/token?code=...`.

Keep the complete returned token set: access/refresh, non-CCS access/refresh, exchangeable access/refresh and ID token. CCI refresh is `POST v2/auth/token-refresh`, JSON containing that set. Persist rotated tokens. CCI `expiresIn` and CCS `expiresTime` are separate relative lifetimes; a CCS lifetime must not be used to assume that CCI remains valid. A refresh `4111` means credentials expired in upstream reports. Do not fall back to repeated password login on outages or arbitrary errors.

CCI headers include app package `com.hyundai.oneapp.eu`, client version/OS/device metadata, locale, timezone, and, when authenticated, Bearer authorization plus `Authentication`, `exchangeable-token` and `non-ccs-token`. Use a stable locally stored device UUID. The initial implementation keeps upstream authorize `country=de&lang=en` because this is the observed protocol shape; it is not a Swedish-account restriction. CCI requests use `SV` / `sv`.

Discovery reads `GET v1/vehicle/available-vehicles?detail=true`. Normalize the `ccspCarId`/nested car-id envelope, VIN, display name and model; no model whitelist. Unexpected envelopes are errors, not empty garages. Push-device registration exists upstream but is best effort and unnecessary for this read-only discovery phase.

## State and commands

[GSPA](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/blob/daca6876e98d33815b8c7611fc38f5ec24c8355a/hyundai_kia_connect_api/GspaApiEU.py) exchanges CCI credentials through `v1/auth/token-exchange?serviceType=CCS`. GSPA requests need CCS Bearer credentials, CCSP identifiers, device/protocol headers and matched `X-Stamp` / `X-Request-Id`. The stamp/response cryptography is reverse engineered from the app SDK. It must remain wholly inside the .NET Hyundai adapter; implementing CCI alone is insufficient for vehicle state.

Cached state: `status/vehicles/{carId}/stored-status`, with timestamped CCS2 `state.Vehicle` data. Do not confuse HVAC set temperature with measured cabin temperature. Missing fields and battery sentinel values must normalize to null. Preserve the source vehicle timestamp and separately record bridge retrieval time. EU Kia CCS2 fixtures exist, but inspected fixtures do not establish an IONIQ 9-specific schema or model-wide support.

Explicit wake: `POST remote/vehicles/{carId}/prewakeup`. Acceptance and another cached read do not prove refreshed vehicle data. Refresh completion needs an advanced vehicle timestamp, or a bounded timeout reported honestly. No automatic wake while parked.

Upstream remote endpoints under `remote/vehicles/{carId}/`:

| Operation | Endpoint | Protocol intent |
|---|---|---|
| Lock/unlock | `door` | close/open |
| Climate start/stop | `temperature` | start/stop; temperature, unit/type and optional defrost |
| Charge start/stop | `charge` | start/stop |
| AC/DC limits | `charge-target` | set with separate AC/DC targets |

Some controls require CCI PIN verification (`v1/auth/pin`) and a short-lived control token. Incorrect PIN attempts can lock access; never retry a PIN blindly. Auth class is endpoint-specific. Remote climate is delayed and cached: do not claim real-time heating/cooling. Upstream charge-limit numbers accept 50–100 in steps of 10; actual per-vehicle limits and climate bounds still need verification before exposing writable HA entities.

GSPA submissions can return HTTP 202 with `metaInfo` and SID/service SID. Result polling uses `status/vehicles/{carId}/update-status?path=gspa/v1/remote/vehicles`; states include WAIT/SUCCESS/FAILURE/TIMEOUT. Upstream polling is vehicle/path scoped, so serialize commands for a vehicle to avoid miscorrelation. A command accepted without a result handle is not completed. Never blindly retry a control after an ambiguous transport timeout.

## Practical limits and initial policy

There is no verified current published EU quota. The [community rate-limit wiki](https://github.com/Hacksore/bluelinky/wiki/API-Rate-Limits) lists 200 EU daily requests but was edited in 2021; it is historical evidence, not a 2026 guarantee. [kia_uvo defaults](https://github.com/Hyundai-Kia-Connect/kia_uvo/blob/1ba15289d8fcf247a4919e6b1a001978bd42947d/README.md) are 30-minute cached reads and four-hour forced reads.

Proposed bridge defaults for phase 3/7: cached reads every ten minutes, no automatic forced refresh, explicit refresh cooldown ten minutes. A single cached endpoint is preferable to pulling all upstream diagnostic/history feeds. Honor 429/Retry-After, back off outages, bound HTTP calls and command-result waits. Final aggregate request budget must include authentication and every vehicle; do not infer spare quota from a successful call. No polling behavior is implemented in phase 2.

2026-10-07 clarification: continue with polling; the requested 60-second interval is for cached vehicle status once vehicles are available and state retrieval is implemented. Discovery remains every ten minutes, including with a linked vehicle. The first implementation incorrectly applied the minute interval to discovery; that was corrected. Failure backoff and the ten-minute forced-refresh cooldown remain unchanged. The running adapter still retrieves discovery only, not vehicle state. Push is deferred.

## Verification boundary

Live verification on 2026-10-06: the supplied Swedish/EU account authenticated through CCI. A second process reused the persisted session without login. CCI token renewal also succeeded after the local cache expiry was deliberately advanced for a controlled test; this was not a naturally expired-token observation. Both discovery runs and the renewal run returned zero available vehicles. VIN/model, GSPA state, model capabilities and commands remain unverified. All subsequent phases require recorded evidence; synthetic tests must never be presented as physical/API acceptance.
