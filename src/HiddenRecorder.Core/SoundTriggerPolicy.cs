namespace HiddenRecorder.Core;

/// <summary>Pure policy: media timestamps, never wall-clock time. Every qualifying interval renews activity.</summary>
public sealed class SoundTriggerPolicy(double thresholdDb, TimeSpan minimumDuration)
{
    private TimeSpan? start;
    private TimeSpan? previousEnd;
    public bool Observe(AudioLevel level)
    {
        if (previousEnd is { } previous && level.MediaTime < previous) return false;
        var intervalStart = level.MediaTime - level.Duration;
        if (level.PeakDb < thresholdDb || double.IsNaN(level.PeakDb))
        { start = null; previousEnd = level.MediaTime; return false; }
        if (previousEnd is null || intervalStart - previousEnd > TimeSpan.FromMilliseconds(40)) start = null;
        start ??= intervalStart;
        previousEnd = level.MediaTime;
        return level.MediaTime - start.Value >= minimumDuration;
    }
}
