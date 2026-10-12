# HY2 bandwidth editor correction

Verified on Windows x64, 2026-10-12.

The active package's HY2 node had no per-node bandwidth overrides and no custom
FinalMask bandwidth. Its global HY2 upload/download settings were both 100 Mbps.
The generator already used those global values; the node editor bound directly to
nullable `UpMbps` and `DownMbps`, so both fields appeared empty.
Only bandwidth values and presence flags were inspected, without printing
addresses, authentication or subscription credentials.

The normal HY2 node editor now uses numeric controls displaying each node's
override, or its global fallback when no override exists. Showing that fallback
does not write it into the node. Changing a value writes an override; clearing it
restores inheritance. Explicit zero remains zero. A tooltip identifies whether the
displayed value comes from the node or global settings. Global values are read
from configuration rather than hardcoded.

This correction is limited to the ordinary HY2 editor. Nodes using custom
FinalMask JSON retain their existing editor and authoritative advanced configuration;
the new global fallback is not applied to those fields.
Import, database schema and Core generation are unchanged.

Verification:

- 21 existing HY2 regression tests passed.
- The isolated native fixture links the production form and AddServerViewModel.
- The two controls rendered 100/100 while retaining null overrides.
- Saving unchanged, editing to 180/280, saving/reopening, share-link round trips,
  explicit zero, clearing, fractional input and form reloads passed.
- Different global defaults, 75/125, displayed correctly.
- Xray and sing-box JSON were generated for inherited, overridden and zero cases.
- All six JSON files passed the actual bundled Core validators:
  Xray `run -test` and sing-box `check`.

Native results, screenshot and synthetic client JSON:
`D:\v2rayN-master\build-check\hy2-bandwidth-ui-smoke\runtime`.
Unit test results:
`v2rayN/ServiceLib.Tests/TestResults/hy2-bandwidth-fields.trx`.

Published application:
`D:\v2rayN-master\build-check\hy2-bandwidth-fields-fixed\freedom.exe`.
Existing user nodes/configuration were not modified. The new package carries
runtime binaries and prerequisites, excluding generated caches, UDP runtime
configuration and logs; Core hashes match the preceding package.
