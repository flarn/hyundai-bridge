namespace HyundaiBridge.Domain;

internal sealed record ClimateCapabilities(double MinTemperatureCelsius, double MaxTemperatureCelsius,
    double TemperatureStepCelsius, bool SupportsDefrost);
internal sealed record ChargeLimitCapabilities(int MinPercent, int MaxPercent, int StepPercent);
internal sealed record VehicleCapabilities(string[] StateFields, string[] Commands,
    ClimateCapabilities? Climate = null, ChargeLimitCapabilities? ChargeLimits = null)
{
    internal static VehicleCapabilities None => new([], []);
}
internal sealed record VehicleMetadata(string VehicleId, string Vin, string? Name, string? Model,
    VehicleCapabilities Capabilities);
internal sealed record VehicleAvailability(bool? ApiReachable, string DataFreshness = "unknown");
internal sealed record BridgeSnapshot(IReadOnlyList<VehicleMetadata> Vehicles, IReadOnlyList<VehicleState> States);

internal sealed record VehicleState
{
    public required string VehicleId { get; init; }
    public required string Vin { get; init; }
    public int? BatteryPercent { get; init; }
    public int? AuxiliaryBatteryPercent { get; init; }
    public double? EstimatedRangeKm { get; init; }
    public bool? IsCharging { get; init; }
    public bool? IsPluggedIn { get; init; }
    public bool? IsLocked { get; init; }
    public bool? IsChargePortOpen { get; init; }
    public bool? IsSunroofOpen { get; init; }
    public double? ChargingPowerKw { get; init; }
    public double? RemainingChargeTimeMinutes { get; init; }
    public bool? IsFrontLeftDoorOpen { get; init; }
    public bool? IsFrontRightDoorOpen { get; init; }
    public bool? IsRearLeftDoorOpen { get; init; }
    public bool? IsRearRightDoorOpen { get; init; }
    public bool? IsTrunkOpen { get; init; }
    public bool? IsHoodOpen { get; init; }
    public int? AcChargeLimitPercent { get; init; }
    public int? DcChargeLimitPercent { get; init; }
    public bool? IsClimateOn { get; init; }
    public double? TargetTemperatureCelsius { get; init; }
    public bool? IsDefrostOn { get; init; }
    public double? CabinTemperatureCelsius { get; init; }
    public double? OutsideTemperatureCelsius { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? OdometerKm { get; init; }
    public DateTimeOffset? VehicleUpdatedAt { get; init; }
    public required DateTimeOffset BridgeUpdatedAt { get; init; }
}

internal sealed record VehicleCommand(string VehicleId, Guid CommandId, string Command,
    double? TemperatureCelsius = null, bool? Defrost = null, int? AcPercent = null, int? DcPercent = null);
internal sealed record CommandResult(Guid CommandId, string Command, string Status, string? Message = null);

// The adapter reports acceptance only after the upstream accepts it. Returning
// means verified completion; an optional state is an actual new observation.
internal delegate Task<VehicleState?> VehicleCommandHandler(VehicleCommand command,
    Func<Task> reportAccepted, CancellationToken cancellationToken);
