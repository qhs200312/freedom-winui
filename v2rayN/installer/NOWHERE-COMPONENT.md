# Nowhere Component

This freedom build includes the unmodified Nowhere 3.0.0 Windows
x64 executable from NodePassProject.

- Project and corresponding source: https://github.com/NodePassProject/Nowhere/tree/v3.0.0
- Release: https://github.com/NodePassProject/Nowhere/releases/tag/v3.0.0
- Archive: `nowhere-x86_64-pc-windows-msvc.zip`
- Archive SHA-256: `4c45907468d5d86ab880730402312f1906fbca1e6bb6e7b6f359ad33476768ab`
- Copyright: 2026 NodePassProject.
- License: GPL-3.0-only; a GPLv3 license text accompanies the executable.

freedom supervises a Vector process for each active Nowhere node, with a random
authenticated SOCKS5 listener on 127.0.0.1. Xray or sing-box uses that listener as
its outbound. No Nowhere wire protocol was added to either native core.

Server trust uses system CA verification or an explicit SHA-256 certificate pin.
Confirm self-signed certificate fingerprints through a trusted server-side channel.
The client does not auto-accept certificates or bypass certificate verification.

This component currently has no automatic updater. Background node speed tests
and complete configuration templates are not supported for Nowhere nodes.
