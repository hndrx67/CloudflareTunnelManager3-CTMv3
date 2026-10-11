# Cloudflare Tunnel Manager

A native Windows WPF application that downloads the official `cloudflared` connector and installs a remotely managed tunnel as a Windows service without requiring the user to open PowerShell or Command Prompt.

This repository targets .NET 8 for Windows.

## Open in Visual Studio

1. Open `CloudflareTunnelManager.sln` in Visual Studio.
2. Allow Visual Studio to restore the project.
3. Build and run (F5). Some operations require Administrator rights and will prompt UAC.

## Recent changes (uninstall, registry cleanup, elevated retry, nav highlighting)

This project recently received a set of improvements focused on reliable uninstall/reinstall of the bundled cloudflared connector and a small UI enhancement:

- Full uninstall flow
  - Stops and deletes the `cloudflared` Windows service.
  - Deletes the installed executable at `%PROGRAMDATA%\CloudflareTunnelManager\bin\cloudflared.exe` and removes the install directory when empty.
  - Removes leftover EventLog registry entry at `HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\Cloudflared`.
  - If normal registry deletion fails due to permissions, the app attempts an elevated PowerShell removal (UAC prompt). All steps are written to the in-app Activity logs.

- Improved reinstall reliability
  - After running `cloudflared service install`, the app polls `sc query cloudflared` for the service to appear (up to several retries) before starting it. This prevents transient "OpenService FAILED 1060" errors and surfaces clearer diagnostic output when install truly fails.

- UI: left-nav active state
  - Left navigation buttons now visually indicate the active page (marker + background/foreground). The app selects Dashboard on startup and updates highlighting on nav clicks.

## Files changed

- `Services/CloudflaredManager.cs`
  - Added `UninstallAsync()` with service stop/delete, executable deletion, EventLog registry cleanup, and elevated retry via PowerShell when necessary. Added polling after install before starting the service.

- `MainWindow.xaml`
  - Added an `Uninstall` button to the Dashboard's Windows service controls.

- `MainWindow.xaml.cs`
  - Added `UpdateNavSelection(...)` to highlight the active nav item and the `UninstallService_Click` handler.

## Behavior and permissions

- Administrative privileges are required for service manipulation and registry deletion. The app attempts a standard (non-elevated) registry delete first; if it fails the user will be prompted with a UAC dialog for an elevated PowerShell to complete the cleanup.

- The uninstall action does not modify resources in your Cloudflare account (DNS records, tunnel objects). You must remove those remotely via the Cloudflare dashboard/API if needed.

## How to use

1. Build and run the solution in Visual Studio.
2. Use the Setup page to install cloudflared with a valid tunnel token.
3. Use the Dashboard page to Start/Stop/Restart or Uninstall the local cloudflared service.
4. Check Activity logs for detailed install/uninstall output (the app redacts sensitive tokens automatically).

## Troubleshooting

- If Uninstall fails to remove the registry key and you declined the UAC prompt, re-run the app as Administrator and uninstall again.
- If reinstall still fails, copy the Activity logs and attach them to a bug report; logs include redacted installer output useful for diagnosing permission or installer failures.

## Next steps / suggestions

- Add a distinct danger style for the Uninstall button in the UI.
- Provide an option to request elevation upfront for install/uninstall flows to avoid mid-operation UAC prompts.
- Persist the last selected page across app restarts.

If you'd like a shorter developer changelog or additional documentation, tell me which format you prefer.
