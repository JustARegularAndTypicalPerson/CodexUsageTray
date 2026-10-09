using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageTray;

internal static class McpServer
{
    public static async Task RunAsync()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        using var reader = Console.In;
        using var writer = Console.Out;
        while (await reader.ReadLineAsync() is { } line)
        {
            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (request is not JsonObject message) continue;

            var method = message["method"]?.GetValue<string>();
            var id = message["id"]?.DeepClone();
            if (id is null) continue;

            JsonObject response;
            try
            {
                var result = method switch
                {
                    "initialize" => InitializeResult(),
                    "ping" => new JsonObject(),
                    "tools/list" => ToolsList(),
                    "tools/call" => await CallToolAsync(message["params"]?.AsObject()),
                    _ => throw new McpException(-32601, "Method not found.")
                };
                response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (McpException ex)
            {
                AppLog.Warning("mcp-request-rejected", $"method={method ?? "unknown"}; code={ex.Code}");
                response = ErrorResponse(id, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                AppLog.Error("mcp-request-failed", ex, $"method={method ?? "unknown"}");
                response = ErrorResponse(id, -32000, string.IsNullOrWhiteSpace(ex.Message) ? "The tray request failed." : ex.Message);
            }

            await writer.WriteLineAsync(response.ToJsonString());
            await writer.FlushAsync();
        }
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
        ["serverInfo"] = new JsonObject { ["name"] = "codex-usage-tray", ["version"] = "1.3.0" },
        ["instructions"] = $"Use get_usage before switching. The tray can manage any number of locally saved accounts. Agent switching is allowed only when active five-hour usage is at least {Program.AgentSwitchThresholdPercent}%; choose a configured account below that threshold. Chats subscribed with subscribe_chat are resumed after automatic switches. Automatic switching waits until tracked Codex turns have all stopped. The PreToolUse hook attaches the caller session ID to subscription and switch tools. After requesting a manual switch, preserve remaining usage by checkpointing minimally and yielding immediately."
    };

    private static JsonObject ToolsList() => new()
    {
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "get_usage",
                ["title"] = "Get Codex account usage",
                ["description"] = "Refresh and return usage, status, active/selected state, and reset times for every locally saved Codex account.",
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
                ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false }
            },
            new JsonObject
            {
                ["name"] = "subscribe_chat",
                ["title"] = "Subscribe this chat to tray account switches",
                ["description"] = "Subscribe only the calling chat for continuation after an automatic tray account switch. The tray PreToolUse hook attaches this chat's session ID and working folder.",
                ["inputSchema"] = IdentityInputSchema(),
                ["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["openWorldHint"] = false }
            },
            new JsonObject
            {
                ["name"] = "unsubscribe_chat",
                ["title"] = "Unsubscribe this chat from tray account switches",
                ["description"] = "Remove only the calling chat from automatic switch continuation.",
                ["inputSchema"] = IdentityInputSchema(),
                ["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["openWorldHint"] = false }
            },
            new JsonObject
            {
                ["name"] = "switch_account",
                ["title"] = "Switch Codex account",
                ["description"] = $"Queue an account switch and restart Codex Desktop after the calling chat yields. The tray enforces at least {Program.AgentSwitchThresholdPercent}% active five-hour usage and a target below {Program.AgentSwitchThresholdPercent}%. The PreToolUse hook injects chat identity. Preserve remaining usage by checkpointing minimally and yielding immediately; do not finish the section using the remaining {100 - Program.AgentSwitchThresholdPercent}%.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["target_label"] = new JsonObject { ["type"] = "string", ["description"] = "Configured account label from get_usage." },
                        ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Brief reason for switching." },
                        ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Injected by the tray PreToolUse hook. Do not fill manually." },
                        ["working_directory"] = new JsonObject { ["type"] = "string", ["description"] = "Injected by the tray PreToolUse hook. Do not fill manually." }
                    },
                    ["required"] = new JsonArray("target_label", "reason"),
                    ["additionalProperties"] = false
                },
                ["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = true, ["openWorldHint"] = false }
            }
        }
    };

    private static JsonObject IdentityInputSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Injected by the tray PreToolUse hook." },
            ["working_directory"] = new JsonObject { ["type"] = "string", ["description"] = "Injected by the tray PreToolUse hook." }
        },
        ["additionalProperties"] = false
    };

    private static async Task<JsonObject> CallToolAsync(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"]?.AsObject() ?? new JsonObject();
        JsonObject request;
        if (name == "get_usage")
        {
            request = new JsonObject { ["operation"] = "status" };
        }
        else if (name == "subscribe_chat" || name == "unsubscribe_chat")
        {
            request = new JsonObject
            {
                ["operation"] = name == "subscribe_chat" ? "subscribe-chat" : "unsubscribe-chat",
                ["session_id"] = arguments["session_id"]?.GetValue<string>(),
                ["working_directory"] = arguments["working_directory"]?.GetValue<string>()
            };
        }
        else if (name == "switch_account")
        {
            var label = arguments["target_label"]?.GetValue<string>();
            var reason = arguments["reason"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(reason))
                throw new McpException(-32602, "target_label and reason are required.");
            request = new JsonObject
            {
                ["operation"] = "switch",
                ["target_label"] = label,
                ["reason"] = reason,
                ["session_id"] = arguments["session_id"]?.GetValue<string>(),
                ["working_directory"] = arguments["working_directory"]?.GetValue<string>()
            };
        }
        else
        {
            throw new McpException(-32602, "Unknown tool.");
        }

        EnsureTrayRunning();
        var result = await LocalAgentBridge.SendAsync(request);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) }),
            ["structuredContent"] = result.DeepClone(),
            ["isError"] = false
        };
    }

    private static void EnsureTrayRunning()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new IOException("The Codex Usage Tray executable could not be located.");

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static JsonObject ErrorResponse(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private sealed class McpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
