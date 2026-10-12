# Updates and network diagnostics

Implementation and verification date: 2026-10-12.

## User-visible changes

| Request | Implemented behavior |
| --- | --- |
| Remove the bottom-left status block | The sidebar footer, separator, upload/download speeds and memory figures are removed. Only the homepage traffic graph and its dashboard statistics remain. |
| GitHub release notes | The application update dialog displays the `body` from the matching release in `qhs200312/freedom-winui`, with a bounded scroll area and selectable text. An absent body is reported explicitly. |
| Quiet update checks | Checking does not create an update notification or progress animation. Available application updates open a dialog when the main window can show it. Check failures create a notification. A manual check can also report that the application is current. |
| Ten-second notifications | Normal messages and update notifications dismiss after ten seconds. Download progress does not continually restart that timer or reopen an already dismissed notification in the same stage. A new stage can notify again. Dismissing a notification does not cancel the download. |
| Automatic core updates | A persisted, initially disabled `AutoUpdateCores` switch appears in Settings under updates/data sources. Startup and daily checks honor the selected components. Enabling the switch triggers a check on the next scheduler tick. Available selected cores use the existing download/core lifecycle with serialized manual/automatic operations. |
| Application in Component Updates | A dedicated freedom row shows current version and update status, with a check/view/install action. Core download/install status remains visible here after notifications disappear. The row uses the verified application update coordinator; it does not invoke the old immediate-install batch path. |
| Service availability and regions | The unlock-test page displays twelve compact cards, with per-service refresh, Test All and cancellation. Three columns adapt to two or one when space is limited. Each card displays typed status, observed region flags/codes and time; full evidence and limitations remain available in tooltips. Changing the node, route or core invalidates previous results. |
| Detailed, visible IP | The dashboard displays the unmasked IP and location/flag. ASN/provider and timezone appear only in the details dialog, following the latest dashboard cleanup request. Its details action opens country, city/region, provider, organization, ASN, timezone, coordinates, network path and timestamp, with copy/select support. IPv6 wraps without hiding the address. |
| Clipboard subscriptions in management | The cached subscription management view refreshes when opened and when clipboard import publishes a refresh event. Each row displays its remark and URL; manual refresh and double-tap editing are available. Existing subscription IDs, node associations and disabled/local entries remain intact. |

The software dialog retains in-application downloading, silent/background downloading,
SHA-256 verification, staged packages and install-on-normal-exit behavior from the
preceding implementation. Closing a notification or minimizing to the tray is not
an installation trigger.

## Update correctness

Application metadata is fetched from the branded repository's release API. Version,
asset and notes are selected from the same response. Wrong repository/version/architecture
assets, drafts, malformed metadata and unsupported version formats fail rather than
reporting an application as current. Existing pending-update manifests without notes
remain readable; changing only notes does not force another package download.

A read-only request to the actual latest-release API during this audit returned:

- Tag: `v7.23.8`.
- Published: `2026-09-15T07:41:02Z`.
- Release body: 170 characters.
- x64 zip/setup assets and both SHA-256 sidecars.

No new release was published and no installed application was replaced.

Core updates prepare and validate zip/gz/tar.gz packages before stopping the core.
Expected executable names and host binary signatures must match; geo data is retained.
Unsafe paths, links, duplicate flattened files, incomplete executables and oversized
packages are rejected. Replacement uses backups and rollback. Failed rollback retains
recovery files with an explicit location. Installation failures do not produce an
`Installed` result or delete the downloaded recovery package.

The running state is evaluated immediately before installation. An automatic update
does not start a core that the user stopped during the download. A running core is
reloaded after successful replacement or an installation failure, unless shutdown is
in progress. If rollback itself fails, the core remains stopped and the recovery
location is reported. The WinUI caller awaits its reload callback. Shutdown cancels and drains
both manual and automatic core operations; ordinary update faults do not prevent exit.
The older scheduled release checker yields ownership to the WinUI coordinator, avoiding
duplicate background checks.

## Routing and interpretation

IP providers share one supplied HTTP client. When a core is running that client uses
the local SOCKS entry; failed proxy queries never switch to a direct client. With the
core stopped, an explicitly labeled direct IP can be displayed. Configured routing
rules still apply inside the core, so the label is “current proxy route,” rather than
an unconditional assertion about one node.

Service checks require a running core, enforce its SOCKS endpoint, retain TLS
verification, bound response sizes/concurrency/timeouts and propagate cancellation.
They do not log in or play subscription content.

“Supported” requires a service response or offer that supports that limited
interpretation. Explicit restrictions, transport errors and insufficient evidence
are different results. Trace geography, public country selectors, generic 403 responses,
anti-bot challenges and CDN locations do not establish account/model/playback access.
These produce “cannot confirm” with an explanation instead of a false positive.
Regions remain unknown when the response provides no defensible region.

See [service-availability-reverse.md](service-availability-reverse.md) for the shipped
Clash Verge evidence, same-tag official source mapping and per-service limitations.
The native integration is an independent C#/WinUI implementation.

Country assets come from MIT-licensed `flag-icons` 7.5.0. They are bundled locally;
the UI does not contact a flag service. The upstream license is included in
`v2rayN/v2rayN.WinUI/Assets/Flags/LICENSE.txt`.

## Verification

The focused update/IP run passed 69 tests. The full suite also exercises existing
HY2 generation, routing and actual loopback Xray/sing-box TCP/UDP paths.

The earlier update/diagnostics full-suite result was **491 passed, 0 failed, 5 skipped**, out of 496 cases.
The skipped cases require Unix execute-bit checks or the ability to create native
filesystem symbolic links, which this Windows test environment could not provide.
The final TRX is
`v2rayN/ServiceLib.Tests/TestResults/diagnostics-updates-final.trx`.

New fixtures cover release metadata/body pairing, migration and package reuse;
automatic-update persistence; archive validation, replacement and rollback;
all twelve availability checkers; redirects; response limits; proxy enforcement;
timeouts/concurrency/cancellation; provider schemas and IPv4/IPv6.

The native smoke application links the production controls and update coordinator.
Its fixture responses contain documentation-only IPs and synthetic versions:

- `Checking`: zero notifications.
- Download: one notification.
- After ten seconds, including intervening progress: zero notifications.
- Further progress in that stage: still zero notifications.
- A new stage: a notification appears.
- Automatic application check failure: a visible error notification.
- Wide/compact unlock views, long IPv6 details, national flags, component updates
  and a scrollable release-notes dialog rendered successfully.

Native XAML render artifacts and timing results are under
`D:\v2rayN-master\build-check\diagnostics-ui-smoke`.
The final `production-runtime` run uses the published application's actual
`ReactiveUI.WinUI` assembly and the same `WithWinUI()` startup initialization.
All timing and render assertions completed without unhandled/render exceptions.
RenderTargetBitmap output does not include every native popup/composition surface;
the accessibility tree additionally confirmed the component toolbar controls.

The desktop-control helper could read the test window's accessibility tree but could
not bring it above the Codex window. Two click attempts were rejected as non-target
inputs. No desktop click-path success is claimed. Rendering/timer verification was
completed inside the isolated test program without touching system proxy settings.

Remaining live boundaries: service eligibility on the user's actual node, authenticated
model/playback access, and installation/relaunch of a newer remote application release
were not exercised. The current remote release equals the application's version.
Tests use synthetic service responses and isolated archive targets, not the user's
installed kernels.

### Subscription management follow-up

The latest full-suite run passed **494 tests, with 0 failures and 5 skips**, out of
499 cases. Results are in
`v2rayN/ServiceLib.Tests/TestResults/subscription-management-final.trx`.
The ten focused clipboard/subscription tests also passed.

The isolated native WinUI fixture links the production collection view. It verifies
clipboard URL import, database persistence, refresh events dispatched from a worker,
visible URL bindings, local HTTP subscription download, node subscription ownership,
duplicate URL handling, selection preservation and refresh after cached-view navigation.
Both wide and compact renders succeeded. Synthetic URLs and documentation-only nodes
are used; no user subscription credentials or application data are read.
Evidence is under `D:\v2rayN-master\build-check\subscription-ui-smoke\runtime`.

The first broad run encountered two unrelated sing-box runtime test failures because
earlier TUN fixtures had cached a controller port occupied by a running core. The two
runtime fixtures now reset the in-process port cache before configuration generation,
allowing normal free-port selection. The running core was not stopped or modified.
The subsequent full run passed, including actual Xray and sing-box TCP/UDP tests.

## Published local build

`D:\v2rayN-master\build-check\service-availability-evidence-fixed\freedom.exe`

Self-contained Windows x64 publish succeeded. Thirteen required application,
helper, runtime, XBF, asset and core files were checked. All 271 flag assets and
their license are present. Bundled Xray, sing-box and mihomo binaries have the same
hashes as the preceding package. Only its `bin` and `prerequisites` payloads were
carried forward; no GUI configuration, database or logs were copied.

The subsequent card layout and background animation verification is documented in
[cards-and-background-traffic.md](cards-and-background-traffic.md). That change
passed 117 focused tests and native layout, interaction and compositor checks.

The latest HY2 bandwidth editor follow-up is documented in
[hy2-bandwidth-editor.md](hy2-bandwidth-editor.md). Global fallback values now
appear in the normal node editor without becoming saved overrides. It passed
21 HY2 regression tests, native editor/persistence checks and six actual Core
configuration validations.

The availability follow-up is in
[clash-unlock-checks-and-fixes.md](clash-unlock-checks-and-fixes.md). It rechecks
the supplied Clash Verge artifact/source mapping, adds actual application/catalog
evidence and specific gate/error labels, and records anonymous live tests through
the user's existing proxy. The final suite passed 519 tests with five environment
skips, and the native card renders include those recorded live results.
