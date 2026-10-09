using System.Text;

namespace CodexUsageTray;

/// <summary>
/// Small local diagnostic log. Never pass credentials, account labels, or raw
/// exception messages to this class; callers should log operation identifiers
/// and exception metadata only.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageTray", "app.log");

    public static void Info(string operation, string detail = "") => Write("INFO", operation, detail);
    public static void Warning(string operation, string detail = "") => Write("WARN", operation, detail);

    public static void Error(string operation, Exception exception, string detail = "")
    {
        var metadata = $"exception={exception.GetType().Name}; hresult=0x{exception.HResult:X8}";
        if (exception is System.ComponentModel.Win32Exception win32)
            metadata += $"; win32={win32.NativeErrorCode}";
        if (!string.IsNullOrWhiteSpace(detail)) metadata += "; " + detail;
        Write("ERROR", operation, metadata);
    }

    private static void Write(string level, string operation, string detail)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
                var info = new FileInfo(PathName);
                if (info.Exists && info.Length > 1_000_000)
                {
                    var previous = PathName + ".1";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(PathName, previous);
                }

                var safeOperation = Sanitize(operation);
                var safeDetail = Sanitize(detail);
                File.AppendAllText(PathName,
                    $"{DateTimeOffset.Now:O} [{level}] {safeOperation}{(safeDetail.Length == 0 ? "" : " · " + safeDetail)}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must not prevent account setup or switching.
        }
    }

    private static string Sanitize(string value) => new(value
        .Where(character => !char.IsControl(character))
        .Take(400)
        .ToArray());
}
