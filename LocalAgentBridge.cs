using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexUsageTray;

internal sealed class LocalAgentBridge(Func<JsonObject, Task<(JsonNode Result, Func<Task>? AfterReply)>> handler)
{
    public const string PipeName = "CodexUsageTray.AgentBridge.v1";
    private readonly Func<JsonObject, Task<(JsonNode Result, Func<Task>? AfterReply)>> _handler = handler;

    public void Start() => _ = Task.Run(ServeAsync);

    private async Task ServeAsync()
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                Func<Task>? afterReply = null;
                JsonObject response;
                try
                {
                    var request = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException();
                    var handled = await _handler(request);
                    response = new JsonObject { ["result"] = handled.Result };
                    afterReply = handled.AfterReply;
                }
                catch (Exception ex)
                {
                    response = new JsonObject { ["error"] = string.IsNullOrWhiteSpace(ex.Message) ? "The tray request failed." : ex.Message };
                }

                await writer.WriteLineAsync(response.ToJsonString());
                await writer.FlushAsync();
                if (afterReply is not null) _ = Task.Run(afterReply);
            }
            catch (IOException) { await Task.Delay(250); }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { await Task.Delay(250); }
        }
    }

    public static async Task<JsonNode> SendAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        var connected = false;
        for (var attempt = 0; attempt < 40 && !connected; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(250, cancellationToken);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(request.ToJsonString());
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) throw new IOException("The tray app did not respond.");
                var response = JsonNode.Parse(line)?.AsObject() ?? throw new IOException("The tray app returned an invalid response.");
                if (response["error"] is JsonNode error)
                    throw new InvalidOperationException(error.GetValue<string>());
                return response["result"]?.DeepClone() ?? new JsonObject();
            }
            catch (TimeoutException) { }
            catch (IOException) when (attempt < 39) { }
            if (!connected) await Task.Delay(125, cancellationToken);
        }
        throw new IOException("The Codex Usage Tray could not be reached. Start the tray app and try again.");
    }
}
