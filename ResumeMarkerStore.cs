using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageTray;

internal sealed record ResumeMarker(string SessionId, string TargetLabel, string WorkingDirectory, DateTimeOffset CreatedAt);

internal static class ResumeMarkerStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray", "pending-resumes");
    public static readonly TimeSpan MarkerLifetime = TimeSpan.FromMinutes(10);

    public static string GetPath(string sessionId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
        return Path.Combine(DirectoryPath, key + ".json");
    }

    public static bool HasPending(string sessionId) => File.Exists(GetPath(sessionId));

    public static void Delete(string sessionId)
    {
        try { File.Delete(GetPath(sessionId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("resume-marker-delete-failed", $"exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
        }
    }

    public static void Write(string sessionId, string targetLabel, string workingDirectory)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = GetPath(sessionId);
        var staging = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(new ResumeMarker(
                sessionId, targetLabel, workingDirectory, DateTimeOffset.Now)));
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("resume-marker-staging-cleanup-failed", $"exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
            }
        }
    }

    public static ResumeMarker? Consume(string sessionId)
    {
        var path = GetPath(sessionId);
        var claimed = path + ".claimed-" + Guid.NewGuid().ToString("N");
        try { File.Move(path, claimed); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        try
        {
            var marker = JsonSerializer.Deserialize<ResumeMarker>(File.ReadAllText(claimed));
            if (marker is null || !string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(marker.TargetLabel)
                || DateTimeOffset.Now - marker.CreatedAt > MarkerLifetime
                || marker.CreatedAt > DateTimeOffset.Now.AddMinutes(1))
            {
                AppLog.Warning("resume-marker-expired-or-invalid");
                return null;
            }
            return marker;
        }
        finally
        {
            try { File.Delete(claimed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("resume-marker-cleanup-failed", $"exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
            }
        }
    }
}
