# WebRTC UDP Interception Audit

Audit date: 2026-10-09 (Asia/Shanghai).

## Finding

Enabling process-based UDP interception did not guarantee a proxy exit.
ProxiFyre sent intercepted datagrams into the ordinary mixed/SOCKS inbound.
The Core then applied ordinary routing rules, which could select `direct`
for STUN endpoints or media destinations outside the limited STUN rules.

This is a confirmed configuration-design defect, not a confirmed reproduction
of the user's Cloudflare test. No running production proxy, real node credentials,
or Cloudflare browser capture was available on the audit machine.

## Changes

- Added a dedicated loopback SOCKS5 inbound, `udp-interception`, on the local
  mixed port plus 7. It supports UDP independently of mixed-inbound settings
  and does not inherit LAN exposure or authentication.
- Xray and sing-box route that inbound to the selected proxy before STUN,
  DNS, user direct rules, or sing-box direct mode. The rule is independent
  of destination ports, server lists, sniffing, and destination address family.
- Existing Xray policy groups continue to use their selected balancer.
- ProxiFyre configuration, readiness checks, and startup probes use the
  dedicated port. Ordinary mixed-port traffic retains its existing routing.
- Reserved the dedicated TCP and UDP port before Core startup, including
  when UDP on the mixed inbound is disabled. Probe the actual loopback bind
  address, because Windows wildcard UDP binding can miss a loopback conflict.
- Do not create the dedicated inbound for disabled interception, TUN,
  non-Windows, custom nodes, full templates, or unsupported Cores.
- Strengthened STUN startup-response validation: require a binding-success
  response, matching random transaction ID, magic cookie, complete attribute
  lengths, and an IPv4 or IPv6 mapped-address attribute. Error responses,
  unrelated datagrams, and stale transactions no longer report success.

Node records and the deployed application's configuration were not modified.
The preceding HY2 changes remain intact.

## Verification

The complete ServiceLib suite passed: **175 passed, 0 failed, 0 skipped**.
There are 30 additional cases: 15 routing/port-selection cases, 13 STUN
response cases, and two actual-Core runtime cases.

Generated configurations were checked with:

- Source-built Xray 26.9.30: `run -test -c`.
- sing-box 1.14.1: `check -c`.

The actual-Core runtime tests configured an ordinary catch-all UDP `direct`
rule and disabled STUN special routing. A local SOCKS5 upstream recorded and
echoed traffic addressed to `1.1.1.1:3478`, `198.51.100.17:54321`, and
`[2001:db8::17]:55000`. These are test destinations, not an actual Cloudflare
STUN service test; the mock upstream never forwards traffic to them.
All three destinations were observed through the proxy for both Cores.

After the mock upstream stopped, a local UDP witness received no datagram
addressed to it through the dedicated inbound. This verifies that the Core
does not fall back to a direct route; it does not prove that the interception
driver blocks browser traffic when the driver or client stops.

Release service-library build passed with zero warnings and zero errors.
Evidence is under `D:\v2rayN-master\build-check\webrtc-audit`, including
the regression TRX, generated Core JSON, runtime logs, and proxy observations.

## Test Package

`D:\v2rayN-master\build-check\webrtc-udp-fixed\freedom.exe`

The test package uses the existing 7.23.8 WinUI shell/resources, newly compiled
Release ServiceLib and UDP-test libraries, Xray 26.9.30, and sing-box 1.14.1.
The self-contained runtime, WinUI resource, Core, and interception-prerequisite
package checks passed.

A clean WinUI publish could not be completed because the local NuGet cache
lacks `Microsoft.WindowsAppSDK.AI` 1.8.76. The reused shell was not rebuilt or
interactively launched during this audit. Do not describe this package as a
fresh, fully tested WinUI publish.

## Remaining Limits

- ProxiFyre remains an experimental third-party NDIS interceptor, not a
  fail-closed privacy boundary. Its exit, Core exit, or startup failure can
  restore direct browser UDP.
- The pinned component documents possible IPv6-fragment bypass.
- Only the configured process names are intercepted. Existing browser UDP
  sessions may require completely restarting the browser.
- Local IPv4 traffic still bypasses interception by design.
- TCP-based TURN and browser IP-candidate exposure are not covered by this
  UDP-only interception change.
- A proxy server's UDP exit can differ from its TCP exit or still be domestic.
  Confirm the actual mapped public IP, not only a country label.
- An actual Cloudflare browser test, physical IPv4/IPv6 interception, driver
  crash/startup behavior, and packet capture remain unverified.

For stronger network-level coverage, use properly configured IPv4/IPv6 TUN
and proxy routing. Protection during failure additionally requires a tested
fail-closed mechanism; this patch does not claim to implement one.
