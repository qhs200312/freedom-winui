# Experimental UDP Interception

Components are pinned to ProxiFyre 2.6.1, Windows Packet Filter 3.6.2.1 x64
and Microsoft Visual C++ runtime 14.44.35211.0. The packaging script verifies
their SHA-256 digests before inclusion.

ProxiFyre is a separate, unmodified process licensed under AGPL-3.0.
Corresponding source and license:
https://github.com/wiresock/proxifyre/tree/v2.6.1
https://github.com/wiresock/proxifyre/blob/v2.6.1/LICENSE

Windows Packet Filter is a separate shared NDIS driver:
https://github.com/wiresock/ndisapi/tree/v3.6.2
https://github.com/wiresock/ndisapi/blob/v3.6.2/LICENSE

Microsoft Visual C++ runtime retains Microsoft's distribution/license terms.
The setup wizard offers prerequisite installation; it can interrupt networking
and may require a reboot. Uninstalling freedom does not remove shared drivers
or runtimes. Remove these separately from Windows Installed Apps if desired.

After installation, leave TUN off and enable Settings > Parameters > Privacy >
experimental UDP interception. freedom requires administrator privileges on every
launch, independently of the interception toggle. Declining UAC prevents launch;
it does not change settings. Elevation does not install missing prerequisites or bypass UAC.
The engine runs only with the proxy core, not as an independently installed
ProxiFyre service. Do not run another ProxiFyre instance simultaneously.

Only listed process names are intercepted. Existing UDP sessions may require
restarting the browser/Discord. Local IPv4 networks bypass interception.
IPv6 fragmented datagrams may bypass ProxiFyre; this is not a complete
fail-closed leak prevention boundary. Engine failure or stopping freedom can
restore direct UDP. A running status is not proof of the external exit IP.
