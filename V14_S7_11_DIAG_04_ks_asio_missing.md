# Diagnosis: Issue 4 -- KS and ASIO Missing/Incomplete

## Reproduction (operator-confirmed)
Audio Source dialog shows "WAS" and "M" tabs only (see DIAG_03 for tab truncation).
Operator expected 4 tabs: WASAPI / MME / KS / ASIO. INTERRUPT #3 brief specified this layout.
KS and ASIO tabs appear absent at runtime.

---

## Source-of-truth analysis

### WASAPI tab

**XAML (AudioDeviceWindow.xaml, line 282):**
```xaml
<TabItem Header="WASAPI">
    <ScrollViewer ...>
        <ListBox ItemsSource="{Binding OutputDevices}" .../>
    </ScrollViewer>
</TabItem>
```
No `Visibility` binding. Always visible.

**ViewModel (AudioDeviceViewModel.cs, line 132):**
`OutputDevices` populated via `EnumerateWasapiDevices()`. Implementation exists.

**Status:** DEFINED + IMPLEMENTED + VISIBLE + DATA-FLOWING

---

### MME tab

**XAML (AudioDeviceWindow.xaml, line 294-304):**
```xaml
<TabItem Header="MME"
         Visibility="{Binding HasMme, Converter={StaticResource BoolToVis},
                              FallbackValue=Visible}">
```
Visible only when `HasMme = true`.

**ViewModel:** `HasMme = MmeDevices.Count > 0` (line 132). `MmeDevices` populated when MME devices are found.

**Status:** DEFINED + IMPLEMENTED + CONDITIONALLY VISIBLE + DATA-FLOWING (when MME devices present)

---

### KS tab

**XAML (AudioDeviceWindow.xaml, lines 307-324):**
```xaml
<TabItem Header="KS">
    <StackPanel ...>
        <TextBlock Text="Kernel Streaming devices appear here when available." .../>
    </StackPanel>
</TabItem>
```

**Critical finding:** NO `Visibility` binding on the KS TabItem. It is **always visible** in the XAML definition.

**ViewModel (AudioDeviceViewModel.cs):** No `EnumerateKsDevices()` method exists. No `KsDevices` collection. No `HasKs` property. The KS tab is a static placeholder with no data binding and no enumeration.

**Why operator does not see the KS tab:** From DIAG_03 analysis, the tab label truncation issue means "KS" (a short label) may be rendered at very small width. Additionally, the tab strip may only be showing the first 2-3 tabs depending on the layout issue. If all 3 visible tabs (WASAPI, MME, KS) are displayed but compressed, the operator may have interpreted the compressed "K" as absent rather than truncated.

**Alternative explanation:** The KS tab IS present but its tab header renders so narrow that it shows no visible text, making it appear absent.

**Status:** DEFINED IN XAML (always visible) + NOT IMPLEMENTED (no enumeration) + ALWAYS VISIBLE (static placeholder) + NO DATA (empty state only)

---

### ASIO tab

**XAML (AudioDeviceWindow.xaml, lines 327-349):**
```xaml
<TabItem Header="ASIO"
         Visibility="{Binding HasAsio, Converter={StaticResource BoolToVis},
                              FallbackValue=Visible}">
    <StackPanel ...>
        <TextBlock ...>
            ASIO devices are configured directly in your DAW.
            Master's FM doesn't need a separate ASIO selection.
        </TextBlock>
    </StackPanel>
</TabItem>
```

`Visibility` is bound to `HasAsio`.

**ViewModel (AudioDeviceViewModel.cs, line 134):**
```csharp
public bool HasAsio => AsioDevices.Count > 0;
```

`AsioDevices` is never populated. No `EnumerateAsioDevices()` method exists. No ASIO enumeration of any kind in the codebase.

**Result:** `AsioDevices.Count = 0` always → `HasAsio = false` always → ASIO tab is **always hidden at runtime**.

**Status:** DEFINED IN XAML (hidden by binding) + NOT IMPLEMENTED (no enumeration; correct per design -- ASIO is not enumerable via Windows APIs) + ALWAYS HIDDEN (HasAsio=false permanently) + NO DATA

**Design intent verified:** The ASIO tab content ("devices configured directly in your DAW") confirms ASIO is an informational static message. The intent was to show the tab as a deferral notice. But `HasAsio=false` prevents the tab from ever appearing. To match the design intent, the ASIO tab should use `Visibility="Visible"` (always shown as an informational message) rather than `HasAsio`-gated visibility.

---

## Summary per audio backend

| Backend | XAML tab defined | Visibility condition | Enumeration implemented | Tab visible at runtime |
|---------|-----------------|---------------------|------------------------|----------------------|
| WASAPI | YES | Always visible | YES | YES |
| MME | YES | HasMme (true when devices present) | YES | YES (if devices found) |
| KS | YES | Always visible | NO (static placeholder only) | YES (but may show truncated) |
| ASIO | YES | HasAsio (always false) | NO (by design; not enumerable) | NO (always hidden) |

---

## Root cause (Issue 4)

**KS not visible:** KS tab IS defined and always-visible in XAML. Operator not seeing it is most likely the tab label truncation from Issue 3 (KS tab header appears as an invisible/zero-width column). KS content = static placeholder -- the INTERRUPT #3 deferral was implemented as a placeholder without enumeration.

**ASIO not visible:** `HasAsio` is permanently `false` because `AsioDevices` collection is never populated. The ASIO tab was designed to be an informational deferral message (not a real device list), but the `Visibility` binding gates it on `HasAsio`, which requires a non-empty ASIO device list that can never exist (ASIO is not Windows-enumerable). The visibility condition is inverted from the design intent: ASIO should always be visible as an informational tab, not hidden when there are no ASIO devices.

---

## Fix complexity

**KS:** The tab is already visible (XAML is correct). The "fix" is dependent on DIAG_03's tab truncation fix: once tab labels render at their correct width, "KS" will be visible. No separate KS fix needed unless the operator wants real KS enumeration.

**ASIO:** trivial (1-line) -- change `Visibility="{Binding HasAsio, ...}"` to `Visibility="Visible"` on the ASIO TabItem. This makes the informational message always visible, matching the design intent.

If the operator wants real KS enumeration: medium (requires P/Invoke KS device enumeration, AudioDeviceService update, KsDevices collection in ViewModel -- ~3-5 hours Ruflo).

---

## Recommendation

**ASIO:** Always-visible informational tab. Fix is trivial (remove HasAsio gating). INTERRUPT #3 called for "ASIO tab shows informational message" -- the message is there, the visibility binding is wrong.

**KS enumeration:** Per the original INTERRUPT #3 deferral comment in the source, KS was deferred intentionally. Recommendation: keep as static placeholder with the existing text, just ensure the tab is visible after tab truncation is fixed (DIAG_03 fix). Do NOT implement KS enumeration for v14 release; mark as v14.1 backlog.

---

## Verification after fix

1. Open Audio Source dialog
2. Confirm 3 visible tabs: WASAPI, MME (if devices present), KS (placeholder)
3. Confirm ASIO informational tab is also visible (after removing HasAsio gating)
4. Confirm ASIO tab content shows "ASIO devices are configured directly in your DAW..."
5. Confirm KS tab content shows "Kernel Streaming devices appear here when available."
