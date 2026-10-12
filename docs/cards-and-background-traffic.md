# Service cards and background traffic animation

Verified on Windows x64, 2026-10-12.

## Changes

The unlock test page follows the supplied reference's twelve-card arrangement.
Cards display the service name, circular refresh button, status badge, available
region flag/code and check time. Full evidence, restrictions and elapsed time
remain in tooltips. The grid uses three columns at 720 effective pixels or wider,
two at 470 or wider, and one below that. Cards retain a minimum height of 112
effective pixels and support light/dark themes.

Service probes and result interpretation are unchanged. Unknown results still say
"cannot confirm"; publicly observed geography does not imply account access.
Existing single-service checks, Test All, cancellation and route invalidation remain.

The previous main window activation handler disabled chart animation on
`WindowActivationState.Deactivated`, even when the window remained visible.
The chart therefore rendered its next complete position only at each sample.
`TrafficChartWindowController` now checks page visibility, window visibility and
minimized state. Loss of focus keeps compositor animation enabled. Hidden,
minimized and other-page states suspend animation; restoring visibility resumes it.
The existing traffic sampling timer and chart geometry are retained.

## Verification

- WinUI Release self-contained x64 publish succeeded.
- 117 focused availability, subscription and clipboard tests passed, with no skips.
- Native fixtures link the production view, chart and window controller.
- All twelve cards rendered in three, two and one columns, and in dark mode.
- Layout checks confirmed card bounds and stable heights.
- Single-service refresh, Test All, cancellation and stale result invalidation passed.
- Country assets and result detail tooltips were checked.
- A second test window deactivated the chart window. Animation remained enabled.
- With no intervening traffic sample, two chart renders approximately 300 ms apart
  differed by 71,785 pixel-channel bytes, confirming movement between samples.
- Minimize, restore, hide, show and page changes passed their animation checks.

The native `AnimationController.Progress` getter returned zero in both captures;
pixel differences, rather than that getter, provide the movement evidence.
The fixtures use synthetic responses and do not establish live service eligibility
on the user's proxy. No production main window was launched, and no system proxy
or user subscription database was modified.

Results and screenshots:
`D:\v2rayN-master\build-check\cards-traffic-ui-smoke\runtime`.
Focused test results:
`v2rayN/ServiceLib.Tests/TestResults/cards-background-focused.trx`.

## Package

`D:\v2rayN-master\build-check\unlock-cards-background-smooth\freedom.exe`

Thirteen required application/resource/helper/core files were checked. Core binary
hashes match the previous subscription-fix package. Only its `bin` and
`prerequisites` directories were carried forward; GUI settings, subscriptions and
logs were not copied into the new package.
