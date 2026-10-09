using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace CodexUsageTray;

internal static class Program
{
    internal static int AgentSwitchThresholdPercent => TraySettings.LoadAgentSwitchThresholdPercent();

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--mcp")
        {
            McpServer.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--hook-session-start")
        {
            SessionStartHook.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--hook-before-tool")
        {
            CodexHookSupport.RunBeforeTool();
            return;
        }
        if (args.Length == 1 && args[0] == "--hook-stop")
        {
            CodexHookSupport.RunStopAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--hook-turn-start")
        {
            CodexHookSupport.RunTurnStart();
            return;
        }
        if (args.Length == 1 && args[0] == "--hook-interrupt")
        {
            CodexHookSupport.RunInterruptAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--self-test-auto-switch")
        {
            AttachParentConsole();
            Environment.ExitCode = AutoSwitchPolicy.RunSelfTest(Console.Out);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, eventArgs) =>
            AppLog.Error("ui-thread-unhandled", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                AppLog.Error("appdomain-unhandled", exception, $"terminating={eventArgs.IsTerminating}");
            else
                AppLog.Warning("appdomain-unhandled", $"terminating={eventArgs.IsTerminating}; exception-object={eventArgs.ExceptionObject?.GetType().Name ?? "null"}");
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Error("unobserved-task", eventArgs.Exception);
            eventArgs.SetObserved();
        };
        AppLog.Info("app-started", $"admin={IsCurrentProcessElevated()}");
        using var instance = new Mutex(initiallyOwned: true, "Local\\CodexUsageTray", out var created);
        if (!created)
        {
            AppLog.Warning("duplicate-app-instance-exited");
            return;
        }
        Application.Run(new TrayApplicationContext());
        AppLog.Info("app-exited");
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void AttachParentConsole()
    {
        _ = AttachConsole(uint.MaxValue);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripLabel _statusLabel;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _manageAccountsItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _switchThresholdItem;
    private readonly List<ToolStripMenuItem> _accountItems = [];
    private readonly UsageBadge _badge;
    private readonly AccountStore _accounts = new();
    private readonly Dictionary<Guid, AccountUsage> _usage = [];
    private readonly SynchronizationContext _uiContext;
    private readonly LocalAgentBridge _agentBridge;
    private bool _refreshing;
    private bool _switching;
    private bool _currentSignInUnmatched;
    private bool _identityStatusLogged;
    private readonly Dictionary<string, TaskCompletionSource> _pendingSwitchStops = new(StringComparer.Ordinal);
    private bool _autoSwitchQueued;

    public TrayApplicationContext()
    {
        var hooksAdded = false;
        try { hooksAdded = CodexHookInstaller.InstallOrUpdate(); }
        catch (Exception ex) { AppLog.Error("global-codex-hooks-install-failed", ex); }

        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _menu = new ContextMenuStrip();
        var menu = _menu;
        _statusLabel = new ToolStripLabel("Loading account usage…")
        {
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };
        menu.Items.Add(_statusLabel);
        menu.Items.Add(new ToolStripSeparator());
        _manageAccountsItem = new ToolStripMenuItem("Manage accounts…");
        _manageAccountsItem.Click += (_, _) => ManageAccounts();
        menu.Items.Add(_manageAccountsItem);
        _refreshItem = new ToolStripMenuItem("Refresh now");
        _refreshItem.Click += async (_, _) => await RefreshAsync();
        menu.Items.Add(_refreshItem);
        _switchThresholdItem = new ToolStripMenuItem();
        _switchThresholdItem.Click += (_, _) => ConfigureSwitchThreshold();
        menu.Items.Add(_switchThresholdItem);
        RebuildAccountMenuItems();
        menu.Items.Add(new ToolStripSeparator());
        _badge = new UsageBadge(menu);
        var badgeToggle = new ToolStripMenuItem("Show percentage badge") { Checked = true, CheckOnClick = true };
        badgeToggle.CheckedChanged += (_, _) => _badge.SetEnabled(badgeToggle.Checked);
        menu.Items.Add(badgeToggle);
        menu.Items.Add("Reset badge position", null, (_, _) => _badge.ResetPosition());
        menu.Items.Add("Usage details", null, (_, _) => ShowDetails());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => UpdateMenu();

        _icon = new NotifyIcon
        {
            Icon = DrawUsageIcon(null, null),
            Text = "Codex usage · loading…",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowDetails();
        _badge.DoubleClick += (_, _) => ShowDetails();
        _badge.Show();
        if (hooksAdded)
            _icon.ShowBalloonTip(8000, "Codex Usage Tray hooks added",
                "Review and trust the new hooks in Codex before automatic switching can run.", ToolTipIcon.Info);

        _timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        UpdateMenu();
        _ = RefreshAsync();
        _agentBridge = new LocalAgentBridge(HandleAgentRequestAsync);
        _agentBridge.Start();
    }

    private async Task<(JsonNode Result, Func<Task>? AfterReply)> HandleAgentRequestAsync(JsonObject request)
    {
        var operation = request["operation"]?.GetValue<string>();
        if (operation == "status")
        {
            await RunOnUiAsync(RefreshAsync);
            return (BuildAgentStatus(), null);
        }

        if (operation == "session-stopped")
        {
            var stoppedSessionId = request["session_id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(stoppedSessionId))
            {
                await RunOnUiAsync(() =>
                {
                    if (_pendingSwitchStops.TryGetValue(stoppedSessionId, out var signal))
                        signal.TrySetResult();
                    return Task.CompletedTask;
                });
            }
            return (new JsonObject { ["acknowledged"] = true }, null);
        }

        if (operation == "subscribe-chat" || operation == "unsubscribe-chat")
        {
            var chatSessionId = request["session_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(chatSessionId))
                throw new InvalidOperationException("The calling chat could not be identified. Enable the tray PreToolUse hook first.");
            if (operation == "subscribe-chat")
            {
                ChatSessionStore.Subscribe(chatSessionId, request["working_directory"]?.GetValue<string>() ?? "");
                AppLog.Info("chat-subscribed-for-auto-switch");
                return (new JsonObject { ["subscribed"] = true, ["resume_on_switch"] = true }, null);
            }

            var removed = ChatSessionStore.Unsubscribe(chatSessionId);
            AppLog.Info("chat-unsubscribed-from-auto-switch", $"was_subscribed={removed}");
            return (new JsonObject { ["subscribed"] = false, ["removed"] = removed }, null);
        }

        if (operation == "turn-ended")
            return await HandleTurnEndedAsync(request);

        if (operation != "switch")
            throw new InvalidOperationException("Unknown local tray operation.");

        var label = request["target_label"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(label))
            throw new InvalidOperationException("Choose a configured account label.");
        var sessionId = request["session_id"]?.GetValue<string>();
        var workingDirectory = request["working_directory"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 200 || sessionId.Any(char.IsControl))
            throw new InvalidOperationException("The calling Codex chat could not be identified. Enable the tray app's PreToolUse hook before requesting an agent switch.");

        Guid targetId = Guid.Empty;
        TaskCompletionSource? stopCompletion = null;
        List<ChatSubscription> resumeSubscriptions = [];
        await RunOnUiAsync(async () =>
        {
            if (_autoSwitchQueued)
                throw new InvalidOperationException("An automatic switch is already queued after the tracked chats yield.");
            await RefreshAsync();
            var target = _accounts.Profiles.FirstOrDefault(p =>
                p.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (target is null) throw new InvalidOperationException("No account has that label.");
            if (!target.HasCredentials) throw new InvalidOperationException("That account needs a sign-in first.");

            var active = _accounts.ActiveProfile;
            if (active is null) throw new InvalidOperationException("The active account is unknown. Mark the current sign-in in Manage accounts first.");
            if (active.Id == target.Id) throw new InvalidOperationException("That account is already active.");

            var activeShort = GetFiveHourWindow(_usage.GetValueOrDefault(active.Id)?.Snapshot);
            if (activeShort is null || activeShort.UsedPercent < Program.AgentSwitchThresholdPercent)
                throw new InvalidOperationException($"Account switching by an agent is available only when active five-hour usage is at least {Program.AgentSwitchThresholdPercent}%.");

            var targetShort = GetFiveHourWindow(_usage.GetValueOrDefault(target.Id)?.Snapshot);
            if (targetShort is null)
                throw new InvalidOperationException("The target account's five-hour usage is unavailable. Refresh usage and try again.");
            if (targetShort.UsedPercent >= Program.AgentSwitchThresholdPercent)
                throw new InvalidOperationException($"The target account is also at {Program.AgentSwitchThresholdPercent}% or higher. Choose an account with more available five-hour usage.");

            targetId = target.Id;
            resumeSubscriptions = ChatSessionStore.GetSubscriptions().ToList();
            if (!resumeSubscriptions.Any(item => item.SessionId == sessionId))
                resumeSubscriptions.Add(new ChatSubscription(sessionId, workingDirectory ?? "", DateTimeOffset.Now));
            foreach (var subscription in resumeSubscriptions)
                ResumeMarkerStore.Write(subscription.SessionId, target.Label, subscription.WorkingDirectory);
            stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingSwitchStops[sessionId] = stopCompletion;
            UpdateMenu();
            UpdateSelectedDisplay();
        });

        var accepted = new JsonObject
        {
            ["queued"] = true,
            ["target_label"] = label,
            ["wait_for_chat_to_stop"] = true,
            ["message"] = $"The tray will wait for this chat's Stop hook before closing Codex. The active account has reached the {Program.AgentSwitchThresholdPercent}% switch threshold: do not spend the remaining {100 - Program.AgentSwitchThresholdPercent}% finishing the current section or starting more work. Save only the minimum checkpoint needed to preserve the current state, give a concise handoff describing the next step, and yield immediately without more tool calls. The tray will wait up to three minutes for this chat to yield. On resume, verify the active account with get_usage before continuing the goal."
        };
        return (accepted, async () =>
        {
            var stopSignal = stopCompletion!.Task;
            var completed = await Task.WhenAny(stopSignal, Task.Delay(TimeSpan.FromMinutes(3)));
            if (completed != stopSignal)
            {
                AppLog.Warning("account-switch-chat-stop-timeout");
                foreach (var subscription in resumeSubscriptions)
                    ResumeMarkerStore.Delete(subscription.SessionId);
                await RunOnUiAsync(() =>
                {
                    _pendingSwitchStops.Remove(sessionId);
                    _statusLabel.Text = "Switch canceled · this chat did not yield in time";
                    UpdateMenu();
                    return Task.CompletedTask;
                });
                return;
            }
            await WaitForTrackedTurnsToStopAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));
            await RunOnUiAsync(async () =>
            {
                _pendingSwitchStops.Remove(sessionId);
                while (_refreshing || _switching)
                    await Task.Delay(250);
                await SwitchAccountAsync(targetId);
                if (_accounts.ActiveId != targetId || _currentSignInUnmatched)
                {
                    foreach (var subscription in resumeSubscriptions)
                        ResumeMarkerStore.Delete(subscription.SessionId);
                }
            });
        });
    }

    private async Task<(JsonNode Result, Func<Task>? AfterReply)> HandleTurnEndedAsync(JsonObject request)
    {
        var sessionId = request["session_id"]?.GetValue<string>();
        var turnId = request["turn_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sessionId))
            return (new JsonObject { ["acknowledged"] = true }, null);

        var tracked = ChatSessionStore.MarkTurnStopped(sessionId, turnId);
        var shouldCheckUsage = false;
        await RunOnUiAsync(() =>
        {
            if (_pendingSwitchStops.TryGetValue(sessionId, out var signal)) signal.TrySetResult();
            if (tracked && ChatSessionStore.GetActiveTurnCount() == 0
                && !_autoSwitchQueued && _pendingSwitchStops.Count == 0)
            {
                _autoSwitchQueued = true;
                shouldCheckUsage = true;
            }
            return Task.CompletedTask;
        });

        if (!shouldCheckUsage)
            return (new JsonObject { ["acknowledged"] = true, ["auto_switch_queued"] = false }, null);

        return (new JsonObject { ["acknowledged"] = true, ["auto_switch_check_queued"] = true }, async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await WaitForTrackedTurnsToStopAsync();
            try
            {
                await RunOnUiAsync(async () =>
                {
                    while (_refreshing || _switching) await Task.Delay(250);
                    var subscriptions = ChatSessionStore.GetSubscriptions().ToList();
                    await RefreshAsync();
                    var active = _accounts.ActiveProfile;
                    var activeIdentityMatched = !_currentSignInUnmatched && active is not null;
                    var desktopRunning = CodexDesktopSwitcher.IsDesktopRunning();
                    var activeWindow = active is null ? null : GetFiveHourWindow(_usage.GetValueOrDefault(active.Id)?.Snapshot);
                    var target = _accounts.Profiles
                        .Where(profile => profile.HasCredentials && profile.Id != active?.Id)
                        .Select(profile => (Profile: profile, Window: GetFiveHourWindow(_usage.GetValueOrDefault(profile.Id)?.Snapshot)))
                        .Where(item => item.Window is not null && item.Window.UsedPercent < Program.AgentSwitchThresholdPercent)
                        .OrderBy(item => item.Window!.UsedPercent)
                        .FirstOrDefault();
                    if (!AutoSwitchPolicy.ShouldSwitch(new AutoSwitchInputs(
                            activeWindow?.UsedPercent,
                            Program.AgentSwitchThresholdPercent,
                            ChatSessionStore.GetActiveTurnCount(),
                            target.Profile is not null,
                            activeIdentityMatched,
                            desktopRunning)))
                    {
                        AppLog.Info("automatic-account-switch-not-needed-or-unavailable");
                        return;
                    }

                    var targetProfile = target.Profile!;
                    foreach (var subscription in subscriptions)
                        ResumeMarkerStore.Write(subscription.SessionId, targetProfile.Label, subscription.WorkingDirectory);
                    _statusLabel.Text = $"All turns ended · switching to {targetProfile.Label}";
                    AppLog.Info("automatic-account-switch-started", $"subscribed_chats={subscriptions.Count}; threshold={Program.AgentSwitchThresholdPercent}");
                    UpdateMenu();
                    UpdateSelectedDisplay();
                    await SwitchAccountAsync(targetProfile.Id);
                    if (_accounts.ActiveId != targetProfile.Id || _currentSignInUnmatched)
                    {
                        foreach (var subscription in subscriptions)
                            ResumeMarkerStore.Delete(subscription.SessionId);
                    }
                });
            }
            catch (Exception ex) { AppLog.Error("automatic-account-switch-failed", ex); }
            finally
            {
                await RunOnUiAsync(() =>
                {
                    _autoSwitchQueued = false;
                    UpdateMenu();
                    return Task.CompletedTask;
                });
            }
        });
    }

    private static async Task WaitForTrackedTurnsToStopAsync()
    {
        while (ChatSessionStore.GetActiveTurnCount() > 0)
            await Task.Delay(TimeSpan.FromSeconds(1));
    }

    private JsonObject BuildAgentStatus()
    {
        var accounts = new JsonArray();
        foreach (var profile in _accounts.Profiles)
        {
            var snapshot = _usage.GetValueOrDefault(profile.Id)?.Snapshot;
            var shortWindow = GetFiveHourWindow(snapshot);
            var weekly = snapshot is null ? null : LongWindow(snapshot);
            accounts.Add(new JsonObject
            {
                ["label"] = profile.Label,
                ["configured"] = profile.HasCredentials,
                ["is_active"] = profile.Id == _accounts.ActiveId,
                ["is_selected"] = profile.Id == _accounts.SelectedId,
                ["status"] = profile.HasCredentials
                    ? shortWindow is null ? _usage.GetValueOrDefault(profile.Id)?.Message ?? "Usage unavailable" : "Ready"
                    : "Sign-in needed",
                ["five_hour_used_percent"] = shortWindow?.UsedPercent,
                ["five_hour_remaining_percent"] = shortWindow is null ? null : 100 - shortWindow.UsedPercent,
                ["five_hour_resets_at"] = shortWindow?.ResetsAt?.ToString("O"),
                ["weekly_used_percent"] = weekly?.UsedPercent,
                ["weekly_remaining_percent"] = weekly is null ? null : 100 - weekly.UsedPercent,
                ["weekly_resets_at"] = weekly?.ResetsAt?.ToString("O"),
                ["updated_at"] = snapshot?.UpdatedAt.ToString("O")
            });
        }

        return new JsonObject
        {
            ["active_label"] = _accounts.ActiveProfile?.Label,
            ["selected_label"] = _currentSignInUnmatched ? null : _accounts.SelectedProfile.Label,
            ["active_account_matched"] = !_currentSignInUnmatched,
            ["codex_running"] = CodexDesktopSwitcher.IsDesktopRunning(),
            ["agent_switch_threshold_percent"] = Program.AgentSwitchThresholdPercent,
            ["subscribed_chat_count"] = ChatSessionStore.GetSubscriptions().Count,
            ["active_turn_count"] = ChatSessionStore.GetActiveTurnCount(),
            ["accounts"] = accounts
        };
    }

    private static UsageWindow? GetFiveHourWindow(UsageSnapshot? snapshot) =>
        snapshot?.Windows.FirstOrDefault(w => w.DurationMinutes == 300);

    private Task RunOnUiAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(async _ =>
        {
            try { await action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }, null);
        return completion.Task;
    }

    private void ManageAccounts()
    {
        if (_refreshing || _switching) return;
        _timer.Stop();
        try
        {
            using var dialog = new AccountManagerForm(_accounts);
            _ = dialog.ShowDialog();
            // Edits persist as they are made. Refresh the tray for every close
            // path, including the window's X button.
            RebuildAccountMenuItems();
            UpdateMenu();
            _ = RefreshAsync();
        }
        finally { _timer.Start(); }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _switching) return;
        _refreshing = true;
        _statusLabel.Text = $"Refreshing {_accounts.Profiles.Count} account profiles…";
        UpdateMenu();
        try
        {
            if (CodexDesktopSwitcher.IsDesktopRunning())
            {
                try
                {
                    // Give Windows and Codex a moment to settle after account
                    // activation before reading the live auth file.
                    await Task.Delay(500);
                    var reconciliation = _accounts.ReconcileCurrentSignIn();
                    _currentSignInUnmatched = !reconciliation.IsKnown;
                    if (!_identityStatusLogged)
                    {
                        if (reconciliation.IsKnown)
                        {
                            var slot = _accounts.Profiles.ToList().FindIndex(profile => profile.Id == _accounts.ActiveId) + 1;
                            AppLog.Info("active-account-verified", $"slot={slot}");
                        }
                        else
                        {
                            AppLog.Warning("active-account-not-matched");
                        }
                        _identityStatusLogged = true;
                    }
                    if (reconciliation.Changed)
                    {
                        var slot = _accounts.Profiles.ToList().FindIndex(profile => profile.Id == _accounts.ActiveId) + 1;
                        AppLog.Warning("active-account-reconciled", $"slot={slot}");
                    }
                    UpdateMenu();
                    UpdateSelectedDisplay();
                }
                catch (Exception ex)
                {
                    _currentSignInUnmatched = true;
                    AppLog.Error("active-account-reconcile-failed", ex);
                }
            }
            else
            {
                _currentSignInUnmatched = false;
            }

            foreach (var profile in _accounts.Profiles)
            {
                if (!profile.HasCredentials)
                {
                    _usage[profile.Id] = AccountUsage.NeedsSignIn;
                    continue;
                }

                try
                {
                    var snapshot = await Task.Run(() => CodexClient.ReadUsageAsync(profile.Id, _accounts));
                    _usage[profile.Id] = AccountUsage.Ready(snapshot);
                }
                catch (Exception ex)
                {
                    AppLog.Error("usage-refresh-failed", ex, $"slot={_accounts.Profiles.ToList().FindIndex(item => item.Id == profile.Id) + 1}");
                    _usage[profile.Id] = AccountUsage.Unavailable(SafeError(ex));
                }
                UpdateMenu();
                UpdateSelectedDisplay();
            }
            _statusLabel.Text = "Usage refreshed · right-click to select or switch accounts";
        }
        finally
        {
            _refreshing = false;
            UpdateMenu();
            UpdateSelectedDisplay();
        }
    }

    private async Task SwitchAccountAsync(Guid accountId)
    {
        if (_switching || _refreshing) return;
        var target = _accounts.Profiles.First(p => p.Id == accountId);
        if (!target.HasCredentials)
        {
            MessageBox.Show($"Set up sign-in for {target.Label} first using Manage accounts.",
                "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var active = _accounts.ActiveProfile;
        if (active is null)
        {
            if (CodexDesktopSwitcher.IsDesktopRunning())
            {
                MessageBox.Show("Codex is open, but the tray app does not know which saved account is active. In Manage accounts, choose “Save current sign-in” for that account, then click the account again.",
                    "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        else if (active.Id == target.Id && CodexDesktopSwitcher.IsDesktopRunning())
        {
            _accounts.Select(target.Id);
            UpdateMenu();
            UpdateSelectedDisplay();
            return;
        }

        _switching = true;
        _timer.Stop();
        _statusLabel.Text = $"Switching to {target.Label}…";
        UpdateMenu();
        try
        {
            var success = await Task.Run(() => CodexDesktopSwitcher.Switch(active, target, _accounts));
            if (success)
            {
                _accounts.SetActive(target.Id);
                _accounts.Select(target.Id);
                _currentSignInUnmatched = false;
                _statusLabel.Text = $"Account changed to {target.Label}";
                _switching = false;
                await RefreshAsyncAfterSwitch();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("account-switch-ui-failed", ex, $"slot={_accounts.Profiles.ToList().FindIndex(item => item.Id == target.Id) + 1}");
            MessageBox.Show(ex is InvalidOperationException ? ex.Message : SafeError(ex),
                "Could not switch Codex account", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _switching = false;
            _timer.Start();
            UpdateMenu();
            UpdateSelectedDisplay();
        }
    }

    private async Task RefreshAsyncAfterSwitch()
    {
        await Task.Delay(2_000);
        await RefreshAsync();
    }

    private void UpdateMenu()
    {
        _switchThresholdItem.Text = $"Agent switch threshold: {Program.AgentSwitchThresholdPercent}%…";
        for (var i = 0; i < _accounts.Profiles.Count; i++)
        {
            var profile = _accounts.Profiles[i];
            var state = _usage.GetValueOrDefault(profile.Id);
            var prefix = !_currentSignInUnmatched && profile.Id == _accounts.SelectedId ? "✓ " : "   ";
            var active = profile.Id == _accounts.ActiveId ? " · current in Codex" : "";
            _accountItems[i].Checked = _accounts.ActiveId is { } activeId && profile.Id == activeId;
            _accountItems[i].Text = $"{prefix}{profile.Label}{active}  |  {FormatMenuUsage(state, profile)}";
            _accountItems[i].Enabled = !_refreshing && !_switching && !_autoSwitchQueued;
        }
        _manageAccountsItem.Enabled = !_refreshing && !_switching && !_autoSwitchQueued;
        _refreshItem.Enabled = !_refreshing && !_switching && !_autoSwitchQueued;
        _switchThresholdItem.Enabled = !_refreshing && !_switching && !_autoSwitchQueued;
    }

    private void ConfigureSwitchThreshold()
    {
        using var dialog = new Form
        {
            Text = "Agent switch threshold",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(370, 154),
            Font = SystemFonts.MessageBoxFont
        };

        var explanation = new Label
        {
            Text = "Allow agent-requested switches when five-hour usage reaches:",
            AutoSize = false,
            Location = new Point(14, 16),
            Size = new Size(340, 34)
        };
        var percent = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 99,
            Value = Program.AgentSwitchThresholdPercent,
            DecimalPlaces = 0,
            Location = new Point(16, 60),
            Size = new Size(90, 26)
        };
        var percentLabel = new Label
        {
            Text = "% used",
            AutoSize = true,
            Location = new Point(112, 65)
        };
        var save = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(196, 108),
            Size = new Size(74, 28)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(278, 108),
            Size = new Size(74, 28)
        };
        dialog.Controls.AddRange([explanation, percent, percentLabel, save, cancel]);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            TraySettings.SaveAgentSwitchThresholdPercent((int)percent.Value);
            AppLog.Info("agent-switch-threshold-updated", $"percent={percent.Value}");
            UpdateMenu();
        }
        catch (Exception ex)
        {
            AppLog.Error("agent-switch-threshold-save-failed", ex);
            MessageBox.Show("The threshold could not be saved. Try again.", "Codex Usage Tray",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RebuildAccountMenuItems()
    {
        foreach (var item in _accountItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _accountItems.Clear();
        for (var i = 0; i < _accounts.Profiles.Count; i++)
        {
            var profile = _accounts.Profiles[i];
            var item = new ToolStripMenuItem
            {
                ToolTipText = "Click to switch Codex to this account. Codex closes and reopens; save work first."
            };
            var accountId = profile.Id;
            item.Click += async (_, _) => await SwitchAccountAsync(accountId);
            _accountItems.Add(item);
            _menu.Items.Insert(2 + i, item);
        }
        UpdateMenu();
    }

    private static string FormatMenuUsage(AccountUsage? state, AccountProfile profile)
    {
        if (!profile.HasCredentials) return "Sign-in needed";
        if (state?.Snapshot is not { } snapshot) return state?.Message ?? "Waiting for usage";
        var hourly = ShortWindow(snapshot);
        var weekly = LongWindow(snapshot);
        if (hourly is null && weekly is null) return "Usage not returned";
        var shortText = hourly is null ? "Short —" : $"Short {100 - hourly.UsedPercent}% left ({ResetLabel(hourly)})";
        var weeklyText = weekly is null ? "Weekly —" : $"Weekly {100 - weekly.UsedPercent}% left ({ResetLabel(weekly)})";
        return $"{shortText} · {weeklyText}";
    }

    private static string ResetLabel(UsageWindow window) => window.ResetsAt is null ? "reset unknown" : window.ResetsAt.Value.ToString("dd HH:mm");

    private void UpdateSelectedDisplay()
    {
        var profile = _accounts.SelectedProfile;
        if (_currentSignInUnmatched)
        {
            const string message = "Codex is open with a sign-in that is not saved in the tray.";
            SetTrayIcon(DrawUsageIcon(null, null));
            _icon.Text = ShortTooltip("Codex · sign-in not matched");
            _statusLabel.Text = "Codex sign-in not matched · save the current sign-in";
            _badge.SetUnavailable(message + " Open Manage accounts and save the current sign-in to a slot.");
            return;
        }

        var state = _usage.GetValueOrDefault(profile.Id);
        var snapshot = state?.Snapshot;
        var hourly = snapshot is null ? null : ShortWindow(snapshot);
        var weekly = snapshot is null ? null : LongWindow(snapshot);
        var weeklyDistinct = weekly is not null && !ReferenceEquals(hourly, weekly) ? weekly : null;
        var label = profile.Label;
        if (hourly is null)
        {
            SetTrayIcon(DrawUsageIcon(null, null));
            _icon.Text = ShortTooltip($"Codex · {label} · {(profile.HasCredentials ? state?.Message ?? "usage unavailable" : "sign-in needed")}");
            _statusLabel.Text = $"{label}: {(profile.HasCredentials ? state?.Message ?? "usage unavailable" : "sign-in needed")}";
            _badge.SetUnavailable($"{label} · {state?.Message ?? "Usage unavailable. Right-click to refresh or manage accounts."}");
            return;
        }

        var remaining = 100 - hourly.UsedPercent;
        SetTrayIcon(DrawUsageIcon(hourly, weeklyDistinct));
        _icon.Text = ShortTooltip($"Codex · {label} · H {remaining}% left" + (weeklyDistinct is null ? "" : $" · W {100 - weeklyDistinct.UsedPercent}% left"));
        var details = weeklyDistinct is null
            ? FormatWindow(hourly, "Short")
            : $"{FormatWindow(hourly, "Short")}  |  {FormatWindow(weeklyDistinct, "Weekly")}";
        _statusLabel.Text = $"{label} · {details}";
        _badge.SetUsage(hourly, weeklyDistinct, $"{label}\n{details}");
    }

    private void ShowDetails()
    {
        if (_currentSignInUnmatched)
        {
            MessageBox.Show("Codex is open with a sign-in that is not saved in the tray. Open Manage accounts and save the current sign-in to a slot first.",
                "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var profile = _accounts.SelectedProfile;
        var state = _usage.GetValueOrDefault(profile.Id);
        var lines = new List<string> { $"Codex usage · {profile.Label}" };
        if (state?.Snapshot is { } snapshot)
        {
            var hourly = ShortWindow(snapshot);
            var weekly = LongWindow(snapshot);
            if (hourly is not null) lines.Add(FormatWindow(hourly, "Short window"));
            if (weekly is not null && !ReferenceEquals(hourly, weekly)) lines.Add(FormatWindow(weekly, "Weekly"));
            lines.Add("Updated " + snapshot.UpdatedAt.ToString("HH:mm"));
        }
        else lines.Add(state?.Message ?? (profile.HasCredentials ? "Usage has not been loaded." : "Sign-in needed."));
        MessageBox.Show(string.Join(Environment.NewLine, lines), "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static UsageWindow? ShortWindow(UsageSnapshot snapshot) =>
        snapshot.Windows.OrderBy(w => w.DurationMinutes ?? int.MaxValue).FirstOrDefault();

    private static UsageWindow? LongWindow(UsageSnapshot snapshot) =>
        snapshot.Windows.OrderByDescending(w => w.DurationMinutes ?? 0).FirstOrDefault();

    private static string FormatWindow(UsageWindow window, string label)
    {
        var reset = window.ResetsAt is null ? "" : $" · resets {window.ResetsAt:dd HH:mm}";
        return $"{label}: {100 - window.UsedPercent}% left{reset}";
    }

    private static string SafeError(Exception ex)
    {
        // Avoid presenting process output or credential-bearing data to the user.
        return ex switch
        {
            FileNotFoundException => "Codex CLI was not found. Install Codex CLI to read usage and manage sign-ins.",
            TimeoutException => "Codex did not respond in time. Right-click to try again.",
            UnauthorizedAccessException => "Windows denied access to Codex account data.",
            _ => "Codex usage is unavailable. Check that the account is signed in, then right-click to retry."
        };
    }

    private static string ShortTooltip(string value) => value.Length <= 63 ? value : value[..60] + "…";

    private void SetTrayIcon(Icon icon)
    {
        var previous = _icon.Icon;
        _icon.Icon = icon;
        if (!ReferenceEquals(previous, icon)) previous?.Dispose();
    }

    private static Icon DrawUsageIcon(UsageWindow? shortWindow, UsageWindow? weekly)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.Black);
            using var track = new SolidBrush(Color.FromArgb(100, 100, 100));
            using var foreground = new SolidBrush(Color.White);
            using var frame = new Pen(Color.White, 2f);
            using var square = RoundedRectangle(1, 1, 30, 30, 7);
            graphics.FillPath(background, square);
            graphics.DrawPath(frame, square);
            DrawVerticalBar(graphics, track, foreground, shortWindow?.UsedPercent ?? 0, 6, 8, 20);
            DrawVerticalBar(graphics, track, foreground, weekly?.UsedPercent ?? 0, 18, 8, 20);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static void DrawVerticalBar(Graphics graphics, Brush track, Brush foreground, int usedPercent, int left, int top, int height)
    {
        const int width = 8;
        graphics.FillRectangle(track, left, top, width, height);
        var fillHeight = (int)Math.Round(height * Math.Clamp(usedPercent, 0, 100) / 100d);
        if (fillHeight > 0) graphics.FillRectangle(foreground, left, top + height - fillHeight, width, fillHeight);
    }

    private static GraphicsPath RoundedRectangle(int x, int y, int width, int height, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        var icon = _icon.Icon;
        _icon.Dispose();
        icon?.Dispose();
        _badge.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed record UsageWindow(int UsedPercent, int? DurationMinutes, DateTimeOffset? ResetsAt);
internal sealed record UsageSnapshot(UsageWindow? Primary, UsageWindow? Secondary, DateTimeOffset UpdatedAt)
{
    public IEnumerable<UsageWindow> Windows => new[] { Primary, Secondary }.OfType<UsageWindow>();
}

internal sealed record AccountUsage(UsageSnapshot? Snapshot, string? Message)
{
    public static AccountUsage Ready(UsageSnapshot snapshot) => new(snapshot, null);
    public static AccountUsage Unavailable(string message) => new(null, message);
    public static AccountUsage NeedsSignIn { get; } = new(null, "Sign-in needed");
}

internal static class CodexClient
{
    public static string FindCodex()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        var candidates = Directory.Exists(root) ? Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories) : [];
        if (candidates.Length > 0) return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
        var pathEntry = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => Path.Combine(path.Trim('"'), "codex.exe"))
            .FirstOrDefault(File.Exists);
        return pathEntry ?? throw new FileNotFoundException();
    }

    public static async Task<UsageSnapshot> ReadUsageAsync(Guid profileId, AccountStore accounts)
    {
        var codexPath = FindCodex();
        var isActive = accounts.ActiveId == profileId;
        var scratch = isActive ? CodexDesktopSwitcher.GetCodexHome()
            : Path.Combine(Path.GetTempPath(), "CodexUsageTray", Guid.NewGuid().ToString("N"));
        if (!isActive) Directory.CreateDirectory(scratch);
        try
        {
            if (!isActive)
            {
                var profileCredentials = accounts.Unprotect(profileId);
                try { File.WriteAllBytes(Path.Combine(scratch, "auth.json"), profileCredentials); }
                finally { CryptographicOperations.ZeroMemory(profileCredentials); }
            }
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = codexPath,
                    Arguments = "app-server --stdio",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.Environment["CODEX_HOME"] = scratch;
            if (!process.Start()) throw new InvalidOperationException();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ignoredErrorOutput = process.StandardError.ReadToEndAsync();
            try
            {
                var input = process.StandardInput;
                var output = process.StandardOutput;
                await input.WriteLineAsync("{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"codex-usage-tray\",\"title\":\"Codex Usage Tray\",\"version\":\"2.0.0\"},\"capabilities\":{}}}");
                await input.FlushAsync(timeout.Token);
                await ReadResponseAsync(output, 1, timeout.Token);
                await input.WriteLineAsync("{\"method\":\"initialized\"}");
                await input.WriteLineAsync("{\"method\":\"account/rateLimits/read\",\"id\":2}");
                await input.FlushAsync(timeout.Token);
                var response = await ReadResponseAsync(output, 2, timeout.Token);
                var rateLimits = response["result"]?["rateLimits"] ?? response["result"];
                if (rateLimits is null) throw new InvalidOperationException();

                return new UsageSnapshot(ParseWindow(rateLimits["primary"]), ParseWindow(rateLimits["secondary"]), DateTimeOffset.Now);
            }
            finally
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(); } catch (InvalidOperationException) { }
                try { await ignoredErrorOutput; } catch (IOException) { }
            }
        }
        finally
        {
            try
            {
                var updatedAuthPath = Path.Combine(scratch, "auth.json");
                if (File.Exists(updatedAuthPath))
                {
                    var updatedCredentials = File.ReadAllBytes(updatedAuthPath);
                    try { accounts.ProtectUpdated(profileId, updatedCredentials); }
                    finally { CryptographicOperations.ZeroMemory(updatedCredentials); }
                }
            }
            finally
            {
                if (!isActive)
                    SecureDeleteScratch(scratch);
            }
        }
    }

    private static void SecureDeleteScratch(string scratch)
    {
        var authPath = Path.Combine(scratch, "auth.json");
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
            AppLog.Warning("usage-temp-auth-wipe-failed", $"exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
        }
        try { Directory.Delete(scratch, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("usage-temp-cleanup-failed", $"exception={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
        }
    }

    private static async Task<JsonObject> ReadResponseAsync(StreamReader output, int id, CancellationToken token)
    {
        while (await output.ReadLineAsync(token) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var json = JsonNode.Parse(line)?.AsObject();
            if (json?["id"]?.GetValue<int>() != id) continue;
            if (json["error"] is not null) throw new InvalidOperationException();
            return json;
        }
        throw new InvalidOperationException();
    }

    private static UsageWindow? ParseWindow(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["usedPercent"] is null) return null;
        var used = Math.Clamp(obj["usedPercent"]!.GetValue<int>(), 0, 100);
        var mins = obj["windowDurationMins"]?.GetValue<int?>();
        var resetValue = obj["resetsAt"]?.GetValue<long?>();
        DateTimeOffset? reset = resetValue is null ? null : DateTimeOffset.FromUnixTimeSeconds(resetValue.Value).ToLocalTime();
        return new UsageWindow(used, mins, reset);
    }
}

internal static class CodexDesktopSwitcher
{
    // The packaged Windows desktop app uses the standard user Codex home.
    // CODEX_HOME is still set explicitly for temporary CLI profiles below.
    private static readonly string CodexHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    private const string DesktopAumid = "OpenAI.Codex_2p2nqsd0c76g0!App";

    public static string GetCodexHome() => CodexHome;
    public static string GetAuthPath() => Path.Combine(CodexHome, "auth.json");

    public static bool IsDesktopRunning()
    {
        var processes = GetDesktopProcesses(out var unverifiable);
        try { return processes.Count > 0 || unverifiable; }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public static bool Switch(AccountProfile? active, AccountProfile target, AccountStore accounts)
    {
        var targetSlot = accounts.Profiles.ToList().FindIndex(profile => profile.Id == target.Id) + 1;
        AppLog.Info("account-switch-requested", $"slot={targetSlot}");
        var authPath = GetAuthPath();
        var authDirectory = Path.GetDirectoryName(authPath)!;
        Directory.CreateDirectory(authDirectory);
        // Validate and decrypt the target before closing Codex, so a damaged
        // saved slot cannot unnecessarily take down the current session.
        byte[] targetBytes;
        try { targetBytes = accounts.Unprotect(target.Id); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
        {
            AppLog.Error("account-switch-target-sign-in-invalid", ex, $"slot={targetSlot}");
            throw new InvalidOperationException("The saved sign-in could not be verified. Codex was left open; sign in to this account again.", ex);
        }
        byte[]? oldBytes = null;
        var signInMayHaveChanged = false;
        try
        {
            CloseDesktop();
            AppLog.Info("account-switch-codex-closed", $"slot={targetSlot}");
            oldBytes = File.Exists(authPath) ? File.ReadAllBytes(authPath) : null;
            if (active is not null && oldBytes is not null)
                accounts.ProtectUpdated(active.Id, oldBytes);

            // Keep the previous sign-in in memory until Codex has started, so a
            // shell activation failure does not leave the user stranded.
            signInMayHaveChanged = true;
            ReplaceFile(authPath, targetBytes);
            AppLog.Info("account-switch-sign-in-staged", $"slot={targetSlot}");
            LaunchDesktop();

            if (!WaitForDesktopStarted(TimeSpan.FromSeconds(25)))
                throw new InvalidOperationException("Codex did not reopen with the selected account.");

            AppLog.Info("account-switch-succeeded", $"slot={targetSlot}");
            return true;
        }
        catch (Exception ex) when (signInMayHaveChanged)
        {
            AppLog.Error("account-switch-launch-failed", ex, $"slot={targetSlot}");
            try
            {
                RestoreFile(authPath, oldBytes);
                AppLog.Warning("account-switch-sign-in-restored", $"slot={targetSlot}");
                LaunchDesktop();
                if (!WaitForDesktopStarted(TimeSpan.FromSeconds(25)))
                    throw new InvalidOperationException("The previous sign-in was restored, but Codex did not reopen.");
            }
            catch (Exception recoveryException)
            {
                AppLog.Error("account-switch-recovery-failed", recoveryException, $"slot={targetSlot}");
                throw new InvalidOperationException(
                    "The account switch failed and automatic recovery could not finish. Open Codex and check its sign-in.",
                    recoveryException);
            }

            throw new InvalidOperationException(
                "Codex could not be reopened with the selected account. The previous sign-in was restored.", ex);
        }
        finally
        {
            if (oldBytes is not null) CryptographicOperations.ZeroMemory(oldBytes);
            CryptographicOperations.ZeroMemory(targetBytes);
        }
    }

    private static void CloseDesktop()
    {
        var processes = GetDesktopProcesses(out var unverifiableProcess);
        if (processes.Count == 0 && !unverifiableProcess) return;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                        process.CloseMainWindow();
                }
                catch (InvalidOperationException) { }
            }

            if (WaitUntilClosed(TimeSpan.FromSeconds(15))) return;
            AppLog.Warning("account-switch-graceful-close-timeout");

            // Closing the main window can leave the packaged app's renderer, GPU,
            // and app-server processes alive. Kill only processes whose executable
            // is inside the installed OpenAI.Codex package, including their trees.
            foreach (var process in GetDesktopProcesses(out _))
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }

            if (!WaitUntilClosed(TimeSpan.FromSeconds(12)))
                throw new InvalidOperationException("Codex could not be fully closed. No account was changed.");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static void ReplaceFile(string path, byte[] content)
    {
        var stagedPath = path + ".codex-usage-new";
        TryDelete(stagedPath);
        try
        {
            File.WriteAllBytes(stagedPath, content);
            File.Move(stagedPath, path, overwrite: true);
        }
        finally { TryDelete(stagedPath); }
    }

    private static void RestoreFile(string path, byte[]? previousContent)
    {
        if (previousContent is null)
        {
            TryDelete(path);
            return;
        }

        ReplaceFile(path, previousContent);
    }

    private static void LaunchDesktop()
    {
        using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{DesktopAumid}")
        {
            UseShellExecute = true,
            CreateNoWindow = true
        });
    }

    private static bool WaitForDesktopStarted(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var processes = GetDesktopProcesses(out _);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                            return true;
                    }
                    catch (InvalidOperationException)
                    {
                        // The process can exit while its main window is checked.
                    }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool WaitUntilClosed(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        DateTime? quietSince = null;
        while (DateTime.UtcNow < deadline)
        {
            var processes = GetDesktopProcesses(out var unverifiableProcess);
            var isRunning = processes.Count > 0 || unverifiableProcess;
            foreach (var process in processes) process.Dispose();
            if (!isRunning)
            {
                quietSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - quietSince >= TimeSpan.FromMilliseconds(750)) return true;
            }
            else
            {
                quietSince = null;
            }
            Thread.Sleep(200);
        }
        var remaining = GetDesktopProcesses(out var unverifiable);
        try { return remaining.Count == 0 && !unverifiable; }
        finally
        {
            foreach (var process in remaining) process.Dispose();
        }
    }

    private static List<Process> GetDesktopProcesses(out bool unverifiableProcess)
    {
        unverifiableProcess = false;
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                if (IsPackagedCodexProcess(process)) result.Add(process);
                else process.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                                         UnauthorizedAccessException or NotSupportedException)
            {
                process.Dispose();
                // Windows can deny MainModule access briefly while an Electron
                // child process is exiting. Keep waiting instead of treating that
                // transient race as a closed app or failing a otherwise sound switch.
                unverifiableProcess = true;
            }
        }
        return result;
    }

    private static bool IsPackagedCodexProcess(Process process)
    {
        var imagePath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        var fullPath = Path.GetFullPath(imagePath);
        return Path.GetFileName(fullPath).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(fullPath)?.EndsWith("\\app", StringComparison.OrdinalIgnoreCase) == true
            && fullPath.Contains("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
