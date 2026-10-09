using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace CodexUsageTray;

/// <summary>A readable quota badge independent of Windows' square notification icon slot.</summary>
internal sealed class UsageBadge : Form
{
    private readonly ToolTip _tooltip = new() { InitialDelay = 300, AutoPopDelay = 15_000 };
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray", "badge-position.json");
    private string _text = "—% • —%";
    private Point? _dragStart;
    private Point _dragOrigin;
    private bool _positionLoaded;
    private bool _badgeEnabled = true;
    private readonly System.Windows.Forms.Timer _visibilityTimer = new() { Interval = 300 };

    public UsageBadge(ContextMenuStrip menu)
    {
        Text = "Codex usage badge";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(25, 25, 25);
        ForeColor = Color.White;
        ContextMenuStrip = menu;
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.StandardDoubleClick, true);
        _tooltip.SetToolTip(this, "Codex usage consumed: hourly • weekly\nDrag horizontally to move · right-click for controls");
        _visibilityTimer.Tick += (_, _) => UpdateVisibility();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Remove the automatic Windows 11 window border as well as the painted border.
        var noBorder = 0xfffffffeu;
        DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(uint));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplySize();
        ResetPosition();
        try
        {
            var saved = JsonSerializer.Deserialize<PointData>(File.ReadAllText(_settingsPath));
            if (saved is not null && Screen.AllScreens.Any(screen => screen.Bounds.Contains(new Point(saved.X, saved.Y))))
            {
                Location = new Point(saved.X, saved.Y);
                DockToBottom();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warning("badge-position-load-failed", $"exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
        }
        _positionLoaded = true;
        _visibilityTimer.Start();
        UpdateVisibility();
    }

    public void SetEnabled(bool enabled)
    {
        _badgeEnabled = enabled;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (IsDisposed || !IsHandleCreated) return;
        var foreground = GetForegroundWindow();
        var shouldHide = !_badgeEnabled || IsFullScreen(foreground) || IsStartOrSearch(foreground);
        if (shouldHide)
        {
            if (Visible) Hide();
            return;
        }

        if (!Visible) Show();
        // Explorer raises the taskbar after closing Start. Restore our place above
        // it without activating this window or covering our own context menu.
        if (ContextMenuStrip?.Visible != true)
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    private bool IsFullScreen(IntPtr foreground)
    {
        if (SHQueryUserNotificationState(out var state) == 0 && state is >= 1 and <= 4)
            return true;
        if (foreground == IntPtr.Zero || foreground == Handle) return false;
        var className = new StringBuilder(128);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;
        if (!GetWindowRect(foreground, out var bounds)) return false;
        var rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        var display = Screen.FromRectangle(rectangle).Bounds;
        return rectangle.Left <= display.Left && rectangle.Top <= display.Top
            && rectangle.Right >= display.Right && rectangle.Bottom >= display.Bottom;
    }

    private static bool IsStartOrSearch(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName is "StartMenuExperienceHost" or "SearchHost" or "SearchApp";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    private void ApplySize()
    {
        var scale = DeviceDpi / 96f;
        ClientSize = new Size((int)Math.Round(208 * scale), (int)Math.Round(46 * scale));
        using var shape = RoundedRectangle(new RectangleF(0, 0, Width, Height), 9 * scale);
        var previous = Region;
        Region = new Region(shape);
        previous?.Dispose();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplySize();
        DockToBottom();
        Invalidate();
    }

    public void SetUsage(UsageWindow hourly, UsageWindow? weekly, string details)
    {
        _text = $"{hourly.UsedPercent}% • {(weekly is null ? "—" : weekly.UsedPercent.ToString())}%";
        _tooltip.SetToolTip(this, "Codex usage consumed: hourly • weekly\n" + details + "\nDrag horizontally to move · right-click for controls");
        if (_dragStart is null) DockToBottom();
        Invalidate();
    }

    public void SetUnavailable(string reason)
    {
        _text = "—% • —%";
        _tooltip.SetToolTip(this, "Codex usage unavailable\n" + reason + "\nRight-click to refresh");
        Invalidate();
    }

    public void ResetPosition()
    {
        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        var scale = DeviceDpi / 96f;
        var tray = FindWindow("Shell_TrayWnd", null);
        var notificationArea = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        var left = area.Right - Width - (int)(360 * scale);
        if (notificationArea != IntPtr.Zero && GetWindowRect(notificationArea, out var trayBounds))
            left = trayBounds.Left - Width;
        Location = new Point(Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width)), area.Bottom - Height);
        if (_positionLoaded) SavePosition();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var scale = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Segoe UI Semibold", 24 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        var values = _text.Split(" • ", StringSplitOptions.None);
        if (values.Length != 2)
        {
            TextRenderer.DrawText(e.Graphics, _text, font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            return;
        }

        var textFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var leftSize = TextRenderer.MeasureText(e.Graphics, values[0], font, Size.Empty, textFlags);
        var rightSize = TextRenderer.MeasureText(e.Graphics, values[1], font, Size.Empty, textFlags);
        var sparkleWidth = 15 * scale;
        var gap = 8 * scale;
        var totalWidth = leftSize.Width + rightSize.Width + sparkleWidth + gap * 2;
        var x = (ClientSize.Width - totalWidth) / 2f;
        var y = (ClientSize.Height - Math.Max(leftSize.Height, rightSize.Height)) / 2f;

        TextRenderer.DrawText(e.Graphics, values[0], font, new Point((int)x, (int)y), ForeColor, textFlags);
        x += leftSize.Width + gap;
        DrawGeminiSparkle(e.Graphics, x + sparkleWidth / 2f, ClientSize.Height / 2f, scale);
        x += sparkleWidth + gap;
        TextRenderer.DrawText(e.Graphics, values[1], font, new Point((int)x, (int)y), ForeColor, textFlags);
    }

    private void DrawGeminiSparkle(Graphics graphics, float centerX, float centerY, float scale)
    {
        // Single monochrome four-point sparkle.
        using var main = new GraphicsPath();
        var radius = 6.2f * scale;
        main.AddPolygon(new[]
        {
            new PointF(centerX, centerY - radius),
            new PointF(centerX + radius * .28f, centerY - radius * .27f),
            new PointF(centerX + radius, centerY),
            new PointF(centerX + radius * .28f, centerY + radius * .27f),
            new PointF(centerX, centerY + radius),
            new PointF(centerX - radius * .28f, centerY + radius * .27f),
            new PointF(centerX - radius, centerY),
            new PointF(centerX - radius * .28f, centerY - radius * .27f)
        });
        using var monochrome = new SolidBrush(ForeColor);
        graphics.FillPath(monochrome, main);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragStart = Cursor.Position;
        _dragOrigin = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not { } start) return;
        var cursor = Cursor.Position;
        Location = new Point(_dragOrigin.X + cursor.X - start.X, _dragOrigin.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _dragStart is null) return;
        _dragStart = null;
        Capture = false;
        DockToBottom();
        SavePosition();
    }

    private void DockToBottom()
    {
        var area = Screen.FromRectangle(Bounds).Bounds;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), area.Bottom - Height);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle bounds);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    private void SavePosition()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new PointData(Left, Top)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("badge-position-save-failed", $"exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _visibilityTimer.Stop();
            _visibilityTimer.Dispose();
            _tooltip.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record PointData(int X, int Y);
}
