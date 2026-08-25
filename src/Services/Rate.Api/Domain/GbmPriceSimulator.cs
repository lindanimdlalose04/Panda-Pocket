namespace PandaPocket.Services.Rate.Domain;

/// <summary>
/// A geometric Brownian motion price generator.
///
/// GBM is the standard model for asset prices. Each step is
///
///     S(t + dt) = S(t) * exp[ (mu - sigma^2 / 2) * dt + sigma * sqrt(dt) * Z ]
///
/// where Z is drawn from the standard normal distribution.
///
/// Two details are worth being able to defend:
///
/// The exponential form means the price is multiplied by a positive number at
/// every step, so it can approach zero but never reach or cross it. A naive
/// additive random walk can go negative, which is nonsense for a price.
///
/// The "- sigma^2 / 2" term is the Ito correction. Without it, mu would not be
/// the drift actually observed, because the expected value of a lognormal
/// variable is not the exponential of the expected value of its logarithm.
/// Including it makes E[S(t)] = S(0) * exp(mu * t), which is what "drift of
/// 15 percent a year" is normally taken to mean.
///
/// This is deliberately a local simulator rather than a call to a real exchange
/// API. An external dependency that rate-limits or goes down during a live demo
/// is an unacceptable risk, and for the purpose of demonstrating a circuit
/// breaker a local service is a genuine dependency all the same.
/// </summary>
public sealed class GbmPriceSimulator(decimal startPrice, double drift, double volatility, int? seed = null)
{
    private const double TradingSecondsPerYear = 365.0 * 24 * 60 * 60;

    private readonly Random _random = seed is null ? new Random() : new Random(seed.Value);
    private double _price = (double)startPrice;

    // Box-Muller produces two independent normals per call. Keeping the spare
    // halves the number of logarithm and trigonometric operations.
    private double? _spareNormal;

    public decimal CurrentPrice => Round(_price);

    /// <summary>
    /// Advance the walk by <paramref name="elapsed"/> and return the new price.
    /// </summary>
    public decimal Advance(TimeSpan elapsed)
    {
        // dt is expressed as a fraction of a year, because drift and volatility
        // are quoted annualised.
        var dt = elapsed.TotalSeconds / TradingSecondsPerYear;

        var z = NextStandardNormal();
        var exponent = (drift - volatility * volatility / 2.0) * dt
                       + volatility * Math.Sqrt(dt) * z;

        _price *= Math.Exp(exponent);
        return Round(_price);
    }

    /// <summary>
    /// Box-Muller transform: converts two independent uniform values on (0, 1]
    /// into two independent standard normal values. .NET's Random supplies
    /// uniform values only, and GBM needs normal ones.
    /// </summary>
    private double NextStandardNormal()
    {
        if (_spareNormal is { } spare)
        {
            _spareNormal = null;
            return spare;
        }

        // NextDouble returns [0, 1). A zero would make Log undefined, so shift
        // it into (0, 1].
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();

        var magnitude = Math.Sqrt(-2.0 * Math.Log(u1));
        _spareNormal = magnitude * Math.Sin(2.0 * Math.PI * u2);
        return magnitude * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// Two decimal places, away from zero on a midpoint. Prices are money and
    /// are stored as decimal; banker's rounding, which is .NET's default, would
    /// be a surprising choice for a quoted price.
    /// </summary>
    private static decimal Round(double value) =>
        Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
}
