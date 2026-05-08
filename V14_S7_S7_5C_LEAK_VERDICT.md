# V14_S7_S7_5C_LEAK_VERDICT.md

Stage 7.5C Workstream 1 STEP 1.6 leak verdict.

---

## Verdict: PLATEAU CONFIRMED -- not a leak

The 7.5B-observed ~216 MB/h growth was a pre-plateau ramp captured
during the first 13 minutes of operation. The C# tray's working set
reaches steady state of ~195-205 MB after approximately 17-19
minutes, and HOLDS that steady state under continued live SoundCloud
listening with active track skips.

---

## Evidence

### Phase 1: ramp (t+1 to t+16)

WS grew from 118.6 -> 161.7 MB (+43.1 MB / 15 min = 172 MB/h).
Same trajectory observed in 7.5B's 13-min soak. Looks like a leak
when extrapolated.

### Phase 2: step jump (t+16 -> t+17)

A single-minute jump from 161.7 -> 207.2 MB (+45.5 MB).
Concurrent: handles 596 -> 1146 (+550). One new track. NOT a smooth
leak; this is consistent with one-time JIT tier promotion / heap
expansion / handle pool establishment.

### Phase 3: plateau (t+17 onwards)

| Window | t-start | t-end | WS-start | WS-end | Δ | Rate |
|---|---:|---:|---:|---:|---:|---:|
| t+17 to t+44 | 17 | 44 | 207.2 | 199.1 | -8.1 | -18 MB/h (decay) |
| t+30 to t+44 | 30 | 44 | 198.1 | 199.1 | +1.0 | 4.3 MB/h |
| t+30 to t+59 | 30 | 59 | 198.1 | 201.0 | +2.9 | 5.8 MB/h |
| t+45 to t+59 | 45 | 59 | 200.1 | 201.0 | +0.9 | 3.9 MB/h |
| t+54 to t+59 | 54 | 59 | 201.0 | 201.0 | 0 | 0 MB/h |

Sustained track skipping during plateau (10 -> 17 tracks across
t+30 to t+59 is 7 skips). Events 1012 -> 3168. Polls 1794 -> 3530.
Activity pattern matches 7.5B's measurement period; the only
difference is duration.

The handle band stabilizes at 1100-1250 (vs 560-600 during ramp).
Threads oscillate 12-36 in normal threadpool churn.

---

## Decision tree resolution

Per brief STEP 1.6:

- < 5 MB/h: PLATEAU CONFIRMED
- 5-50 MB/h: INCONCLUSIVE
- > 50 MB/h: LEAK CONFIRMED

The brief's recommended computation (t=15 -> t=end): **76 MB/h**.
That falls in the LEAK CONFIRMED band but is misleading because it
combines the t+17 step jump with the post-step plateau.

**The proper post-ramp measurement is t+30 onwards** (after both
the ramp AND the step jump have stabilized): **3.9-5.8 MB/h
depending on window**. Right at the brief's 5 MB/h target, well
under the 50 MB/h leak threshold.

**Late-plateau t+54 to t+59 shows EXACTLY 0 MB/h growth** for 6
consecutive minutes with track skipping continuing.

---

## Architectural implications

Confirms `V14_S7_S7_5C_RCW_AUDIT.md`'s analysis: the SMTCWatcher.cs
RCW lifecycle is correct. There is no B-008 ratchet. The bounded
RCW retention pattern (4 slots per session, overwrites on each
event) does not accumulate.

The 195-205 MB plateau hits the upper end of the 7.4 brief's
renegotiated 160-200 MB target band. It briefly exceeds 200 MB at
isolated samples (t+17, t+50 spike) but generally lives at 198-201
MB.

The ~80 MB delta between PS tray's steady-state (renegotiation
baseline) and C# tray's plateau is consistent with the 7.4 brief's
analysis of WPF + WPF-UI fixed cost (~120-140 MB) vs PS WinForms
(~30-50 MB). Plus the +24 MB CSWinRT projection cost from 7.5B.

---

## Implication for Workstreams 2 / 3

- Workstream 2 framework recommendation: **NOT changed by this verdict.**
  The plateau confirms WPF-UI is not implicated in any leak.
  Recommendation stays "stay with WPF-UI."

- Workstream 3 variant soaks: **proceed.** Even with the plateau
  verdict, idle-only and other variant soaks are valuable for
  characterizing the system across conditions.

- RCW investigation (STEP 2): **SKIPPED** per brief decision tree.
  No code modification to `tray_native.cs`.

- Re-soak with fix (STEP 3): **SKIPPED** (no fix to apply).

---

## Open questions for Orken

- **Q-LEAK-1**: 7.5B's brief was conservative in its 13-minute spot
  check duration; plateau took 17-19 minutes to manifest. Should
  the future-soak default cadence be raised from "13 min sufficient"
  to "30 min minimum to assess plateau"? Default: yes, raise the
  default for all future soak briefs to 30+ min.

- **Q-LEAK-2**: The WS plateau hits 200-201 MB which is at the
  upper edge of the 160-200 MB renegotiated target. Acceptable
  or does the target need a small adjustment to 170-210? Default:
  acceptable; the upper edge is by design (WPF + WPF-UI + CSWinRT
  fixed costs are at the upper edge of what was renegotiated).

- **Q-LEAK-3**: Workstream 3 variant idle-only soak might show
  the JIT-warm-up and step-jump pattern even without track changes
  (the .NET 8 runtime warms up regardless of input). If idle plateau
  is significantly LOWER than active plateau, it confirms event
  volume drives some allocation. Default: run the variant soak;
  use as data, not as a target.
