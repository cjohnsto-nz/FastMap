using System;

namespace FastMap.Network;

// Feedback is callback latency and process CPU across all available cores, not load average.
internal sealed class AdaptiveSamplingBudget
{
    private double current;
    public double Update(double intervalMs, double processCpuPercent, int baseline, int ceiling, bool enabled)
    {
        baseline = Math.Clamp(baseline, 1, 20);
        ceiling = Math.Clamp(ceiling, baseline, 20);
        if (!enabled) return current = baseline;
        current = Math.Clamp(current, baseline, ceiling);
        if (intervalMs > 80 || processCpuPercent >= 85)
            current = Math.Max(baseline, current * 0.65);
        // A 50 ms listener can be quantised to two ~33 ms engine frames.
        else if (intervalMs <= 75 && processCpuPercent < 70)
            current = Math.Min(ceiling, current + 0.5);
        return current;
    }
}
