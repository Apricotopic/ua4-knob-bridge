namespace Ua4KnobBridge;

/// <summary>
/// Converts the RK M75's two reserved Consumer usages into one action per
/// active-to-neutral report cycle. Repeated active reports are key-repeat or
/// duplicate transport reports, not additional encoder detents.
/// </summary>
public sealed class ConsumerUsageNormalizer
{
    public const ushort CounterClockwiseUsage = 0x02FE;
    public const ushort ClockwiseUsage = 0x02FF;

    private ushort activeUsage;

    public KnobAction? Accept(ushort usage)
    {
        if (usage == 0)
        {
            activeUsage = 0;
            return null;
        }

        if (usage == activeUsage)
            return null;

        activeUsage = usage;
        return usage switch
        {
            ClockwiseUsage => KnobAction.Clockwise,
            CounterClockwiseUsage => KnobAction.CounterClockwise,
            _ => null
        };
    }

    public void Reset() => activeUsage = 0;
}
