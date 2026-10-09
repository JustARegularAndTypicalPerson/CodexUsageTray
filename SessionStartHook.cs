using System.Text.Json.Nodes;

namespace CodexUsageTray;

/// <summary>Consumes only the pending switch marker for this Codex chat session.</summary>
internal static class SessionStartHook
{
    public static void Run()
    {
        try
        {
            Console.InputEncoding = new System.Text.UTF8Encoding(false);
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            var hook = JsonNode.Parse(Console.In.ReadToEnd())?.AsObject();
            if (hook?["hook_event_name"]?.GetValue<string>() != "SessionStart"
                || hook["source"]?.GetValue<string>() is not ("startup" or "resume")
                || hook["session_id"]?.GetValue<string>() is not { Length: > 0 } sessionId)
            {
                WriteEmptyResult();
                return;
            }

            var marker = ResumeMarkerStore.Consume(sessionId);
            if (marker is null)
            {
                WriteEmptyResult();
                return;
            }

            var label = new string(marker.TargetLabel.Where(character => !char.IsControl(character)).Take(48).ToArray());
            var project = string.IsNullOrWhiteSpace(marker.WorkingDirectory)
                ? "the original working folder"
                : $"the project folder \"{Path.GetFileName(Path.TrimEndingDirectorySeparator(marker.WorkingDirectory))}\"";
            var context = $"This chat is subscribed to Codex Usage Tray account-switch continuation. Codex restarted after a switch to tray account \"{label}\" while this chat was in {project}. First call the tray MCP get_usage tool and confirm active_label is exactly \"{label}\". If it matches, continue the interrupted task in this chat and avoid repeating completed side effects. If it does not match or get_usage is unavailable, do not continue; report that the switch could not be verified.";
            Console.Out.WriteLine(new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "SessionStart",
                    ["additionalContext"] = context
                }
            }.ToJsonString());
            AppLog.Info("switch-resume-context-delivered");
        }
        catch (Exception ex)
        {
            AppLog.Error("session-start-hook-failed", ex);
            WriteEmptyResult();
        }
    }

    private static void WriteEmptyResult() => Console.Out.WriteLine("{}");
}
