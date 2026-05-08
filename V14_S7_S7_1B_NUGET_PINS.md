# V14_S7_S7_1B_NUGET_PINS.md

Stage 7.1B -- STEP 1 deliverable. Locked NuGet stack, pinned to
exact versions current at brief execution start (2026-05-08).
All four packages MIT-licensed, all four within the 12-month
active-maintenance gate.

---

## Pinned versions

| PackageReference Include | Version | License | Published | Age | Verdict |
|---|---|---|---|---:|:---:|
| `H.NotifyIcon.Wpf` | `2.3.2` | MIT | 2025-10-23 | ~6.5 mo | PASS |
| `WPF-UI` | `4.3.0` | MIT | 2026-05-04 | 4 days | PASS |
| `CommunityToolkit.Mvvm` | `8.4.2` | MIT | 2026-03-25 | ~6 wk | PASS |
| `Microsoft.Extensions.DependencyInjection` | `9.0.15` | MIT | 2026-04-14 | ~3.5 wk | PASS |

All four published-dates verified via NuGet v3 catalog API. All
licenses verified via NuGet v3 flatcontainer .nuspec. All four
package ids verified canonical (NOT typo-squatted forks).

---

## Per-package notes

### `H.NotifyIcon.Wpf` 2.3.2

- **Project**: https://github.com/HavenDV/H.NotifyIcon
- **License**: MIT.
- **Published**: 2025-10-23.
- **Maintainer**: HavenDV / Konstantin Semenenko + community.
- **Target frameworks at 2.3.2**: net4.6.2, net8.0-windows7.0, net9.0-windows7.0.
- **Reason chosen**: active fork of the original Hardcodet.NotifyIcon.Wpf (which has slowed maintenance per re-plan WPF_LOCK section 2.1). API parity with Hardcodet means the migration cost from any prior code-snippet reference is near-zero.
- **Why NOT the latest stable 2.4.1**: 2.4.1 (published 2025-12-01) DROPPED net8.0-windows7.0 target -- it ships only net4.6.2 and net10.0-windows7.0. Stage 7.1B targets net8.0-windows; consuming 2.4.1 would force NuGet to fall back to net4.6.2 which uses .NET Framework's WPF assemblies (PresentationCore/PresentationFramework), incompatible with .NET 8 WPF runtime. 2.3.2 is the latest stable with explicit net8.0-windows7.0 target. Pre-build compatibility check caught this before the build attempt; no strike consumed.
- **Known breaking advisory for next major (3.x line)**: none surfaced in NuGet metadata at pin time. Future Stage 7.x brief authors should re-evaluate this pin if the project re-adds net8.0 support (perhaps in a 2.4.x patch) or if Stage 7's runtime moves to net9.0+.

### `WPF-UI` 4.3.0

- **Project**: https://github.com/lepoco/wpfui
- **License**: MIT.
- **Published**: 2026-05-04.
- **Maintainer**: Leszek "lepoco" Pomianowski + community.
- **Multi-target**: net10.0-windows, net9.0-windows, net8.0-windows, net481, net472, net462.
- **Canonical NuGet id**: `WPF-UI` (hyphenated). NOT `Wpf.Ui` (dotted) -- which resolves to a different / older package on NuGet, the source of an early STEP 1 confusion that was caught and corrected before the pin landed.
- **Reason chosen**: closest match to "Win11 Fluent + Discord/Spotify dense-dark" blend per re-plan WPF_LOCK section 2.2. Fluent System Icons + accent-aware controls + theme override path supports the brand-purple `#9333EA` override in App.xaml.
- **Known breaking advisory for next major (5.x)**: none surfaced. The 4.x line has 7 stable releases (4.0.0 -> 4.3.0) over the last 12 months suggesting steady cadence. RC2/RC3 prereleases were used for 4.0.0 transition; future majors likely follow same pattern.

### `CommunityToolkit.Mvvm` 8.4.2

- **Project**: https://github.com/CommunityToolkit/dotnet
- **License**: MIT.
- **Published**: 2026-03-25.
- **Maintainer**: Microsoft Community Toolkits org.
- **Reason chosen**: source-generator-driven (`[ObservableProperty]`, `[RelayCommand]`); AOT-friendly; canonical Microsoft-stewarded MVVM helper for modern .NET. Per re-plan WPF_LOCK section 2.3.
- **Known breaking advisory for next major (9.x)**: 8.4.x is the current minor; 9.x not yet announced at pin time.

### `Microsoft.Extensions.DependencyInjection` 9.0.15

- **Project**: https://github.com/dotnet/runtime
- **License**: MIT.
- **Published**: 2026-04-14.
- **Maintainer**: Microsoft (.NET runtime team).
- **Reason chosen**: standard DI container in .NET. Per re-plan WPF_LOCK section 2.4.
- **Note on version selection**: 9.0.x supports net8.0+ via floor-targeting; 8.0.1 (the net8.0 LTS counterpart) is older (2024-10-08) and triggers the 12-month gate. Pinning to 9.0.15 keeps the package recent without requiring net9.0 SDK; this is standard practice when net8.0-windows targeting + recent dependency selection both apply. 10.0.7 also viable; chose 9.0.15 for closer match to net8.0 LTS support window.
- **Known breaking advisory for next major (10.x)**: 10.x is already published (10.0.7 latest); generally backward-compatible per Microsoft's runtime versioning policy. Future Stage 7.x brief authors can move to 10.x freely if needed.

---

## csproj declaration shape (specs only -- no code in this deliverable)

The csproj will declare these four package references with exact
version pins (no floating versions, no `*`, no `>=`):

```
<PackageReference Include="H.NotifyIcon.Wpf" Version="2.3.2" />
<PackageReference Include="WPF-UI" Version="4.3.0" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.15" />
```

(Snippet shown for clarity; actual csproj written in STEP 2.3.)

---

## Verification

| Check | Status |
|---|:---:|
| Each package's NuGet id verified canonical (not typo-squatted) | PASS |
| Each license verified MIT | PASS (4 of 4) |
| Each published-date within 12-month gate | PASS (4 of 4: 5 mo / 4 days / ~6 wk / ~3.5 wk) |
| Multi-target compatibility with net8.0-windows | PASS (4 of 4) |
| No `prereleases-only` packages in pin list | PASS |

---

End of NuGet pins.
