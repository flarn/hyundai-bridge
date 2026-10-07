# Linked-vehicle cached status — 2026-10-07

The current Hyundai EU adapter is based on the inspected community implementation
at `daca6876e98d33815b8c7611fc38f5ec24c8355a`: CCI token exchange for CCS,
CCS JWT `uid` (ID-token `sub` fallback), matched TSID/X-Stamp headers, and
`GET gspa/v1/status/vehicles/{carId}/stored-status`. This is an undocumented,
reverse-engineered consumer protocol, not a supported Hyundai developer API.

The stamp is the Hyundai SDK white-box block cipher with affine substitution and
linear tables: eight X4/X27 rounds, five GAP/POST_X27 rounds, X8 final substitution,
then CFB-128 using `iv.ccsp.stamp.eu`. A standard AES call with an invented key would
not be equivalent. Only Hyundai's EU parameters are included; no generic brand
framework or response-body decryption is needed for this endpoint. The original
MIT notice accompanies the protocol table. Fixed upstream block/CFB vectors are
tested before real requests.

## Single live read

A temporary read-only .NET probe used the existing container's secret configuration
and session store. It retrieved one vehicle and exchanged a CCS token, then made
one stored-status GET at **18:10:30 UTC**. HTTP **200**, `retCode=S`, `resCode=200-000`.
No remote/prewakeup/control endpoint was called. Production bridge polling was
unchanged during this probe. The temporary helper is separate from the service;
response metadata/field structure were masked before inspection, excluding tokens,
VIN/account identifiers and precise location.

The response contained **243 leaf fields** under the normal GSPA envelope,
including `data.state.Vehicle`. Observed useful data:

| Data | Observed raw value | Normalized behavior |
|---|---|---|
| Traction battery SoC | 80.5 | v1 whole percentage, rounded nearest/half upward: 81 |
| Range | 408, unit 1 | 408 km |
| Odometer | 112.4 | 112.4 km, as in the EU CCS2 parser |
| Four door locks | all 0 | locked; a missing door remains unknown |
| Charging remaining time | 0 | not charging, following upstream CCS2 semantics |
| Connector fastening | 0 | unplugged |
| AC/DC target SoC | 80 / 80 | 80% / 80%, read-only until commands are implemented |
| Driver HVAC temperature | OFF, unit 0 | target unknown; no invented measured cabin temperature/HVAC action |
| Outside temperature | 5.0, no Unit field | unknown normalized temperature until its unit is established |
| Vehicle Date / Offset | 20261007175313.000 / 1 | 16:53:13 UTC according to the supplied offset |
| Bridge observation | 18:10:30 UTC | separate retrieval timestamp; not evidence of fresh vehicle data |

The fractional SoC explains why strict integer mapping originally returned null.
Normalization adapts inside the Hyundai adapter; MQTT v1 and its HA consumer keep
their established integer-percentage contract. Sentinel/out-of-range SoC stays null.

Other reported fields include 12-V battery level, individual door/hood/trunk state,
battery temperatures/voltages and charging schedules. Their presence does not add
new entities or commands. A cached response alone cannot establish live freshness.
Do not substitute retrieval time when the source timestamp is absent.

## Requested auxiliary battery and openings

Following the user's request, the adapter now maps `Electronics.Battery.Level`
to `auxiliaryBatteryPercent`, and cabin door `Open` fields plus
`Body.Trunk.Open`/`Body.Hood.Open` to six normalized opening booleans.
The current stored response reports 83% and all six openings closed (0).
The inspected upstream parser uses the same paths. For this Swedish left-hand-drive
vehicle, Row1.Driver is front left and Row1.Passenger front right, as mapped upstream.
Only 0/1 and boolean opening values are supported; unknown/sentinel data remains null.
HA exposes a battery measurement and opening entities on the same vehicle device,
with no new API requests or controls.

Position is already parsed from `Location.GeoCoord` and supported by the native HA
tracker, but the actual stored response has no Location field. The inspected EU
client obtains ordinary location from stored status; no separate ordinary cached
location request was found there. Stored surround-view media can contain capture
coordinates, but these are tied to a photo and are not substituted for vehicle status.

Climate target and defrost parsing also already exist. The current driver's target
is OFF and blower speed is zero; no measured cabin temperature is present. Upstream
uses blower speed as an air-control indication, which alone does not verify heating,
cooling or remote climate command support. Native climate controls still require
verified start/stop commands and supported bounds; their implementation remains a
separate phase. No climate or location wake command was sent for this research.

## Operation

Discover only at startup, with at most five discovery attempts. Read cached state
immediately after successful discovery, then every ten minutes per vehicle. Keep
successful observations during failures; back off 10/20/40/60 minutes and honor
Retry-After. A 429 pauses the whole account. No automatic forced refresh. Only
fields with observed supported values are advertised, retaining that capability
through later null observations. Writable features remain unadvertised.

The bridge dashboard now displays the latest successful upstream stored-status
response as formatted JSON for each model. It reads an in-memory, redacted copy: no
extra Hyundai request, no raw authentication response, no exact location or vehicle/
account identifiers. A failed later read retains the last successful response with
its retrieval time. JSON rendering uses text content and does not interpret HTML.
