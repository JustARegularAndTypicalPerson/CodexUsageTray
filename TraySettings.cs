using System.Text.Json;

namespace CodexUsageTray;

internal static class TraySettings
{
    private const int DefaultAgentSwitchThresholdPercent = 80;
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageTray", "settings.json");

    public static int LoadAgentSwitchThresholdPercent()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return DefaultAgentSwitchThresholdPercent;
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (document.RootElement.TryGetProperty("agentSwitchThresholdPercent", out var value)
                && value.TryGetInt32(out var percent)
                && percent is >= 1 and <= 99)
                return percent;

            AppLog.Warning("agent-switch-threshold-invalid");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error("agent-switch-threshold-read-failed", ex);
        }

        return DefaultAgentSwitchThresholdPercent;
    }

    public static void SaveAgentSwitchThresholdPercent(int percent)
    {
        if (percent is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(percent));

        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var stagingPath = SettingsPath + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(stagingPath, JsonSerializer.Serialize(new
            {
                agentSwitchThresholdPercent = percent
            }));
            File.Move(stagingPath, SettingsPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("agent-switch-threshold-staging-cleanup-failed");
            }
        }
    }
}
