namespace Karry.Modules.Assets.Domain;

/// <summary>DEMO ONLY (TC-P1-PLT-005): domain logic without any test.</summary>
public sealed class HourMeter(decimal reading)
{
    public decimal Reading { get; private set; } = reading;

    public decimal Advance(decimal newReading)
    {
        if (newReading < Reading)
        {
            throw new InvalidOperationException("Hour meters never go backwards.");
        }

        var delta = newReading - Reading;
        Reading = newReading;
        return delta;
    }
}
