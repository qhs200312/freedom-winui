# Clash Verge detector comparison and availability corrections

Investigation date: 2026-10-12. Target: the supplied Clash Verge v2.5.8 installer.

## Shipped artifact evidence

The installer SHA-256 was rechecked:
`78d8cb9fdfc1140c7846314848d09db7a39e46af68351876ea7357db922f39fe`.
The extracted `clash-verge.exe` hash still matches:
`e343e2c4b80f665f9616fd7e5ce25ba9b0a8c5d100ab1449440bfc79eb7faea0`.

The matching retained REA evidence is
`ev_8d84746d2261766e4b0dd8f2b472bf7bc4b2f0ef1241d3b1c9240e139ec41765`.
It covers 18 JavaScript modules parsed as inert text, without executing the bundle.
`useLockFn-0GjA3wou.js` retains hash
`39b5a21e42dcc672602a27dc829da19a3616fa6296d502d73469a4c940fb370c`.
Its exact command literals were rechecked at zero-based character offsets:

- 19804: `get_unlock_items`.
- 19854: `check_media_unlock`, passing `onComplete`.
- 19921: `check_media_unlock_item`, passing the service `name`.

These are Tauri IPC calls to the detector backend. Same-tag official source was
checked at commit `4337bd85758d54d7b9044b2da3c9ac1176eae1f3`.
This corroborates the source mapping, not native backend byte-for-byte equivalence.
No native backend was executed or newly decompiled. REA doctor reported missing
MCP registration/native engines; matching retained evidence and read-only source
inspection were sufficient for this scoped comparison. No tool configuration was
changed, and no installation was performed.

## Reference decision rules

| Service | What v2.5.8 source calls supported | Limitation |
| --- | --- | --- |
| ChatGPT | The compliance endpoint body does not contain `unsupported_country` | No account/model request; a readable generic body can pass |
| Claude | Trace `loc=` is outside a static ten-country blocklist | Only geography; no session/model access check |
| Gemini | Extracted three-letter page geography is outside a static blocklist | Page fragment matching; no model response |
| Netflix | Fast.com returns a target; otherwise title status/redirect heuristics | CDN can short-circuit; absent redirect region defaults to US |
| Prime Video | No `isServiceRestricted` substring, with extracted `currentTerritory` | Even a false restriction flag is treated as restricted |
| Spotify | Public country-selector returns an unblocked 2xx response | Selector choices are not visitor/account eligibility |
| TikTok | An unblocked 2xx trace/page response | Arbitrary successful body can pass; generic 403 is treated as No |
| Bahamut | Device/token response body contains `animeSn` | Substring presence; token failures can become false denials |

Primary source files:
[ChatGPT](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/chatgpt.rs),
[Claude](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/claude.rs),
[Gemini](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/gemini.rs),
[Netflix](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/netflix.rs),
[Prime Video](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/prime_video.rs),
[Spotify](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/spotify.rs),
[TikTok](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/tiktok.rs),
[Bahamut](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/bahamut.rs).

The [adapter](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/src-tauri/src/cmd/media_unlock_checker/mod.rs)
uses the local mixed-port HTTP proxy, HTTP/1 and cookies. Its certificate/hostname
exceptions were not copied; normal TLS verification and required proxy routing
remain enabled in freedom.

## Corrections and live evidence

Previously eight checks returned generic Unknown on the user's active route.
Read-only anonymous checks through its existing SOCKS listener reproduced them.
No login, account creation, model generation, video or audio playback was performed.
The corrected detector distinguishes bounded evidence and actual gates:

| Service | Observed result |
| --- | --- |
| ChatGPT, Claude, Bahamut | Human-verification pages, now labeled explicitly |
| Gemini | Identified application bootstrap, labeled webpage reachable |
| Netflix | Requested title metadata explicitly reports `isAvailable=true`, with observed request country |
| Prime Video | Non-member offer page/page data identified; a later run returned HTTP 503 |
| Spotify | Base64 Web Player config identified; its reported market is US |
| TikTok | Application/environment JSON identified; service region is US |

The page readers parse JSON without executing JavaScript. Netflix validates the
requested title ID and uses its page's request country, independently of CDN
geography. Spotify reads player configuration instead of public selector choices.
TikTok restriction messages are evaluated outside scripts, avoiding false denials
from translation catalogs. Prime Video HTML entities are decoded.

Cards now display webpage reachable, catalog available, sign-in required, human
verification, access denied, sample unavailable or service busy as applicable.
Tooltips describe exactly what was checked. Account/model/playback entitlement
remains unverified where the anonymous evidence cannot establish it.

Idempotent GET requests receive at most one bounded retry for 502/503/504 through
the same client/proxy, respecting long Retry-After values without an immediate
retry. POSTs are not replayed. Cancellation and the active check deadline apply.
The three genuine challenge responses were not converted into successful checks.

The proxy listener subsequently closed. It was not restarted or replaced.
Final native rendering uses the recorded real response results; it is not another
live connection test. Its historical HTTP 503 is displayed using the new busy
reason. Raw anonymous response captures remain local, outside the package.

## Verification and package

- Final suite: **519 passed, 0 failed, 5 platform/permission skips**.
- New fixtures cover bootstrap shape/encoding, requested title identity,
  geography provenance, translation false positives, login/challenge gates,
  explicit restrictions and bounded retries.
- Native cards rendered with the recorded live labels, light/dark modes,
  adaptive columns, refresh/cancellation and background chart checks.
- Self-contained WinUI x64 publish succeeded. Core hashes are unchanged.

TRX: `v2rayN/ServiceLib.Tests/TestResults/availability-evidence-final.trx`.
Live comparison: `D:\v2rayN-master\build-check\service-availability-live\bin\Release\net10.0\evidence`.
Native render: `D:\v2rayN-master\build-check\cards-traffic-ui-smoke\runtime\cards-live-results.png`.
Package: `D:\v2rayN-master\build-check\service-availability-evidence-fixed\freedom.exe`.
