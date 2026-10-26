# Nowhere experimental integration

Date: 2026-10-16. Windows x64.

## Upstream evidence

The upstream repository is https://github.com/NodePassProject/Nowhere.
The main branch inspected during discovery was
`a15bbb3230f39a562f8bae456364fb59b05bf832`.
Implementation and runtime checks target the published **v3.0.0** configuration,
Vector client and official Windows x64 executable. The release archive's GitHub
SHA-256 was verified:

`4c45907468d5d86ab880730402312f1906fbca1e6bb6e7b6f359ad33476768ab`.

Relevant source and documentation: `docs/configuration.md`, `docs/integrations.md`,
`docs/platforms.md`, `src/main.rs`, `src/vector/config.rs` and the SOCKS implementation,
all under the upstream v3.0.0 tag.

## Implemented behavior

- Import `nowhere://` shares and Vector role URLs; Portal and Egress role URLs are rejected.
- Preserve the shared key, endpoint, TLS/QUIC carrier, Morph, certificate pin,
  SNI and upload/download limits in the existing profile/ProtoExtra storage.
- Add a Nowhere node editor, transport choices, Morph switch, bandwidth fields
  and certificate pin. No database schema or existing enum values were changed.
- Translate each referenced Nowhere node into an authenticated loopback SOCKS
  outbound at context-building time. Saved profiles remain unchanged.
- Reuse the same Vector for repeated references within a context and allocate
  distinct ports/credentials for distinct nodes.
- Supervise the Vector processes with core startup/stop; verify their local
  SOCKS identity before starting the native core. Stop partial startup on failure.
- Register the Nowhere executable with the existing TUN process bypass rules.
- Reuse native Xray/sing-box DNS, routing and traffic accounting.
- Always replace imported `in=` with an owned 127.0.0.1 listener; imported unknown
  query options are not passed through as process arguments.

The wire protocol is implemented by Nowhere itself, not reimplemented in freedom.
The standard compact share format uses `net=tcp|udp`. Family-specific Vector
URLs retain `tcp4/tcp6/udp4/udp6`; role URLs cannot include a display-name fragment.
This client also retains Vector trust/rate options when exporting compact links;
other clients may ignore those options on a share URL.

## Trust

No pin means system CA and hostname verification. Self-signed Portal certificates
need a trusted 64-character lowercase hexadecimal certificate fingerprint.
No automatic fingerprint adoption or `allowInsecure` mode was added.
Wrong-key and wrong-pin tests verify that target connections are not established.

## Verification

The tests use isolated processes and loopback echo/DNS endpoints:

- Link parsing, duplicate/default rules, malformed role/client input and JSON round trips.
- SQLite storage and native client JSON generation for Xray and sing-box.
- TLS and QUIC carriers; Morph enabled and disabled; 100/100 Mbps limit parameters.
- Actual 4096-byte TCP echo, 48/4000-byte UDP echo and a DNS wire query/response.
- Random authenticated local ingress, stable repeated references and process cleanup.
- Separate authenticated listeners when two runtime contexts refer to the same node.
- Wrong certificate/key failure and missing executable cleanup.

The latest TRX report is
`v2rayN/ServiceLib.Tests/TestResults/nowhere-startup-fixed.trx`.
The complete ServiceLib run finished with **568 passed, 0 failed, 5 skipped**
(573 total). The skipped cases are pre-existing archive/symbolic-link/Unix
permission tests; no Nowhere test was skipped.
The editor fixture under `build-check/nowhere-ui-smoke` verifies batch import,
edit/save/reopen and sharing against production view/view-model code.
All editor checks passed, including transport selection, numeric rate fields,
certificate pin/SNI retention and exported-link round trips. The fixture edits
native controls programmatically; it does not simulate physical mouse input.

The isolated WinUI build is at
`D:\v2rayN-master\build-check\nowhere-experimental\freedom.exe`, with product
version `7.30.2-nowhere-experimental`. It includes the verified official Nowhere
3.0.0 executable, WinUI resources, Xray, sing-box and their required static files.
The normal main window was not launched during tests to avoid changing system
proxy settings. No user settings, databases or logs were copied into the build.
The packaged Xray and sing-box executable hashes match those used in the runtime
tests. Required application resources, updater executable, cores, Nowhere license
and static prerequisite files were checked after publishing.

The 100 Mbps settings were retained and accepted; throughput at that rate was not
measured. A follow-up isolated check using the existing saved remote node passed
an HTTPS request (HTTP 204) and Google/Cloudflare STUN UDP requests (approximately
198/200 ms). The saved database was opened read-only; no system proxy or driver
configuration was changed by this check. See `nowhere-startup-investigation.md`.
Remote throughput and broader NAT behavior remain untested.

## Current limits

- No background/batch real-ping or speed-test lifecycle for Nowhere nodes yet.
  Generation is rejected explicitly rather than starting an unowned sidecar.
- Complete custom configuration templates are rejected for Nowhere nodes.
- Nowhere is pinned and bundled in the local experimental build; it is not yet
  included in the component auto-update workflow.
- Address-family URL parsing is covered; native IPv6 traffic and a real TUN
  adapter are not yet exercised by this integration's runtime tests.
- Exported native core JSON needs its corresponding running Vector processes;
  it is not a standalone Nowhere configuration.

No GitHub release or existing installation was replaced for this experiment.
