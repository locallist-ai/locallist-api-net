using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Bordes directos de <see cref="TripContextDto.IsStartDateWithinWindow"/> con un <c>today</c>
/// fijo inyectado (nunca <see cref="DateTime.UtcNow"/>) para que el test sea determinista y no
/// dependa de cuando corre la suite. La ventana permitida es [today-1, today+MaxTripHorizonDays].
/// Los tests de integracion de <c>Tests/Features/PlansTests.cs</c> cubren el mismo contrato via
/// HTTP (POST /plans) usando el <c>DateTime.UtcNow</c> real del servidor; este test pinea los
/// bordes exactos del helper de forma unitaria y barata.
/// </summary>
public class TripContextDtoTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    [Theory]
    // ── Aceptados: dentro de [today-1, today+365] ──
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(365, true)]
    // ── Rechazados: justo fuera de la ventana en cada extremo ──
    [InlineData(-2, false)]
    [InlineData(366, false)]
    public void IsStartDateWithinWindow_PinsExactBoundaries(int offsetDays, bool expected)
    {
        var startDate = Today.AddDays(offsetDays);

        var result = TripContextDto.IsStartDateWithinWindow(startDate, Today);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsStartDateWithinWindow_NullStartDate_IsAlwaysWithinWindow()
    {
        // Compat: clientes viejos que no envian startDate no deben romper.
        var result = TripContextDto.IsStartDateWithinWindow(null, Today);

        Assert.True(result);
    }
}
