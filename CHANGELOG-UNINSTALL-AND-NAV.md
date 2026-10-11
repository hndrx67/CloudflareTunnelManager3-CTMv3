Changelog — Uninstall improvements, registry cleanup, elevated retry, nav highlighting

Summary

This change set implements a full uninstall flow for the bundled cloudflared connector, improves the reliability of re-installation, and adds left-nav selection highlighting in the UI. The build completed successfully after the changes.

Files changed / new

- Services/CloudflaredManager.cs
  - Added: public async Task UninstallAsync()
	- Stops and deletes the cloudflared Windows service (sc stop/delete).
	- Deletes the installed executable (bin\cloudflared.exe) and removes the install directory when empty.
	- Removes the leftover EventLog registry key at:
	  HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\Cloudflared
	- If normal registry deletion fails (permission / UAC), spawns an elevated PowerShell process (Verb=runas) to run Remove-Item -Recurse -Force against the EventLog key and waits up to 30s for completion.
  - Improved install flow:
	- After running cloudflared service install, the code now polls sc query cloudflared (10 attempts with 500ms delay) so the service has time to appear before calling sc start.
	- If the service never appears, the code attempts one start with throwOnFailure=false and surfaces the captured output with a clearer exception message.
  - Emits progress and diagnostic information through existing MessageLogged event so app UI logs show installer and uninstall activity.

- MainWindow.xaml
  - Added an "Uninstall" button to the Dashboard's Windows service controls near Start/Stop/Restart.

- MainWindow.xaml.cs
  - Added UpdateNavSelection(Button active) which updates left-nav look (background/foreground and a "●" marker) to indicate the active page.
  - Calls UpdateNavSelection on startup and in each nav click handler so the left navbar highlights the active page.
  - Added UninstallService_Click handler that prompts the user for confirmation, calls UninstallAsync, logs results, and refreshes the displayed service state.

Behavioral notes and rationale

- Problem addressed: Uninstall previously removed the executable and service but left a leftover event-log registry key. When reinstalling, sc or the installer could fail because the registry entry conflicted or the service had not yet fully registered, leading to errors such as OpenService FAILED 1060.

- Registry cleanup: Deleting the EventLog key helps ensure a clean re-install. Deletion requires administrative privileges; the app attempts a normal delete first and, if that fails, attempts an elevated registry removal via PowerShell (UAC will prompt the user). All results are logged to the in-app log.

- Service start race: The cloudflared installer may return before Windows registers the new service. Polling for the service to appear before attempting to start it avoids transient 1060 errors and gives clearer errors when the service truly fails to install.

Permissions and UAC

- Registry deletion and service removal require administrative rights. If the app is launched without administrator privileges, the standard deletion may fail; the code attempts an elevated removal which triggers a UAC prompt. If the user declines elevation the registry entry may remain and re-install attempts might still fail.

Testing and verification

1. Build and run the app from Visual Studio (Ensure you run with normal privileges first; UAC prompts will appear if the elevated retry is used).
2. On the Dashboard page, click Uninstall and confirm. Confirm the following outcomes in the Activity logs:
   - Messages indicating stopping/deleting the service.
   - Messages indicating deletion of the cloudflared executable (if present).
   - Messages indicating removal of the EventLog registry key or logs showing elevated removal results.
3. Install cloudflared again with a valid tunnel token through the Setup page. The installer should no longer fail with "OpenService FAILED 1060". The UI should show the service status, startup type, and connector version.
4. Click through the left navigation (Dashboard, Tunnels, Setup tunnel, Activity logs) — the active nav item should be visually highlighted.

Troubleshooting

- If Uninstall fails to remove the registry key and you declined the UAC prompt, re-run the app as Administrator and uninstall again.
- If re-install still fails, collect the Activity logs from the app (Copy from Activity logs page) and attach them; they include redacted installer output.

Revert

- To revert any changes, use your source control to discard/rollback the commits that modify the listed files.

Enhancements you may want next

- Add a distinct "danger" style for the Uninstall button to make the action more visible.
- Persist last selected page across restarts.
- Add an option to request the app be run as admin up-front for uninstall/install operations to avoid UAC during an operation.

If you want, I can produce a short README.md in the repository root with the same contents.*/