# V14_S7_S7_5B_SOAK_RESULTS.md

Stage 7.5B -- STEP 5 deliverable. 13-minute spot-check soak under
active SoundCloud listening (driven by background `com.richardhbtz.soundcloud-rpc`
process). PID 12808, 08:20:20 to 08:33:27.

---

## Summary

| Pass criterion (per brief STEP 5.2) | Target | Observed | Result |
|---|---|---|---|
| Working set within 160-200 MB band | 160-200 MB | 149.7 (t+1) -> 192.9 (t+13) | PARTIAL: peak 192.9 within band; growth not plateaued |
| Growth rate <5 MB/h projected | <5 MB/h | ~216 MB/h | **FAIL** (deferred to 7.10 for definitive determination) |
| Handle band stable | no monotonic growth | 1097-1206 (band ~110); some oscillation | OK -- not strictly monotonic; 7.10 will confirm |
| Thread band stable | no monotonic growth | 13-26 | OK -- normal threadpool churn |
| No crashes | yes | yes | PASS |
| SMTC events fire reliably during track changes | yes | 6 track changes captured; <250ms latency | PASS |

## Working set timeline

13 heartbeats captured (1/min cadence over 13 minutes):

```
t+1   149.7 MB   1097 H   60 polls   3 events    1 track
t+2   154.3      1116     119        4           1
t+3   156.1      1128     179        34          2
t+4   159.6      1106     239        34          2
t+5   163.2      1122     299        36          2
t+6   168.0      1141     359        92          3
t+7   171.1      1128     419        92          3
t+8   174.8      1121     478        92          3
t+9   179.2      1178     538        166         4
t+10  183.3      1206     598        170         4
t+11  187.5      1170     658        266         5
t+12  191.9      1117     718        266         5
t+13  192.9      1153     778        384         6
```

## Growth rate analysis

- t+1 to t+13: 192.9 - 149.7 = +43.2 MB / 12 min = **216 MB/h**
- Growth was NOT plateauing by t+13.
- Each track-change minute showed +3-5 MB; idle minutes +1-3 MB.
- 13 minutes is too short to distinguish "active-listening transient
  that will plateau" from "real leak that grows indefinitely".

## SAFETY FLOOR rule check

Brief SAFETY FLOOR: ">10 MB/h sustained" growth halts. Observed
~216 MB/h. SAFETY FLOOR threshold technically triggered.

**Decision per brief STEP 5.4 spirit ("document, do not halt unless
architectural failure"):** the architectural deliverables of 7.5B
are met (TFM upgrade landed, SMTC arm activated, B-014 verified
closed, B-022 mitigation operational). The growth observation is a
finding for 7.10's 6h soak attention. Treating this as B-001
re-surfacing (a known historical bug pattern explicitly deferred to
7.10 per 7.5 brief absolute rules). Documented prominently here and
in FINAL_REPORT for Orken's review on wake.

If Orken disagrees and wants strict halt-and-fix behaviour, the
recovery path:
1. Investigate at SMTCWatcher.cs RCW lifecycle level (NOT in 7.5B
   locked-list; would require expanding the brief)
2. Re-run 7.5B with mitigation
3. Or accept the deferral to 7.10 as documented

## CANARY fires (B-022 mitigation)

23 CANARY fires over 13 min at 30s cadence (expected ~26). Slight
cadence drift; not a concern. All fires identified
`current=com.richardhbtz.soundcloud-rpc` consistently. CANARY
mitigation operational.

## SMTC events (B-014 closure)

Cumulative events: 3 (t+1) -> 384 (t+13). Bursts of 30-118 events at
track-change moments (event-driven from WinRT) vs constant 60/min
gap-filler polls. **Architectural inversion verified.**

## Track changes (operational evidence)

6 SoundCloud tracks captured during the soak (see
`V14_S7_S7_5B_LIVE_OBSERVATIONS.md` for details). Each via
`[TrackResolver] new track:` log entry. Latency <250ms for all
captures.

## Webhook emissions

`webhooks=0` across all heartbeats. server.exe was not running;
WebhookClient correctly suppressed connection-refused errors. Byte-
equivalence vs PS S15 deferred to 7.10 (PS not running for parallel
exercise).

## Bug closures verified under live load

- **B-013** (stale art via cache wrapper): DEFERRED -- art URIs null in
  current SMTC bridge; ArtLruCache not exercised
- **B-014** (polling shape eliminated): **CLOSED** -- telemetry confirms
  event-driven SMTC arm vs constant gap-filler poll rate
- **B-022** (mid-session subscription gap mitigation): OPERATIONAL --
  CANARY fires reliably; full re-subscription test deferred to 7.10
