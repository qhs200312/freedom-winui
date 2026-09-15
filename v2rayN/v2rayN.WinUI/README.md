# freedom WinUI 3

freedom is a Windows proxy client based on v2rayN. WinUI 3 is its only application front end. `ServiceLib` provides configuration, validation, core control, subscriptions, routing, testing, updates and backup behavior.

## Included functionality

- Dashboard, live traffic, core reload/stop, system proxy, TUN and routing controls
- All supported server protocols, clipboard/image/screen QR import and custom configuration import
- Profile filtering, subscription groups, multi-selection, copy/delete/deduplication, movement and column sorting
- TCP ping, real ping, UDP test, speed test, mixed test and fast delay test
- Sharing, QR codes, client config/share URL/Base64/internal URI export and policy group generation
- Subscription CRUD, sharing and direct/proxied updates
- Routing plans, routing rules, DNS, full config templates, application options and regional presets
- sing-box/mihomo proxy groups and active connections
- Runtime logs, component updates, local/WebDAV backup and restore
- Theme, accent, font size, language and global hotkeys
- Tray icon with proxy-state icons, tray commands, close-to-tray and single-instance activation

## Build

```powershell
dotnet build .\v2rayN.WinUI\v2rayN.WinUI.csproj -c Debug -p:Platform=x64
```

The project is unpackaged and self-contained with Windows App SDK, so no MSIX installation is required.
