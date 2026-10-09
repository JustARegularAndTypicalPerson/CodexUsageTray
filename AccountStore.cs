using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace CodexUsageTray;

internal sealed class AccountProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "Account";
    public string? ProtectedCredentials { get; set; }
    public bool HasCredentials => !string.IsNullOrWhiteSpace(ProtectedCredentials);
}

internal sealed class AccountStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray", "accounts.json");
    private readonly List<AccountProfile> _profiles;
    private Guid? _selectedId;
    private Guid? _activeId;

    public IReadOnlyList<AccountProfile> Profiles => _profiles;
    public Guid SelectedId => _selectedId ?? _profiles[0].Id;
    public Guid? ActiveId => _activeId;
    public AccountProfile SelectedProfile => _profiles.First(p => p.Id == SelectedId);
    public AccountProfile? ActiveProfile => _activeId is null ? null : _profiles.FirstOrDefault(p => p.Id == _activeId);

    public AccountStore()
    {
        if (!File.Exists(_path))
        {
            _profiles = Enumerable.Range(1, 4).Select(i => new AccountProfile { Label = $"Account {i}" }).ToList();
            Save();
            return;
        }

        var saved = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("Account settings could not be read.");
        _profiles = saved.Profiles ?? [];
        if (_profiles.Count == 0)
            _profiles.Add(new AccountProfile { Label = "Account 1" });
        _selectedId = _profiles.Any(p => p.Id == saved.SelectedId) ? saved.SelectedId : _profiles[0].Id;
        _activeId = saved.ActiveId is { } active && _profiles.Any(p => p.Id == active) ? active : null;
        Save();
    }

    public void SetLabel(Guid id, string label)
    {
        var profile = Get(id);
        var cleaned = new string(label.Where(c => !char.IsControl(c)).Take(32).ToArray()).Trim();
        profile.Label = string.IsNullOrWhiteSpace(cleaned) ? "Account" : cleaned;
        Save();
    }

    public AccountProfile AddProfile()
    {
        var used = _profiles.Select(p => p.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1;
        string label;
        do { label = $"Account {number++}"; } while (used.Contains(label));
        var profile = new AccountProfile { Label = label };
        _profiles.Add(profile);
        Save();
        return profile;
    }

    public void Select(Guid id)
    {
        _ = Get(id);
        _selectedId = id;
        Save();
    }

    public void SetActive(Guid id)
    {
        var profile = Get(id);
        if (!profile.HasCredentials) throw new InvalidOperationException("Sign in to this account before setting it as current.");
        _activeId = id;
        Save();
    }

    /// <summary>Align saved tray selection with the identity in Codex's live auth file.</summary>
    public (bool IsKnown, bool Changed) ReconcileCurrentSignIn()
    {
        var authPath = CodexDesktopSwitcher.GetAuthPath();
        if (!File.Exists(authPath)) return (false, false);

        var currentBytes = File.ReadAllBytes(authPath);
        try
        {
            var currentAccountId = ReadAccountId(currentBytes);
            if (string.IsNullOrWhiteSpace(currentAccountId)) return (false, false);

            var matchingProfiles = new List<Guid>();
            foreach (var profile in _profiles.Where(profile => profile.HasCredentials))
            {
                byte[] savedBytes;
                try { savedBytes = Unprotect(profile.Id); }
                catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
                {
                    AppLog.Warning("account-identity-check-failed",
                        $"slot={_profiles.IndexOf(profile) + 1}; exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
                    continue;
                }

                try
                {
                    if (string.Equals(ReadAccountId(savedBytes), currentAccountId, StringComparison.Ordinal))
                        matchingProfiles.Add(profile.Id);
                }
                finally { CryptographicOperations.ZeroMemory(savedBytes); }
            }

            // Duplicate slots can intentionally contain the same sign-in. Keep
            // the previously active one when it still matches, then selection,
            // then the first matching profile.
            var matchedId = matchingProfiles.Contains(_activeId ?? Guid.Empty) ? _activeId
                : matchingProfiles.Contains(_selectedId ?? Guid.Empty) ? _selectedId
                : matchingProfiles.FirstOrDefault();

            if (matchedId is null || matchedId == Guid.Empty)
            {
                if (_activeId is null) return (false, false);
                _activeId = null;
                Save();
                return (false, true);
            }

            var activeChanged = _activeId != matchedId;
            var selectionChanged = _selectedId != matchedId;
            if (activeChanged)
            {
                _activeId = matchedId;
                _selectedId = matchedId;
                // Capture the currently active sign-in so future switches use
                // current tokens, while keeping them encrypted at rest.
                ProtectUpdated(matchedId.Value, currentBytes);
            }
            else if (selectionChanged)
            {
                _selectedId = matchedId;
                Save();
            }
            var changed = activeChanged || selectionChanged;
            return (true, changed);
        }
        finally { CryptographicOperations.ZeroMemory(currentBytes); }
    }

    public void CaptureCurrent(Guid id)
    {
        var profile = Get(id);
        var authPath = CodexDesktopSwitcher.GetAuthPath();
        if (!File.Exists(authPath))
            throw new FileNotFoundException("Codex's current sign-in file was not found. Use Sign in to add this account instead.");
        var credentials = File.ReadAllBytes(authPath);
        try
        {
            ValidateCredentialPayload(credentials);
            var protectedCredentials = Dpapi.Protect(credentials);
            try
            {
                var previousCredentials = profile.ProtectedCredentials;
                var previousActiveId = _activeId;
                var previousSelectedId = _selectedId;
                profile.ProtectedCredentials = Convert.ToBase64String(protectedCredentials);
                _activeId = id;
                _selectedId = id;
                try { Save(); }
                catch
                {
                    profile.ProtectedCredentials = previousCredentials;
                    _activeId = previousActiveId;
                    _selectedId = previousSelectedId;
                    throw;
                }
            }
            finally { CryptographicOperations.ZeroMemory(protectedCredentials); }
        }
        finally { CryptographicOperations.ZeroMemory(credentials); }
    }

    public CaptureInspection InspectCurrent(Guid targetId)
    {
        _ = Get(targetId);
        var authPath = CodexDesktopSwitcher.GetAuthPath();
        if (!File.Exists(authPath))
            throw new FileNotFoundException("Codex's current sign-in file was not found.");

        var currentBytes = File.ReadAllBytes(authPath);
        try
        {
            ValidateCredentialPayload(currentBytes);
            var currentAccountId = ReadAccountId(currentBytes);
            if (string.IsNullOrWhiteSpace(currentAccountId))
                return new CaptureInspection(false, false, []);

            string? targetAccountId = null;
            var matchingOtherProfiles = new List<string>();
            foreach (var profile in _profiles.Where(profile => profile.HasCredentials))
            {
                byte[] savedBytes;
                try { savedBytes = Unprotect(profile.Id); }
                catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
                {
                    AppLog.Warning("account-identity-check-failed",
                        $"slot={_profiles.IndexOf(profile) + 1}; exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
                    continue;
                }

                try
                {
                    var savedAccountId = ReadAccountId(savedBytes);
                    if (profile.Id == targetId)
                        targetAccountId = savedAccountId;
                    else if (string.Equals(savedAccountId, currentAccountId, StringComparison.Ordinal))
                        matchingOtherProfiles.Add(profile.Label);
                }
                finally { CryptographicOperations.ZeroMemory(savedBytes); }
            }

            var replacesDifferentSignIn = !string.IsNullOrWhiteSpace(targetAccountId)
                && !string.Equals(targetAccountId, currentAccountId, StringComparison.Ordinal);
            return new CaptureInspection(true, replacesDifferentSignIn, matchingOtherProfiles);
        }
        finally { CryptographicOperations.ZeroMemory(currentBytes); }
    }

    public byte[] Unprotect(Guid id)
    {
        var payload = Get(id).ProtectedCredentials;
        if (string.IsNullOrWhiteSpace(payload)) throw new InvalidOperationException("This account has not been signed in.");
        var bytes = Dpapi.Unprotect(Convert.FromBase64String(payload));
        try
        {
            ValidateCredentialPayload(bytes);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public void ProtectUpdated(Guid id, byte[] credentials)
    {
        ValidateCredentialPayload(credentials);
        Get(id).ProtectedCredentials = Convert.ToBase64String(Dpapi.Protect(credentials));
        Save();
    }

    public async Task SignInAsync(Guid id, Action<string>? onStatus = null)
    {
        var profileNumber = Profiles.ToList().FindIndex(profile => profile.Id == id) + 1;
        var cli = CodexClient.FindCodex();
        var tempHome = Path.Combine(Path.GetTempPath(), "CodexUsageTray", "login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        var scriptPath = Path.Combine(tempHome, "sign-in.cmd");
        try
        {
            AppLog.Info("sign-in-requested", $"slot={profileNumber}");
            var script = string.Join(Environment.NewLine,
            [
                "@echo off",
                "setlocal",
                $"set \"CODEX_HOME={tempHome}\"",
                $"\"{cli}\" login",
                "set \"login_exit=%ERRORLEVEL%\"",
                "echo.",
                "if \"%login_exit%\"==\"0\" (echo Sign-in completed. Saving this account; this window will close shortly.) else (echo Sign-in failed. This window will close shortly.)",
                "if \"%login_exit%\"==\"0\" (timeout /t 3 /nobreak >nul) else (timeout /t 8 /nobreak >nul)",
                "exit /b %login_exit%"
            ]);
            File.WriteAllText(scriptPath, script, Encoding.UTF8);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    Arguments = $"/d /c \"\"{scriptPath}\"\"",
                    WorkingDirectory = tempHome,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                }
            };
            if (!process.Start()) throw new InvalidOperationException("The administrator sign-in window did not open.");
            AppLog.Info("sign-in-window-started", $"slot={profileNumber}; pid={process.Id}");
            onStatus?.Invoke("Finish sign-in in window");
            await process.WaitForExitAsync();
            AppLog.Info("sign-in-window-exited", $"slot={profileNumber}; exit={process.ExitCode}");
            if (process.ExitCode != 0) throw new InvalidOperationException("Sign-in did not complete. The existing saved sign-in was kept.");
            var authPath = Path.Combine(tempHome, "auth.json");
            if (!File.Exists(authPath)) throw new InvalidOperationException("Codex did not save a sign-in for this slot. The existing saved sign-in was kept.");
            var credentials = File.ReadAllBytes(authPath);
            try { ProtectUpdated(id, credentials); }
            finally { CryptographicOperations.ZeroMemory(credentials); }
            AppLog.Info("sign-in-saved", $"slot={profileNumber}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppLog.Warning("sign-in-canceled", $"slot={profileNumber}; win32={ex.NativeErrorCode}");
            throw new OperationCanceledException("Administrator approval was canceled. The existing saved sign-in was kept.", ex);
        }
        catch (Exception ex)
        {
            AppLog.Error("sign-in-failed", ex, $"slot={profileNumber}");
            throw;
        }
        finally
        {
            SecureDeleteTempHome(tempHome, profileNumber);
        }
    }

    private static void SecureDeleteTempHome(string tempHome, int profileNumber)
    {
        var authPath = Path.Combine(tempHome, "auth.json");
        try
        {
            if (File.Exists(authPath))
            {
                using var file = new FileStream(authPath, FileMode.Open, FileAccess.Write, FileShare.None);
                var remaining = file.Length;
                var zeros = new byte[8192];
                file.Position = 0;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(zeros.Length, remaining);
                    file.Write(zeros, 0, count);
                    remaining -= count;
                }
                file.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("sign-in-temp-auth-wipe-failed",
                $"slot={profileNumber}; exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
        }
        try { Directory.Delete(tempHome, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("sign-in-temp-cleanup-failed",
                $"slot={profileNumber}; exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
        }
    }

    private AccountProfile Get(Guid id) => _profiles.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException("Account profile is no longer available.");

    private static void ValidateCredentialPayload(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Codex sign-in data is invalid.");
        }
        catch (JsonException) { throw new InvalidDataException("Codex sign-in data is invalid."); }
    }

    private static string? ReadAccountId(byte[] authBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(authBytes);
            if (document.RootElement.TryGetProperty("tokens", out var tokens)
                && tokens.ValueKind == JsonValueKind.Object
                && tokens.TryGetProperty("account_id", out var accountId)
                && accountId.ValueKind == JsonValueKind.String)
                return accountId.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var data = new StoreData(_profiles, _selectedId ?? _profiles[0].Id, _activeId);
        var staging = _path + ".new";
        File.WriteAllText(staging, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(staging, _path, overwrite: true);
    }

    private sealed record StoreData(List<AccountProfile> Profiles, Guid SelectedId, Guid? ActiveId);
}

internal sealed record CaptureInspection(bool IdentityKnown, bool ReplacesDifferentSignIn, IReadOnlyList<string> OtherMatchingLabels);

internal sealed class AccountManagerForm : Form
{
    private readonly AccountStore _store;
    private readonly FlowLayoutPanel _rows;
    private readonly List<AccountRow> _accountRows = [];
    private int _signInsInProgress;

    public AccountManagerForm(AccountStore store)
    {
        _store = store;
        Text = "Manage Codex accounts";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(930, 680);
        BackColor = Color.FromArgb(245, 247, 250);
        ForeColor = Color.FromArgb(32, 39, 50);
        Font = new Font("Segoe UI", 9f);

        var header = new Panel
        {
            BackColor = Color.FromArgb(30, 41, 59),
            Location = Point.Empty,
            Size = new Size(930, 76),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(header);
        header.Controls.Add(new Label
        {
            Text = "Codex accounts",
            Font = new Font("Segoe UI Semibold", 16f),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(20, 10)
        });
        header.Controls.Add(new Label
        {
            Text = "Sign in to add an account, or copy the sign-in currently open in Codex into a row.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(213, 221, 232),
            AutoSize = true,
            Location = new Point(22, 43)
        });

        var columns = new Panel
        {
            Location = new Point(14, 87),
            Size = new Size(902, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        columns.Controls.Add(ColumnLabel("ACCOUNT", 8, 150));
        columns.Controls.Add(ColumnLabel("STATUS", 180, 150));
        columns.Controls.Add(ColumnLabel("ACTIONS", 340, 250));
        columns.Controls.Add(ColumnLabel("CURRENT", 610, 240));
        Controls.Add(columns);

        _rows = new FlowLayoutPanel
        {
            Location = new Point(14, 112),
            Size = new Size(902, 480),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Padding = new Padding(0, 2, 8, 0),
            BackColor = BackColor
        };
        Controls.Add(_rows);
        foreach (var profile in store.Profiles) AddRow(profile);

        var add = new Button { Text = "+  Add account", Location = new Point(14, 624), Size = new Size(132, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        StyleButton(add, Color.FromArgb(37, 99, 235), Color.White);
        add.Click += (_, _) => AddRow(_store.AddProfile());
        Controls.Add(add);
        var close = new Button { Text = "Done", DialogResult = DialogResult.OK, Location = new Point(832, 624), Size = new Size(84, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        StyleButton(close, Color.FromArgb(30, 41, 59), Color.White);
        Controls.Add(close);
        AcceptButton = close;
        FormClosing += (_, eventArgs) =>
        {
            if (_signInsInProgress > 0)
            {
                eventArgs.Cancel = true;
                MessageBox.Show("Wait for the sign-in window to finish before closing account management.",
                    "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SaveLabels();
        };
    }

    private void AddRow(AccountProfile profile)
    {
        var row = new Panel
        {
            Width = 866,
            Height = 48,
            Margin = new Padding(0, 0, 0, 7),
            BackColor = Color.White,
            Padding = new Padding(4)
        };
        var name = new TextBox { Text = profile.Label, Location = new Point(8, 10), Size = new Size(160, 27), MaxLength = 32, BorderStyle = BorderStyle.FixedSingle };
        var state = new Label
        {
            Text = profile.HasCredentials ? "Ready" : "Sign-in needed",
            Location = new Point(180, 15),
            Size = new Size(150, 20),
            ForeColor = profile.HasCredentials ? Color.FromArgb(21, 128, 61) : Color.FromArgb(180, 83, 9)
        };
        name.TextChanged += (_, _) =>
        {
            try { _store.SetLabel(profile.Id, name.Text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("account-label-save-failed", ex, $"slot={_store.Profiles.ToList().FindIndex(item => item.Id == profile.Id) + 1}");
                state.Text = "Label not saved";
            }
        };
        row.Controls.Add(name);
        row.Controls.Add(state);

        var saveCurrent = new Button { Text = "Copy Codex sign-in", Location = new Point(340, 8), Size = new Size(155, 32) };
        saveCurrent.AccessibleDescription = "Copies the sign-in already open in Codex into this row. It does not sign in to a different account.";
        StyleButton(saveCurrent, Color.FromArgb(232, 238, 247), Color.FromArgb(30, 41, 59));
        saveCurrent.Click += (_, _) => SaveCurrent(profile);
        row.Controls.Add(saveCurrent);

        var signIn = new Button { Text = "Sign in…", Location = new Point(505, 8), Size = new Size(90, 32) };
        StyleButton(signIn, Color.FromArgb(219, 234, 254), Color.FromArgb(29, 78, 216));
        signIn.Click += async (_, _) => await SignIn(profile, state, signIn, saveCurrent);
        row.Controls.Add(signIn);

        var active = new Label
        {
            Text = profile.Id == _store.ActiveId ? "Current in Codex" : "",
            Location = new Point(610, 15),
            Size = new Size(230, 22),
            ForeColor = Color.FromArgb(71, 85, 105),
        };
        row.Controls.Add(active);
        _rows.Controls.Add(row);
        _accountRows.Add(new AccountRow(profile, name, state, active));
    }

    private void SaveCurrent(AccountProfile profile)
    {
        var slot = _store.Profiles.ToList().FindIndex(item => item.Id == profile.Id) + 1;
        try
        {
            var inspection = _store.InspectCurrent(profile.Id);
            var explanation = "This copies the sign-in already open in Codex into this row. It does not switch Codex to another account. To add a different account, cancel and use Sign in….";
            if (!inspection.IdentityKnown)
                explanation += Environment.NewLine + Environment.NewLine + "The app cannot verify whether this sign-in is already saved elsewhere.";
            if (inspection.ReplacesDifferentSignIn)
                explanation += Environment.NewLine + Environment.NewLine + "This will replace a different sign-in currently saved in this row.";
            if (inspection.OtherMatchingLabels.Count > 0)
                explanation += Environment.NewLine + Environment.NewLine
                    + "This sign-in is already saved in: " + string.Join(", ", inspection.OtherMatchingLabels)
                    + ". Saving it here creates a duplicate row; it will not add another account.";

            if (MessageBox.Show(explanation + Environment.NewLine + Environment.NewLine + "Continue?",
                    "Copy current Codex sign-in", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                AppLog.Info("save-current-sign-in-canceled", $"slot={slot}");
                return;
            }

            _store.CaptureCurrent(profile.Id);
            UpdateStates();
            AppLog.Info("save-current-sign-in-succeeded", $"slot={slot}");
            MessageBox.Show($"Copied the sign-in currently open in Codex into {profile.Label}. The row is now marked as current.",
                "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("save-current-sign-in-failed", ex, $"slot={slot}");
            var reason = ex switch
            {
                FileNotFoundException => "Codex's local sign-in file was not found. Open Codex and sign in, then try again.",
                InvalidDataException => "Codex's local sign-in file is not a supported sign-in. Use Sign in… to add this account instead.",
                UnauthorizedAccessException => "Windows denied access to Codex's local sign-in or the tray app's saved accounts.",
                CryptographicException => "Windows could not protect the sign-in for this Windows account.",
                IOException => "A file could not be read or the saved account settings could not be written.",
                _ => "The sign-in could not be saved. The previous saved sign-in was kept. Check %LOCALAPPDATA%\\CodexUsageTray\\app.log for safe diagnostic details."
            };
            MessageBox.Show(reason,
                "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SignIn(AccountProfile profile, Label state, Button signIn, Button saveCurrent)
    {
        _signInsInProgress++;
        var succeeded = false;
            signIn.Enabled = false;
        saveCurrent.Enabled = false;
        signIn.Text = "Opening…";
        state.Text = "Opening admin window…";
        try
        {
            await _store.SignInAsync(profile.Id, status => state.Text = status == "Finish sign-in in window" ? "Finish in admin window" : status);
            succeeded = true;
            state.Text = "Ready";
        }
        catch (Exception ex)
        {
            AppLog.Error("sign-in-ui-failed", ex, $"slot={_store.Profiles.ToList().FindIndex(item => item.Id == profile.Id) + 1}");
            state.Text = profile.HasCredentials ? "Saved sign-in kept" : "Sign-in needed";
            MessageBox.Show(ex is FileNotFoundException
                    ? "Codex CLI was not found. Install Codex CLI to add accounts."
                    : ex is OperationCanceledException
                        ? ex.Message
                        : ex is InvalidOperationException
                            ? ex.Message
                            : "The administrator sign-in did not complete. The existing saved sign-in was kept; you can try again. Details are in %LOCALAPPDATA%\\CodexUsageTray\\app.log.",
                "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _signInsInProgress--;
            signIn.Enabled = true;
            signIn.Text = "Sign in…";
            saveCurrent.Enabled = true;
            UpdateStates();
            if (!succeeded && profile.HasCredentials)
            {
                state.Text = "Saved sign-in kept";
                state.ForeColor = Color.FromArgb(180, 83, 9);
            }
        }
    }

    private void SaveLabels()
    {
        Exception? firstError = null;
        foreach (var row in _accountRows)
        {
            try { _store.SetLabel(row.Profile.Id, row.Name.Text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                firstError ??= ex;
                AppLog.Error("account-label-save-failed", ex, $"slot={_store.Profiles.ToList().FindIndex(item => item.Id == row.Profile.Id) + 1}");
            }
        }
        if (firstError is not null)
            MessageBox.Show("One or more account labels could not be saved. Check the tray app log and try again.",
                "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void UpdateStates()
    {
        foreach (var row in _accountRows)
        {
            var profile = row.Profile;
            if (row.State.Text is not "Signing in as administrator…" and not "Opening admin window…" and not "Finish in admin window")
            {
                row.State.Text = profile.HasCredentials ? "Ready" : "Sign-in needed";
                row.State.ForeColor = profile.HasCredentials ? Color.FromArgb(21, 128, 61) : Color.FromArgb(180, 83, 9);
            }
            row.Active.Text = profile.Id == _store.ActiveId ? "Current in Codex" : "";
        }
    }

    private static Label ColumnLabel(string text, int x, int width) => new()
    {
        Text = text,
        Location = new Point(x, 3),
        Size = new Size(width, 18),
        Font = new Font("Segoe UI Semibold", 8f),
        ForeColor = Color.FromArgb(100, 116, 139)
    };

    private static void StyleButton(Button button, Color background, Color foreground)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.Font = new Font("Segoe UI Semibold", 9f);
        button.Cursor = Cursors.Hand;
    }

    private sealed record AccountRow(AccountProfile Profile, TextBox Name, Label State, Label Active);
}

internal static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexUsageTray account profile v1");
    private const uint UiForbidden = 0x1;

    public static byte[] Protect(byte[] clear) => Transform(clear, protect: true);
    public static byte[] Unprotect(byte[] protectedBytes) => Transform(protectedBytes, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = CreateBlob(input);
        var entropyBlob = CreateBlob(Entropy);
        DataBlob outputBlob = default;
        try
        {
            var success = protect
                ? CryptProtectData(ref inputBlob, "Codex Usage Tray account", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob);
            if (!success) throw new CryptographicException("Windows could not protect account data.");
            var output = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            FreeBlob(inputBlob);
            FreeBlob(entropyBlob);
            if (outputBlob.Data != IntPtr.Zero)
            {
                ClearBlob(outputBlob);
                LocalFree(outputBlob.Data);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            ClearBlob(blob);
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private static void ClearBlob(DataBlob blob)
    {
        if (blob.Size > 0 && blob.Data != IntPtr.Zero)
            Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
