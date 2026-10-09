using System.Text.Json.Nodes;

namespace CodexUsageTray;

internal static class CodexHookSupport
{
    public static void RunBeforeTool()
    {
        try
        {
            Console.InputEncoding = new System.Text.UTF8Encoding(false);
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            var hook = JsonNode.Parse(Console.In.ReadToEnd())?.AsObject();
            if (hook?["hook_event_name"]?.GetValue<string>() != "PreToolUse"
                || hook["tool_name"]?.GetValue<string>() is not { } toolName
                || !(toolName is "switch_account" or "subscribe_chat" or "unsubscribe_chat"
                    || toolName.EndsWith("__switch_account", StringComparison.Ordinal)
                    || toolName.EndsWith("__subscribe_chat", StringComparison.Ordinal)
                    || toolName.EndsWith("__unsubscribe_chat", StringComparison.Ordinal))
                || hook["session_id"]?.GetValue<string>() is not { Length: > 0 } sessionId
                || hook["tool_input"] is not JsonObject toolInput)
            {
                WriteEmptyResult();
                return;
            }

            var updatedInput = (JsonObject)toolInput.DeepClone();
            updatedInput["session_id"] = sessionId;
            updatedInput["working_directory"] = hook["cwd"]?.GetValue<string>() ?? "";
            Console.Out.WriteLine(new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "allow",
                    ["updatedInput"] = updatedInput
                }
            }.ToJsonString());
        }
        catch (Exception ex)
        {
            AppLog.Error("pre-tool-hook-failed", ex);
            WriteEmptyResult();
        }
    }

    public static async Task RunStopAsync()
    {
        await NotifyTurnEndedAsync("Stop");
    }

    public static async Task RunInterruptAsync() => await NotifyTurnEndedAsync("Interrupt");

    public static void RunTurnStart()
    {
        try
        {
            Console.InputEncoding = new System.Text.UTF8Encoding(false);
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            var hook = JsonNode.Parse(Console.In.ReadToEnd())?.AsObject();
            if (hook?["hook_event_name"]?.GetValue<string>() != "UserPromptSubmit"
                || hook["session_id"]?.GetValue<string>() is not { Length: > 0 } sessionId
                || hook["turn_id"]?.GetValue<string>() is not { Length: > 0 } turnId)
            {
                WriteEmptyResult();
                return;
            }

            ChatSessionStore.MarkTurnStarted(sessionId, turnId);
            WriteEmptyResult();
        }
        catch (Exception ex)
        {
            AppLog.Error("turn-start-hook-failed", ex);
            WriteEmptyResult();
        }
    }

    private static async Task NotifyTurnEndedAsync(string expectedEvent)
    {
        string? sessionId = null;
        string? turnId = null;
        try
        {
            Console.InputEncoding = new System.Text.UTF8Encoding(false);
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            var hook = JsonNode.Parse(Console.In.ReadToEnd())?.AsObject();
            if (hook?["hook_event_name"]?.GetValue<string>() != expectedEvent
                || hook["session_id"]?.GetValue<string>() is not { Length: > 0 } parsedSessionId)
            {
                WriteEmptyResult();
                return;
            }

            sessionId = parsedSessionId;
            turnId = hook["turn_id"]?.GetValue<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _ = await LocalAgentBridge.SendAsync(new JsonObject
            {
                ["operation"] = "turn-ended",
                ["session_id"] = sessionId,
                ["turn_id"] = turnId
            }, timeout.Token);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                try { ChatSessionStore.MarkTurnStopped(sessionId, turnId); }
                catch (Exception storeException) { AppLog.Error("turn-end-state-cleanup-failed", storeException); }
            }
            AppLog.Error("turn-end-hook-notified-tray-failed", ex);
        }
        finally { WriteEmptyResult(); }
    }

    private static void WriteEmptyResult() => Console.Out.WriteLine("{}");
}
