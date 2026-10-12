# Service availability and detailed IP evidence

Scope: static investigation and implementation on 2026-10-12. Owned changes are
`v2rayN/ServiceLib/Services/Diagnostics/*`, new
`v2rayN/ServiceLib.Tests/Services/Diagnostics/*`, and this report.
No WinUI, ConnectionHandler or existing IP DTO edits were made by this work.
IP fetch/fallback and UI integration belong to main.

## Evidence identity

The prior investigation recorded these artifacts; this pass reuses their identity,
without re-extracting or analyzing the whole executable/bundle:

- Installer `Clash.Verge_2.5.8_x64_fixed_webview2-setup.exe` SHA-256:
  `78d8cb9fdfc1140c7846314848d09db7a39e46af68351876ea7357db922f39fe`.
- Extracted/installed `clash-verge.exe` SHA-256:
  `e343e2c4b80f665f9616fd7e5ce25ba9b0a8c5d100ab1449440bfc79eb7faea0`.
- Existing scoped IP-card REA evidence, supplied by main:
  `ev_8d84746d2261766e4b0dd8f2b472bf7bc4b2f0ef1241d3b1c9240e139ec41765`.
  This is a retained static evidence reference, not a new runtime observation.

Focused files read from
`D:\v2rayN-master\build-check\clash-traffic-audit\frontend`:

| File | SHA-256 recomputed in this pass | Observation |
| --- | --- | --- |
| `useLockFn-0GjA3wou.js` | `39b5a21e42dcc672602a27dc829da19a3616fa6296d502d73469a4c940fb370c` | `qt`, `Jt`, `Yt` invoke `get_unlock_items`, `check_media_unlock` with `onComplete`, and `check_media_unlock_item` with `name`. |
| `tests-bA6Qw90D.js` | `445915b174b2c1e8c0fea41717c6b35947023eb928f63327c5af071c94f60bd1` | Display/localization statuses; contains no detector logic. Other `tests*.js` are language variants. |
| `ip-info-card-CN1I_Z07.js` | `b16c88f0deda81c1a23a945e383e3556443a69c39476eb20dbaa6510bbac1b4d` | `_e` contains provider mappings; `ve` performs bounded attempts through Tauri HTTP; UI renders ASN/location/timezone. |

The JS IPC-name occurrences are at zero-based string character offsets 19804,
19854 and 19921 respectively. They are not native addresses or UTF-8 byte offsets.
`test-card-Bywc2cn2.js` is the separate customizable latency card, not the media
availability backend.

Official upstream was fetched read-only. GitHub's `commits/v2.5.8` API resolved
the tag to `4337bd85758d54d7b9044b2da3c9ac1176eae1f3` on this pass:
<https://api.github.com/repos/clash-verge-rev/clash-verge-rev/commits/v2.5.8>.
This identifies the requested tag, not the latest overall Clash Verge release.

## Source trace and finding ledger

All rows are static observations of the cited source plus implementation choices.
The JS IPC names corroborate the same-tag source entrypoints, but do not prove
native executable/source byte-for-byte equivalence or successful endpoint execution.
Each source link is fixed to the verified commit.

| ID | Official source | Reference observation | Implemented interpretation and limitation |
| --- | --- | --- | --- |
| S01 | [command adapter](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/src-tauri/src/cmd/media_unlock_checker/mod.rs) | The three commands match the shipped IPC literals. The adapter creates a client using the effective local mixed-port HTTP proxy and cookies. | Injected clients must enforce main's selected proxy. No direct-route retry. Normal TLS verification remains enabled, unlike reference certificate/hostname exceptions. |
| S02 | [catalog/scheduler](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/lib.rs) | Twelve checks; four concurrent checks; each has an outer 15-second timeout. | Same catalog/order/defaults, with a service-instance semaphore covering individual and batch calls. Caller cancellation throws; timeout becomes a typed failure. |
| S03 | [result types](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/types.rs), [helpers](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/utils.rs) | Reference results carry name/status/region/check time; trace geography uses `loc=`; common helper treats 403 and 451 as restricted. | Typed state/reason, ISO2 region plus evidence URL, UTC timestamp and elapsed time. Generic 403 is ambiguous `Unknown`; 451 is explicit `Unavailable`. |
| S04 | [Bilibili](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/bilibili.rs) | CN episode `307247`, HK/MO/TW episode `183799`; response code zero is supported, `-10403` is restricted. No region field. | Exact episode requests and explicit codes. No inferred CN/HK region. Other codes/schema errors are `Failed`. Acceptance proves only the reference episode response, not all playback. |
| S05 | [Bahamut](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/bahamut.rs) | Fetch device ID, token for `adID=89422&sn=37783`; substring `animeSn` means supported. Region uses main-page `data-geo`. | URL-escape the device ID; require an actual populated `animeSn` JSON field. Missing token evidence is `Unknown`, not a geographic denial. Region is independently observed. No playback is performed. |
| S06 | [ChatGPT](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/chatgpt.rs) | Compliance cookie endpoint detects `unsupported_country`; geography comes from `chat.openai.com/cdn-cgi/trace`. Reference otherwise assumes support. | Explicit restriction is `Unavailable`; valid generic compliance JSON is `Unknown` with account/model-access limitation. Malformed expected JSON is `Failed`. |
| S07 | [Claude](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/claude.rs) | Only trace geography and a static AF/BY/CN/CU/HK/IR/KP/MO/RU/SY blocklist. | Both blocklist branches are inferential `Unknown`; detail records whether the observed location appears in the reference list. No claim of Claude access. |
| S08 | [Gemini](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/gemini.rs) | Geography follows the literal `,2,1,200,"`; alpha-3 blocklist drives the reference status. | Use marker length rather than the earlier erroneous offset; normalize alpha-3/aliases to ISO2. Geography/blocklist alone is `Unknown`; no model-access request. |
| S09 | [Netflix](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/netflix.rs) | Fast.com target location short-circuits success; fallback titles `81280792`/`70143836`; two 404s imply Originals Only. Region-title `80018499` defaults absent region to US. | CDN evidence yields `Unknown` with an explicitly labeled edge region. Title/404 heuristics remain `Unknown`, not playback entitlement. Region uses only an observed HTTPS Netflix redirect; never defaults to US. |
| S10 | [Disney+](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/disney_plus.rs) | Anonymous device assertion, token exchange and GraphQL session geography/support; JP shortcut; false support means Soon; main-page region fallback implies success. | Retain the anonymous exchange using the public reference browser key, without an account credential. Support requires the explicit supported-location flag. False means unsupported, with no launch prediction. Main-page geography alone is `Unknown`. Generic 403 is not an IP ban. |
| S11 | [Prime Video](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/prime_video.rs) | Mere presence of `isServiceRestricted` means restricted; otherwise `currentTerritory` means supported. | Inspect the boolean value: true is `Unavailable`; false plus observed territory is a page-level availability signal. Territory alone is `Unknown`. Detail excludes account/playback claims. |
| S12 | [Spotify](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/spotify.rs) | Public country-selector response/redirect supplies a region; generic success implies support. | Selector choices and redirect locale cannot establish visitor/account eligibility. Successful selector responses yield `Unknown` and no region, even with `countryCode` entries. Explicit country restriction remains `Unavailable`. |
| S13 | [TikTok](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/tiktok.rs) | Trace then main-page fallback; any unblocked successful body means supported; quoted region supplies geography. | Arbitrary trace/page success is `Unknown`, even when geography exists. Explicit product-region denial is `Unavailable`. Generic access denial is ambiguous. All attempts use the same client. |
| S14 | [YouTube Premium](https://github.com/clash-verge-rev/clash-verge-rev/blob/4337bd85758d54d7b9044b2da3c9ac1176eae1f3/crates/clash-verge-media-unlock/src/youtube.rs) | Offer-page text/browse ID versus explicit unavailable-country/region messages. Region has several page fields. | Require the Premium browse ID or Premium text together with ad-free offer text. Generic branded page is `Unknown`. Accepted offer evidence does not prove subscription/playback. |
| S15 | shipped `ip-info-card-CN1I_Z07.js`, retained REA IP evidence above | ip.sb, ipapi.co, ipapi.is, ipwho.is, SKK and GeoJS have different nested ASN/org/location/timezone fields. Numeric fallback zero conflates absence with a real coordinate. | Standalone structured parser supports these mappings and ipinfo `org` ASN prefix/`loc`, including newer nested geography aliases. Missing coordinates stay nullable; no network calls in parser. |

Detector endpoint URLs are literal constants in `ServiceAvailabilityService.cs`
and the cited Rust files. Optional requests/fallbacks are geographic/evidence
checks using the same supplied transport. The implementation does not retry
through another proxy, direct networking, a user account or a bypass mechanism.
Endpoint longevity and current response schemas remain unverified.

## Stable integration contract

Namespace: `ServiceLib.Services.Diagnostics`.

```csharp
new ServiceAvailabilityService(HttpClient client, ServiceAvailabilityOptions? options = null);
static IReadOnlyList<ServiceAvailabilityDefinition> ServiceDefinitions; // Id, Name
static IReadOnlyList<AvailabilityService> Services;
static string GetName(AvailabilityService service);
Task<ServiceAvailabilityResult> CheckAsync(AvailabilityService service, CancellationToken cancellationToken = default);
Task<IReadOnlyList<ServiceAvailabilityResult>> CheckAllAsync(
    IProgress<ServiceAvailabilityResult>? progress = null, CancellationToken cancellationToken = default);
static DetailedIpInfoResult? DetailedIpInfoParser.Parse(string json);
```

`AvailabilityService` is stable in this order: `BilibiliMainland`,
`BilibiliHongKongTaiwan`, `ChatGPT`, `Claude`, `Gemini`, `YouTubePremium`,
`Bahamut`, `Netflix`, `DisneyPlus`, `PrimeVideo`, `Spotify`, `TikTok`.

`ServiceAvailabilityResult` carries `Service`, `Name`, `State`, `Reason`,
`RegionCode`, `EvidenceUrl`, `RegionEvidenceUrl`, `CheckedAt`, `Elapsed`, `Detail`
and computed `Status`. States are `Available`, `Unavailable`, `Limited`,
`Failed`, `Unknown`. The reason enum retains reference display categories
`IpBanned`, `OriginalsOnly`, `ComingSoon` for API compatibility; the conservative
checks do not manufacture those claims from generic 403/404/geography.

- `Available`: an explicit service response or offer supports the limited
  page/episode/session interpretation described in `Detail`.
- `Unavailable`: explicit service restriction evidence or HTTP 451.
- `Unknown`: insufficient evidence, generic 403 or an anti-bot challenge.
- `Failed`: transport/timeout/read/schema error, oversized response, or other
  unsuccessful HTTP status without an explicit service restriction.
- Caller cancellation is propagated as `OperationCanceledException`, including
  checks waiting for the concurrency slot. It does not become a result.

`RegionCode` is nullable ISO alpha-2, not an emoji, locale or label. Alpha-3
codes normalize using .NET `RegionInfo` mappings; aliases include UK -> GB,
EL -> GR and XK/XKX -> XK. Unknown codes remain null.
Spotify selector options are deliberately not region evidence. Netflix
`RegionCode` from Fast.com is an edge region, labeled in `Detail`, not an
assertion about the subscriber catalog.

`DetailedIpInfoResult` properties: `Ip`, `CountryCode`, `CountryName`, `City`,
`Region`, `Asn` (`long?`), `Provider`, `Organization`, `Timezone`, `Latitude`
(`double?`), `Longitude` (`double?`). IP is validated/normalized for IPv4/IPv6.
ASN range is 1 through 4294967295. Coordinates must be finite and in range;
zero is valid. Missing/invalid IP, malformed JSON, non-object root and provider
failure payloads return null. Malformed optional fields remain null.
Country names supplied by the response take precedence; otherwise .NET
`RegionInfo.EnglishName` may fill an observed country code.

The injected client remains caller-owned. Main's client factory must enforce
the supplied proxy, disable automatic redirects, and retain certificate
verification. Prefer an infinite client timeout; the service supplies its
own per-check cancellation budget. Client and handler injection are trust
boundaries: their underlying routing cannot be inspected by this service.
The `IWebProxy` constructor enforces non-bypass routing itself; a null/self
proxy endpoint fails rather than selecting a direct route.

Defaults: 15-second active-check budget (queue time excluded), four concurrent
service checks per instance, 4 MiB per decoded response, five redirects.
Netflix title fallback may issue two requests inside one active service check;
this is a service-check concurrency limit, not a global HTTP request limit.
Main should reuse the instance if it wants one shared concurrency budget.
HTTP bodies are streamed with the same cancellation token, capped before
parsing, and disposed. Redirects require HTTPS; cross-origin redirects drop
authorization, and authenticated POST bodies are never replayed to another
origin. Relative redirects and standard POST-to-GET redirect rules are handled.

## Verification and remaining boundaries

New fixtures cover all twelve services; generic 403, HTTP 451 and challenge
classification; reference service signals; malformed object/scalar/array JSON;
Gemini offset and ISO aliases; Netflix observed redirect versus fabricated US;
Spotify public selector and arbitrary TikTok pages; Disney support/region
fallback; header/method/body behavior on redirects; declared/streamed response
limits; timeout versus active/queued caller cancellation; shared concurrency;
batch order/progress; client ownership; IPv4/IPv6 and nested IP schemas.
A loopback-only CONNECT fixture is written to prove that supplied proxy bypass
advice cannot produce a direct route; it returns a synthetic 502 before TLS
and never contacts a service.

The backend workstream did not run builds independently because main controlled
the shared build outputs. The integrated final run on 2026-10-12 passed 491 tests
with zero failures; five platform/permission-dependent archive tests were skipped.
The service fixtures were included in that run. See
[updates-and-diagnostics.md](updates-and-diagnostics.md) for native rendering,
package validation and the final TRX. A focused rerun is available with:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter FullyQualifiedName~ServiceLib.Tests.Services.Diagnostics
```

There is no live availability evidence in this report. Source retrieval is not
service testing. The retained REA record and focused JS reads are static, not
runtime observations. No account login, subscription, video playback, AI model
request or provider bypass was performed. Compilation and fixture validation are
complete; the live-service boundaries above remain unverified.
