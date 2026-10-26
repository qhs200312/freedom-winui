# Nowhere startup log investigation

Date: 2026-10-16. Follow-up experimental build: 7.30.2-nowhere-experimental.

## Findings

- The submitted log shows successful Xray startup and accepted proxy requests.
  Accepted requests alone do not prove end-to-end traffic success.
- The startup health check authenticated to Vector and then closed without a
  SOCKS command. Upstream v3.0.0 treats that incomplete exchange as an early EOF.
- `stun.services.mozilla.com` returned NXDOMAIN both locally and through the
  Google public DNS JSON resolver during the investigation.
- UDP interception's local SOCKS and external STUN checks had passed before the
  reported timeout. The timeout occurred while waiting for ProxiFyre's driver
  and application-association messages.
- Windows Packet Filter's NDISRD driver was installed and running. The packaged
  ProxiFyre executable matches an earlier working build (SHA-256
  `b317381d7f61af1c5a697c0de32c7743869843fbf52dd1805e9ebd1a5612741e`).
- No ProxiFyre startup output or component log was recorded for those attempts.
  This is insufficient to identify a specific native initialization failure.
- Windows socket errors 10053/10054 in individual flows do not by themselves
  establish that the entire Nowhere tunnel is down. They remain visible.

## Changes

- Complete an authenticated local UDP ASSOCIATE request before closing the
  readiness connection. This creates no remote traffic and avoids the startup
  early-EOF error.
- Remove the nonresolving Mozilla hostname from the built-in active STUN list.
  Standard STUN port routing remains enabled.
- Exclude `nowhere.exe` from application UDP interception to prevent accidental
  interception of the proxy transport itself.
- Show the original Nowhere node in core startup/status summaries rather than
  its temporary local SOCKS adapter.
- Accept fresh ProxiFyre readiness records from either its captured console or
  its component log. Existing log contents are skipped; partial UTF-8 records
  are buffered. Driver readiness and every configured association are still
  required.
- Add NLog internal diagnostics and describe missing driver/association evidence
  on timeout. Do not downgrade readiness requirements or automatically switch
  routing modes.

## Verification

- ServiceLib: **568 passed, 0 failed, 5 skipped**, 573 total.
  TRX: `v2rayN/ServiceLib.Tests/TestResults/nowhere-startup-fixed.trx`.
- Official Nowhere + generated Xray/sing-box runtime tests cover TLS, QUIC,
  Morph, TCP, UDP, DNS, certificate/key rejection and process cleanup. They now
  also check that the startup exchange does not emit the early-EOF warning.
- Added tests for rejecting recursive Nowhere interception and for reading new,
  partial, missing and truncated readiness logs without accepting prior runs.
- The original application exited before the initial live local-port checks;
  those connection-refused results were not interpreted as remote-node failure.
- A second probe opened the existing node database read-only and launched
  isolated Vector and Xray processes with production-generated configuration.
  No key or node details were printed. Results:
  - HTTPS `www.gstatic.com/generate_204`: **HTTP 204**.
  - STUN `stun.l.google.com:19302`: **198.1 ms**.
  - STUN `stun.cloudflare.com:3478`: **200.0 ms**.
  - Startup early-EOF log: **absent**.
  - All probe-owned processes stopped afterwards.
  Evidence: `build-check/nowhere-live-probe/bin/Release/net10.0/remote-results.json`.

The isolated remote test proves traffic through the node, not browser packet
capture. The agent process is not elevated and did not start a driver-level
interception session. ProxiFyre startup, browser interception and actual WebRTC
exit addresses still need a run of the updated application. The original
timeout's native root cause is not yet established; the console/file fallback
only fixes readiness when valid new messages actually exist.

The application was already stopped when the runtime update was applied to the
original experimental directory. Changed runtime files were backed up under
`D:\v2rayN-master\build-check\nowhere-runtime-backup-20261016-155039`.
The existing node database and application-settings file had identical SHA-256
hashes before and after the update. No production application was restarted.

## Upstream references

- https://github.com/NodePassProject/Nowhere/blob/v3.0.0/src/vector/socks/server.rs
- https://github.com/NodePassProject/Nowhere/blob/v3.0.0/src/vector/socks/udp.rs
- https://github.com/wiresock/proxifyre/blob/v2.6.1/ProxiFyre/Program.cs
- https://github.com/wiresock/proxifyre/blob/v2.6.1/ProxiFyre/NLog.config
