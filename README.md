# Codex Usage Tray

A Windows tray utility with a readable floating badge and usage monitoring for any number of Codex accounts.

The badge shows **short-window usage consumed • weekly usage consumed** for the selected
account. The right-click menu lists all saved accounts with remaining percentages and reset
times. Each row has a check mark for the account active in Codex. Click an account to
switch to it; if it is already the active account but Codex is closed, the app launches it.
The badge follows the account after the switch succeeds.

The badge sits flush against the screen's bottom edge, initially left of the system tray.
Drag horizontally to move it; its position is saved under `%LOCALAPPDATA%\CodexUsageTray`.
Usage refreshes every minute. A missing sign-in or unavailable quota is shown beside that
account.

Run `publish-current\CodexUsageTray.exe` to start the installed app.

## MCP server

The tray executable includes an MCP server over standard input/output. It exposes
`get_usage` for all saved accounts and `switch_account` for an agent-requested switch.
The tray enforces a configurable five-hour threshold on the active account (80% by
default; change it from the tray menu's **Agent switch threshold** item) and requires the
target account to be below the configured threshold. For a manual switch, the calling agent
is told to preserve the remaining usage: checkpoint minimally, hand off the next step, and
yield immediately. Switching closes and restarts Codex Desktop. The server starts the tray
app when a tool call needs it.
When the tray starts, it adds global turn-tracking hooks to
`%USERPROFILE%\.codex\hooks.json`, preserving other hook entries and creating a timestamped
backup before edits. Codex may require you to review and trust the new hooks before they run.

The MCP command is `publish-current\CodexUsageTray.exe --mcp`. It can be registered in a
MCP client when you want to enable it there. To use chat subscriptions, that client must
load the updated tray MCP server that includes `subscribe_chat` and `unsubscribe_chat`.

### Automatic switching and subscribed chats

Use MCP `subscribe_chat` in each chat that should continue after an automatic switch. The
tray tracks turn starts and ends across chats. When five-hour usage reaches the configured
threshold, it waits until all tracked turns have stopped, chooses a configured account below
the threshold, and writes one-time resume markers for subscribed chats only. On startup or
resume, a subscribed chat must verify the active account with `get_usage` before continuing.
Use `unsubscribe_chat` to remove a chat. Session IDs and working folders are stored locally;
credentials and transcript contents are not stored by these hooks.

Codex must restore a conversation for its `SessionStart` hook to add continuation context;
the tray cannot force the Desktop app to reopen a particular conversation. The tray first
asks the running app to close normally and waits longer before it considers force-closing it.

`hook-setup\codex-session-start.hooks.json.example` shows the global hooks installed by
the tray. Replace its example executable path with the local `publish-current` path before
installing the hooks manually.

Run the safe policy test (it does not close Codex or change accounts):

```powershell
.\tests\Test-AutoSwitchPolicy.ps1
```

## Set up accounts

1. Right-click the tray icon or badge and choose **Manage accounts**.
2. For the account currently open in Codex, choose **Save current sign-in** and give it a
   private label.
3. Choose **Add account** for each additional account, then choose **Sign in…** and complete
   the Codex login in the window that opens.
4. Click an account row in the tray menu to switch Codex Desktop to it. Save your work
   first; Codex closes and reopens as part of the switch.

Saved sign-ins are protected with Windows DPAPI for the current Windows user. The app
does not show or log credential contents. Usage checks use Codex's installed app-server;
inactive accounts are opened in short-lived isolated profile folders, and updated sign-in
data is protected again before those folders are removed.

Codex Desktop does not currently provide a built-in account switcher. This utility switches
the local sign-in used by Desktop, asks the app to close, then force-closes its packaged
process tree if it has not exited after a short wait. It verifies the app is closed before
changing sign-ins and then reopens Desktop. Save your work before clicking an account.
Local Codex history remains shared between accounts. Switching requires a readable
`auth.json` in the Codex home directory; a keyring-only sign-in cannot be saved or switched
by this version. Account changes made outside this utility should be saved again with
**Save current sign-in** before switching.

Windows notification icons use small square image slots, so the percentage badge is a
separate, borderless window. It does not appear as a taskbar button or take keyboard focus
when shown. Its default size is 208 × 46 logical pixels with 24-pixel text, scaled for the
display's DPI. It hides during full-screen applications and while Start or Search is open,
and restores its position above the taskbar afterward. Right-click either the badge or tray
icon for account switching, management, refresh, details, and exit.

To build, use .NET 10 on Windows:

```powershell
dotnet publish -c Release -o publish-current
```

Exit the running utility before replacing the executable in `publish-current`.

## Verification and limits

- The Windows Release build succeeds with .NET 10.
- The safe auto-switch policy self-test checks seven decisions without closing Codex or changing accounts.
- The interactive account-switch flow has not been smoke-tested as part of this portfolio build because it closes Codex Desktop and replaces the active sign-in file. Review the confirmation prompts and keep a separate sign-in method available before using it.
- This project depends on the installed Codex Desktop package layout and its local `auth.json` format. A keyring-only sign-in cannot be saved or switched by this version.

References:
- [OpenAI account switching support](https://help.openai.com/en/articles/20001068-use-multiple-accounts-with-account-switching)
- [Microsoft notification area documentation](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)
- [Non-activating Windows Forms](https://learn.microsoft.com/dotnet/api/system.windows.forms.form.showwithoutactivation)
