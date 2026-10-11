# Cloudflare Tunnel Manager

A native Windows WPF application that downloads the official `cloudflared` connector and installs a remotely managed tunnel as a Windows service without requiring the user to open PowerShell or Command Prompt.

## Open in Visual Studio

1. Open `CloudflareTunnelManager.csproj` in Visual Studio.
2. Allow Visual Studio to restore the project.
3. Select `Debug` and `Any CPU`, then press **F5**.
4. Accept the Windows UAC prompt. Administrator access is required to manage the service.

The project targets **.NET 8 for Windows** and has no third-party NuGet dependencies.

## Current features

- Modern dark WPF dashboard
- Accepts a raw tunnel token or full Cloudflare install command
- Masks and redacts tunnel credentials
- Downloads the official AMD64 Windows connector
- Checks the executable's Cloudflare signing certificate
- Installs `cloudflared` as an automatic Windows service
- Start, stop, restart, status, startup-type, and version controls
- In-app troubleshooting log
- Tunnels page for listing multiple remotely managed tunnels
- Per-tunnel ingress route editor for different hostnames, web apps, and ports
- Independent in-app connector processes for multiple tunnels
- Cloudflare API credentials are retained only for the current app session
- Creates remotely managed tunnels directly from the Tunnels page
- Creates or updates proxied DNS CNAME records for selected published hostnames
- Explicit route type selector for HTTP, HTTPS, TCP, SSH, RDP, and SMB

## Multi-tunnel API access

The Tunnels page needs your Cloudflare **Account ID** and a scoped API token. Give it **Account / Cloudflare Tunnel / Edit**, **Zone / Zone / Read**, and **Zone / DNS / Edit** for the intended account and zones. Do not use a Global API Key.

The **Bind selected DNS** action saves the current ingress configuration, finds the most specific matching active zone for the hostname, and creates or updates a proxied CNAME pointing to `<TUNNEL-ID>.cfargotunnel.com`. It refuses to overwrite a conflicting non-CNAME record.

Connectors started from the Tunnels page run while the manager is open and stop when it closes. The original Setup page still installs Cloudflare's standard persistent Windows service for one primary tunnel.

## Important testing note

Use a test tunnel first. Removing the local Windows service does not automatically remove DNS routes or the tunnel stored in your Cloudflare account.
