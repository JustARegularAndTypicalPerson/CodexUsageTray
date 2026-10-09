using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageTray;

internal sealed record ChatSubscription(string SessionId, string WorkingDirectory, DateTimeOffset SubscribedAt);
internal sealed record ActiveCodexTurn(string SessionId, string TurnId, DateTimeOffset StartedAt);
internal sealed record ChatSessionState(List<ChatSubscription> Subscriptions, List<ActiveCodexTurn> ActiveTurns);

internal static class ChatSessionStore
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageTray", "chat-sessions.json");
    private static readonly string MutexName = "Local\\CodexUsageTray.ChatSessionStore.v1";
    private static readonly TimeSpan StaleTurnAge = TimeSpan.FromHours(12);

    public static void Subscribe(string sessionId, string workingDirectory)
    {
        ValidateSessionId(sessionId);
        Mutate(state =>
        {
            var existing = state.Subscriptions.FindIndex(item => item.SessionId == sessionId);
            var item = new ChatSubscription(sessionId, SafePath(workingDirectory), DateTimeOffset.Now);
            if (existing >= 0) state.Subscriptions[existing] = item;
            else state.Subscriptions.Add(item);
        });
    }

    public static bool Unsubscribe(string sessionId)
    {
        ValidateSessionId(sessionId);
        var removed = false;
        Mutate(state => removed = state.Subscriptions.RemoveAll(item => item.SessionId == sessionId) > 0);
        return removed;
    }

    public static IReadOnlyList<ChatSubscription> GetSubscriptions() => Read().Subscriptions;

    public static void MarkTurnStarted(string sessionId, string turnId)
    {
        ValidateSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(turnId) || turnId.Length > 200 || turnId.Any(char.IsControl)) return;
        Mutate(state =>
        {
            state.ActiveTurns.RemoveAll(item => item.SessionId == sessionId && item.TurnId == turnId);
            state.ActiveTurns.Add(new ActiveCodexTurn(sessionId, turnId, DateTimeOffset.Now));
        });
    }

    public static bool MarkTurnStopped(string sessionId, string? turnId)
    {
        ValidateSessionId(sessionId);
        var removed = false;
        Mutate(state =>
        {
            if (string.IsNullOrWhiteSpace(turnId))
                removed = state.ActiveTurns.RemoveAll(item => item.SessionId == sessionId) > 0;
            else
                removed = state.ActiveTurns.RemoveAll(item => item.SessionId == sessionId && item.TurnId == turnId) > 0;
        });
        return removed;
    }

    public static int GetActiveTurnCount() => Read().ActiveTurns.Count;

    private static void Mutate(Action<ChatSessionState> mutation)
    {
        using var mutex = new Mutex(false, MutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException("The chat session registry is busy.");
        }
        catch (AbandonedMutexException) { }

        var state = ReadUnlocked();
        PruneStaleTurns(state);
        mutation(state);
        WriteUnlocked(state);
    }

    private static ChatSessionState Read()
    {
        using var mutex = new Mutex(false, MutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException("The chat session registry is busy.");
        }
        catch (AbandonedMutexException) { }

        var state = ReadUnlocked();
        PruneStaleTurns(state);
        return state;
    }

    private static ChatSessionState ReadUnlocked()
    {
        if (!File.Exists(StatePath)) return new([], []);
        try
        {
            var state = JsonSerializer.Deserialize<ChatSessionState>(File.ReadAllText(StatePath));
            return state is { Subscriptions: not null, ActiveTurns: not null } ? state : new([], []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error("chat-session-state-read-failed", ex);
            throw new InvalidDataException("The tray chat subscription state could not be read safely.", ex);
        }
    }

    private static void WriteUnlocked(ChatSessionState state)
    {
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var staging = StatePath + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(state));
            File.Move(staging, StatePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("chat-session-state-staging-cleanup-failed");
            }
        }
    }

    private static void PruneStaleTurns(ChatSessionState state)
    {
        var cutoff = DateTimeOffset.Now - StaleTurnAge;
        var removed = state.ActiveTurns.RemoveAll(item => item.StartedAt < cutoff);
        if (removed > 0) AppLog.Warning("stale-chat-turns-pruned", $"count={removed}");
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 200 || sessionId.Any(char.IsControl))
            throw new ArgumentException("A valid Codex chat session ID is required.", nameof(sessionId));
    }

    private static string SafePath(string path) => new((path ?? "")
        .Where(character => !char.IsControl(character))
        .Take(1000)
        .ToArray());
}
