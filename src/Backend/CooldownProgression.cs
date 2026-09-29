internal static class CooldownProgression
{
    // The first value is the next cooldown for an account with no observed cooldown yet.
    public static readonly uint[] StepsSeconds =
    [
        20u * 60u * 60u,
        7u * 24u * 60u * 60u,
        31u * 24u * 60u * 60u,
        90u * 24u * 60u * 60u,
        181u * 24u * 60u * 60u,
    ];

    public static int Classify(uint seconds)
    {
        if (seconds <= 36u * 60u * 60u) return 1;
        if (seconds <= 10u * 24u * 60u * 60u) return 2;
        if (seconds <= 45u * 24u * 60u * 60u) return 3;
        if (seconds <= 120u * 24u * 60u * 60u) return 4;
        return 5;
    }

    public static uint Next(int observedTier) =>
        StepsSeconds[Math.Clamp(observedTier, 0, StepsSeconds.Length - 1)];
}
