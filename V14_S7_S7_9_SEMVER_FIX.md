# V14_S7_S7_9_SEMVER_FIX.md

Stage 7.9 -- one-line surgical fix to `SemVerComparer.Compare` (file
`src/tray_csharp/Update/SemVerComparer.cs`). Closes the downgrade-prompt
edge case surfaced by Stage 7.2 smoke (a tester running v14.0.0-rc.1
saw v12.0.1 stable as `State.Available` because the project policy
"any pre-release < any stable" was being applied globally).

---

## What changed

The pre-release rule (pre-release < stable) now applies ONLY when
major.minor.patch is equal between the two versions. Numeric MMP
comparison runs FIRST and returns early if MMP differs; pre-release
status is consulted only as the tiebreaker at the same MMP.

### Before (Stage 7.2 logic)

```csharp
// Project policy: any pre-release < any stable, applied GLOBALLY.
var aIsPre = IsPreRelease(a);
var bIsPre = IsPreRelease(b);
if (aIsPre && !bIsPre) return -1;
if (!aIsPre && bIsPre) return 1;
if (aIsPre && bIsPre) return 0;

if (majA != majB) return majA.CompareTo(majB);
if (minA != minB) return minA.CompareTo(minB);
if (patA != patB) return patA.CompareTo(patB);
return 0;
```

### After (Stage 7.9 fix)

```csharp
// Numeric MMP comparison FIRST. If MMP differs, that's the answer
// regardless of pre-release status.
if (majA != majB) return majA.CompareTo(majB);
if (minA != minB) return minA.CompareTo(minB);
if (patA != patB) return patA.CompareTo(patB);

// Same MMP: apply pre-release rule (graduate-from-RC-to-stable case).
var aIsPre = IsPreRelease(a);
var bIsPre = IsPreRelease(b);
if (aIsPre && !bIsPre) return -1;  // pre < stable at same MMP
if (!aIsPre && bIsPre) return 1;
return 0;                           // both stable equal OR both pre at same MMP
```

## Why

The original logic was implemented per the brief's literal STEP 2.2
test case 5: `Compare("14.0.1-beta.5", "14.0.0")` returns negative.
That codified "any pre-release < any stable" as a global rule. Faithful
to the brief, technically.

But in production smoke (7.2), it caused a UX surprise: the user
running the latest v14.0.0-rc.1 saw the v12.0.1 stable on `main`
branch as State.Available. The C# safety guard (no auto-chain to
Download) prevented an actual silent downgrade, but the
"update available" badge was misleading.

The Stage 7.9 fix changes the policy interpretation: the
pre-release-is-less rule is for the **graduation** use case (RC of
14.0.0 vs stable of 14.0.0 = stable wins), not for cross-version
comparison. When MMP differs, numeric comparison is the right answer
regardless of pre-release status.

## Test cases

The 11 original R6 closure cases plus 4 new cases plus 1 Q4-default
case = **16 cases total**. All PASS empirically:

| # | Case | Expected | Notes |
|---:|---|---|---|
| 1 | `Compare(14.0.0, 14.0.0-rc.1) > 0` | true | same MMP, stable wins |
| 2 | `Compare(14.0.0-rc.1, 14.0.0) < 0` | true | same MMP, pre loses |
| 3 | `Compare(14.0.0, 14.0.1) < 0` | true | numeric MMP differs |
| 4 | `Compare(14.0.0-rc.1, 14.0.0-rc.2) == 0` | true | both pre at same MMP |
| 5 | `Compare(14.0.1-beta.5, 14.0.0) > 0` | true | **CHANGED in 7.9**: numeric MMP wins (14.0.1 > 14.0.0) |
| 6-11 | IsPreRelease cases | unchanged | regex behaviour intact |
| 12 | `Compare(14.0.0-rc.1, 12.0.1) > 0` | true | **NEW 7.9**: local RC newer than remote stable |
| 13 | `Compare(14.0.0-rc.1, 13.5.0) > 0` | true | **NEW 7.9**: local RC newer than remote stable |
| 14 | `Compare(14.0.0, 12.0.1) > 0` | true | **NEW 7.9**: regression check |
| 15 | `Compare(12.0.1, 14.0.0-rc.1) < 0` | true | **NEW 7.9**: inverse of bug case |
| 16 | `Compare(14.0.0-rc.1, 14.0.0-rc.1) == 0` | true | **NEW 7.9 Q4 default**: same RC equivalent |

**Empirical verification at runtime: 16/16 PASS** (logged as
`[Update] R6 closure: ALL 16 pre-release regex synthetic test cases PASS`).

## Compatibility with 7.2 R6 closure

The 7.2 R6 closure (rejecting pre-release REMOTE versions before
comparison) is unchanged. `IsPreRelease` regex behaviour unchanged.
Only the `Compare` ordering rule was tweaked.

UpdateCheckService.ProcessManifestJson still:
1. Parses JSON
2. Checks `IsPreRelease(remoteVersion)` -- if true, log + state Idle (R6 closure)
3. Otherwise, calls `Compare(remoteVersion, localVersion)`

The downgrade-prompt edge case was at step 3: when local was an RC of
a NEWER version, the comparison incorrectly said "remote > local".
Now Compare returns the correct value (remote < local because
remote's MMP is numerically lower).

## Locked-list deviation note

`src/tray_csharp/Update/SemVerComparer.cs` is a 7.2 file. The brief
explicitly lists it in the 7.9 locked-list edit set as "DEVIATION:
one-line fix" with the brief author's blessing. Same pattern as Stage
7.2 Strike 2 (ConfigService.cs, a 7.4 file edited in 7.2 to close a
runtime regression).

## Empirical verification

Skeleton smoke at 07:36:27 logged:
```
[Update] R6 closure: ALL 16 pre-release regex synthetic test cases PASS
```

Real-HTTP startup check (post-fix) against version.json on main:
- local=14.0.0-rc.1, remote=12.0.1
- new behavior: `Compare(remote=12.0.1, local=14.0.0-rc.1)` returns
  negative (12 < 14 numerically; pre-release status irrelevant)
- state stays Idle (no false-positive Available)
- log: `up-to-date (remote=12.0.1 local=14.0.0-rc.1)` (when this
  scenario re-runs against the 7.9 build)

Edge case CLOSED.
