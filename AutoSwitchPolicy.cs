namespace CodexUsageTray;

internal readonly record struct AutoSwitchInputs(
    int? ActiveUsagePercent,
    int ThresholdPercent,
    int ActiveTurnCount,
    bool HasAvailableTarget,
    bool ActiveIdentityMatched,
    bool DesktopRunning);

internal static class AutoSwitchPolicy
{
    public static bool ShouldSwitch(AutoSwitchInputs input) =>
        input.ActiveUsagePercent is { } usage
        && usage >= input.ThresholdPercent
        && input.ActiveTurnCount == 0
        && input.HasAvailableTarget
        && input.ActiveIdentityMatched
        && input.DesktopRunning;

    public static int RunSelfTest(TextWriter output)
    {
        var cases = new (string Name, AutoSwitchInputs Input, bool Expected)[]
        {
            ("below threshold stays put", new(79, 80, 0, true, true, true), false),
            ("threshold with running turn waits", new(80, 80, 1, true, true, true), false),
            ("threshold without target stays put", new(80, 80, 0, false, true, true), false),
            ("unmatched active account fails closed", new(95, 80, 0, true, false, true), false),
            ("closed Desktop stays closed", new(95, 80, 0, true, true, false), false),
            ("all turns ended triggers switch", new(80, 80, 0, true, true, true), true),
            ("above threshold switches after final turn", new(94, 80, 0, true, true, true), true)
        };

        var failures = 0;
        foreach (var test in cases)
        {
            var passed = ShouldSwitch(test.Input) == test.Expected;
            output.WriteLine($"{(passed ? "PASS" : "FAIL")} {test.Name}");
            if (!passed) failures++;
        }
        output.WriteLine(failures == 0 ? "Auto-switch policy checks passed." : $"{failures} auto-switch policy check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
