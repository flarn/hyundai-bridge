using System.Text.Json;
using HyundaiBridge.Hyundai;
using Xunit;

namespace HyundaiBridge.Tests;

public sealed class VehicleAndSessionTests
{
    [Theory]
    [InlineData("[{\"vehicleId\":\"id\",\"modelName\":\"TUCSON\"}]", "TUCSON")]
    [InlineData("{\"vehicles\":{\"ccspVehicle\":{\"carId\":\"id\"},\"modelName\":\"IONIQ 9\"}}", "IONIQ 9")]
    [InlineData("{\"contents\":[{\"ccspCarId\":\"id\",\"vehicleModelName\":\"IONIQ 5\"}]}", "IONIQ 5")]
    public void DiscoveryNormalizesObservedEnvelopeVariantsWithoutModelWhitelist(string json, string model)
    {
        using var document = JsonDocument.Parse(json);
        var vehicle = Assert.Single(VehicleParser.Parse(document.RootElement));
        Assert.Equal("id", vehicle.VehicleId);
        Assert.Equal(model, vehicle.Model);
        Assert.Null(vehicle.Vin);
        Assert.Null(vehicle.Name);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"contents\":null}")]
    [InlineData("{\"contents\":[{}]}")]
    [InlineData("{\"contents\":[{\"vehicleId\":\"id\"},{\"vehicleId\":\"id\"}]}")]
    public void MalformedDiscoveryCannotMasqueradeAsAnEmptyGarage(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<HyundaiException>(() => VehicleParser.Parse(document.RootElement));
    }

    [Fact]
    public void ValidEmptyGarageIsDistinctFromMalformedEnvelope()
    {
        using var document = JsonDocument.Parse("{\"contents\":[]}");
        Assert.Empty(VehicleParser.Parse(document.RootElement));
    }

    [Fact]
    public async Task SessionStorageIsPrivateAndBoundToAccount()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(), CancellationToken.None);
        Assert.Null(await fixture.Store.LoadAsync("other-account", CancellationToken.None));
        Assert.NotNull(await fixture.Store.LoadAsync(Fixture.AccountHash, CancellationToken.None));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(fixture.Directory, "session.json")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fixture.Directory));
        }
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task CancelledSavePreservesTheLastCommittedSession()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(), CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.SaveAsync(fixture.Session(fixture.Clock.Now), cancelled.Token));
        var original = await fixture.Store.LoadAsync(Fixture.AccountHash, CancellationToken.None);
        Assert.False(original!.NeedsRefresh(fixture.Clock.Now));
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }
}
