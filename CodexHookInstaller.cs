using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageTray;

internal static class CodexHookInstaller
{
    private static readonly string HooksPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "hooks.json");

    public static bool InstallOrUpdate()
    {
        var root = ReadExisting();
        if (root["hooks"] is not JsonObject hooks)
        {
            if (root["hooks"] is not null)
                throw new InvalidDataException("The global Codex hooks setting is not an object; it was left unchanged.");
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var executable = Environment.ProcessPath ?? Application.ExecutablePath;
        var changed = false;
        changed |= AddHook(hooks, "PreToolUse", "(switch_account|subscribe_chat|unsubscribe_chat)$", executable,
            "--hook-before-tool", "Preparing a tray account action");
        changed |= AddHook(hooks, "UserPromptSubmit", null, executable,
            "--hook-turn-start", "Tracking active Codex turns");
        changed |= AddHook(hooks, "Stop", null, executable,
            "--hook-stop", "Checking whether tray account switching is ready");
        changed |= AddHook(hooks, "Interrupt", null, executable,
            "--hook-interrupt", "Updating active Codex turn tracking");
        changed |= AddHook(hooks, "SessionStart", "startup|resume", executable,
            "--hook-session-start", "Checking subscribed chats after account switching");

        if (!changed) return false;
        var directory = Path.GetDirectoryName(HooksPath)!;
        Directory.CreateDirectory(directory);
        var staging = HooksPath + ".new-" + Guid.NewGuid().ToString("N");
        var backup = HooksPath + ".codex-usage-tray-backup-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        try
        {
            if (File.Exists(HooksPath)) File.Copy(HooksPath, backup, overwrite: false);
            File.WriteAllText(staging, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(staging, HooksPath, overwrite: true);
            AppLog.Info("global-codex-hooks-installed", $"backup={(File.Exists(backup) ? "created" : "not-needed")}");
            return true;
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("global-hooks-staging-cleanup-failed");
            }
        }
    }

    private static JsonObject ReadExisting()
    {
        if (!File.Exists(HooksPath)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(HooksPath))?.AsObject() ?? new JsonObject(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            AppLog.Error("global-codex-hooks-read-failed", ex);
            throw new InvalidDataException("The global Codex hooks file could not be parsed safely; it was left unchanged.", ex);
        }
    }

    private static bool AddHook(JsonObject hooks, string eventName, string? matcher, string executable, string argument, string statusMessage)
    {
        if (hooks[eventName] is not JsonArray eventHooks)
        {
            if (hooks[eventName] is not null)
                throw new InvalidDataException($"The existing {eventName} hook setting is not an array; hooks were left unchanged.");
            eventHooks = new JsonArray();
            hooks[eventName] = eventHooks;
        }

        var command = $"\"{executable}\" {argument}";
        var changedExisting = false;
        for (var groupIndex = eventHooks.Count - 1; groupIndex >= 0; groupIndex--)
        {
            if (eventHooks[groupIndex] is not JsonObject existingGroup
                || existingGroup["hooks"] is not JsonArray existingHandlers)
                continue;
            var handlerIndex = -1;
            for (var i = existingHandlers.Count - 1; i >= 0; i--)
            {
                if (existingHandlers[i] is JsonObject existingHandler
                    && string.Equals(existingHandler["command"]?.GetValue<string>(), command, StringComparison.OrdinalIgnoreCase))
                {
                    handlerIndex = i;
                    break;
                }
            }
            if (handlerIndex < 0) continue;

            var existingMatcher = existingGroup["matcher"]?.GetValue<string>();
            if (string.Equals(existingMatcher, matcher, StringComparison.Ordinal)) return changedExisting;
            existingHandlers.RemoveAt(handlerIndex);
            if (existingHandlers.Count == 0) eventHooks.RemoveAt(groupIndex);
            changedExisting = true;
        }

        var handler = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = 3,
            ["statusMessage"] = statusMessage
        };
        var group = new JsonObject { ["hooks"] = new JsonArray(handler) };
        if (matcher is not null) group["matcher"] = matcher;
        eventHooks.Add(group);
        return true;
    }
}
