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

## Requested charge port, sunroof, charging measurements and source-time sensor

A later cached observation at 19:07:02 UTC reported current range 363 with unit 1
(km); target-charge range Standard/Quick was 403. These are different measurements.
The adapter keeps current DTE and converts only declared miles (unit 2/3) to km.
No conversion is added to unit 1 to imitate an app value of approximately 400 km.

The app showed 80% while the stored traction SoC was 80.5. The previous rounding
policy shown above produced 81%; it is now truncated to the whole percentage (80).
This matches this observation, without claiming the app's policy for every model.

`Green.ChargingDoor.State` maps 0/2 to closed, 1 to open, others to unknown,
following the inspected community parser. The real value was 2.
`Body.Sunroof.Glass.Open` uses only boolean/0/1; the real value was 0.
Unverified tilt codes do not become an invented open/closed state.

The same upstream implementation maps `Green.Electric.SmartGrid.RealTimePower`
to charging power in kW (`Vehicle.ev_charging_power`) and
`Green.ChargingInformation.Charging.RemainTime` to minutes remaining in the active
session. These become nullable `chargingPowerKw` and `remainingChargeTimeMinutes`.
The real parked observation reports zero for both. Positive charging measurements
are covered by synthetic mapping tests but have not yet been verified while this
vehicle is charging. EstimatedTime by charger type and ElectricCurrentLevel settings
are not substituted for actual power or remaining time.

HA now exposes `vehicleUpdatedAt` as a native timestamp sensor when advertised.
Later retrievals of the same cached data do not advance it. Bridge retrieval time
remains a separate attribute. Raw lights currently contain warnings and turn-signal
fields; the user requested belysning on/off, so no warning entities were added.
Ordinary headlamp on/off semantics remain unverified.

Source: [Hyundai-Kia-Connect EU adapter](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/blob/daca6876e98d33815b8c7611fc38f5ec24c8355a/hyundai_kia_connect_api/GspaApiEU.py)
and [normalized upstream Vehicle units](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/blob/daca6876e98d33815b8c7611fc38f5ec24c8355a/hyundai_kia_connect_api/Vehicle.py).

## Requested driving measurements — 2026-10-08

The cached response retrieved at 06:22:15 UTC now contains numerical driver target
22 °C and blower level 3; the later 06:52:17 UTC response reports OFF and level 0.
Expose the existing target as a read-only temperature sensor and blower activity
as `isCabinFanOn`. Do not infer measured cabin temperature, remote climate state,
HVAC action or writable capabilities. Nonnegative integral blower levels yield
activity (>0); missing/unsupported data stays unknown.

Four tire pressure raw readings 30/30/30/29 with PressureUnit=2 map to 3.0/3.0/3.0/2.9
bar. The current community constants encode PSI=0 (raw psi), kPa=1 (raw ×5 kPa),
bar=2 (raw ×0.1 bar). Normalize all supported units to bar. Raw 255 is the no-reading
sentinel, raw zero is a possible reading, unsupported unit codes remain unknown.
The aggregate PressureLow field is a vehicle-reported warning, without an invented
pressure threshold. Conversion follows upstream and should be compared with the
car display; this step does not certify the physical TPMS reading.

Battery temperatures use the upstream-mapped Min.Raw/Max.Raw Celsius fields (9/10
in the inspected response). Decimal subfields are not substituted without verifying
their semantics. BatteryRemain.Value=272872.8 with explicit Unit=kJ converts to
75.798 kWh. This is reported remaining battery energy, not a guarantee of usable
energy, nominal capacity or health. Missing/unknown unit or negative energy is null.
Cabin.Window Open values map independently for four windows; verified boolean/0/1
codes are supported, other codes remain unknown. The inspected response has all
four closed.

AverageFuelEconomy values and Unit=5 remain unnormalized until the unit is verified.
No additional API request, wake or command is introduced; all fields are from the
existing stored-status response. Native entity classes, units, unknown states and
absence of command capabilities are tested through HA's actual state machine.

Reference: the current upstream
[pressure scale definitions](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/blob/master/hyundai_kia_connect_api/const.py),
[no-reading sentinel](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/blob/master/hyundai_kia_connect_api/utils.py)
and [CCS2 field mapping](https://github.com/Hyundai-Kia-Connect/hyundai_kia_connect_api/blob/master/hyundai_kia_connect_api/GspaApiEU.py), inspected 2026-10-08.

## Live remote HVAC observation and requested entities — 2026-10-08

The user manually started HVAC. Before the start, the cached response retrieved at
08:56:38 UTC reported target OFF, blower level 0 and RemoteClimateDetails=0.
The normal stored-status read at 09:06:38 UTC reported target 24 °C, blower level 5
and RemoteClimateDetails=1, with an advanced Vehicle.Date. No bridge remote command,
wake or forced refresh was issued for this observation.

Map the observed remote codes 0/1 to existing normalized `isClimateOn` false/true;
other values remain unknown. This indication is independent of blower activity
and does not report a heating/cooling action. HA exposes a read-only Remote climate
binary sensor. The new nullable `cabinFanSpeedLevel` carries a nonnegative integral
blower level with no invented unit or maximum, alongside existing fan activity and
target temperature. Missing/invalid levels remain unknown. Commands stay empty;
a writable ClimateEntity still requires verified start/stop support and bounds.

Tests cover observed on/off, unsupported codes, invalid/missing levels, normalized
serialization, native HA states and read-only operation without command capabilities.
