# HeadlessTester

A WPF UI smoke tester for the Master's FM tray. Runs fully headless (no windows
pop up on your display, no mouse/keyboard control). Catches the exact class of
bug that shipped in v14.2.0 + v14.2.1: a `MenuItem` with children but no
`Command`, so clicking the header did nothing visible.

## What it does

1. **Tray `ContextMenu` structural walk** — reads `MainWindow.xaml`, walks
   every `MenuItem`, flags any top-level item that has sub-items but no
   `Command`. Saves the full menu tree as text for `diff`-ing across commits.

2. **Each dialog window XAML** — for each file under
   `src/tray_csharp/Dialogs/*.xaml`:
   - Loads via `XamlReader.Load` (after stripping `x:Class`, `x:Shared`, and
     event-handler attributes that need compiled code-behind).
   - Forces layout via `Measure`/`Arrange`/`UpdateLayout` at the window's
     declared dimensions.
   - Renders to PNG via `RenderTargetBitmap` — fully offscreen, no `Show()`,
     no HWND, no visible pop-up.
   - Walks the visual tree, dumps a textual representation.

3. **Summary report** — `report.md` with per-window pass/fail, anomaly list,
   and counts.

## Run it

```powershell
dotnet build tests/HeadlessTester/HeadlessTester.csproj
dotnet run --project tests/HeadlessTester/HeadlessTester.csproj
```

Output lands in
`tests/HeadlessTester/bin/Debug/net8.0-windows/snapshots/<timestamp>/`:

```
report.md                  # overall pass/fail + anomalies
ctx_menu_tree.txt          # full tray ContextMenu structural walk
<Window>.png               # offscreen render per dialog
<Window>.tree.txt          # visual tree dump per dialog
```

Exit code 0 if no anomalies/failures, non-zero otherwise — usable as a
pre-ship gate.

## What it catches well

- `MenuItem` with sub-items but no `Command` (the Audio source bug shape)
- XAML files that fail to parse (compile-only attributes, missing namespaces)
- Visual-tree changes between commits (textual `diff` over the `.tree.txt`)
- New dialogs that throw during construction

## Honest limitations

- **Data-bound text content stays empty.** No `DataContext` is set on the
  loaded XAML, so `{Binding ...}` expressions resolve to nothing. Rendered
  PNGs show structure + styles but no values.
- **Theme styles don't always resolve.** `StaticResource` lookups across
  merged dictionaries that depend on compiled BAML may fail; the tester
  strips `x:Shared` to maximise survival but some dialogs still fail to load
  (`AudioDeviceWindow`, `ErrorDialogWindow` currently — flagged in the
  report and dumped as XML structure instead of WPF tree).
- **Runtime command behaviour isn't exercised.** This is a structural +
  rendering smoke test, not an end-to-end interaction test.

## Future expansion

- `ProjectReference` the tray and load XAML via baml so `DataContext` +
  themes resolve cleanly. Means the harness depends on a clean tray build —
  which is exactly what you want before shipping.
- Boot a minimal DI container with stubs for `IConfigService`/`HttpClient`/
  `ILogger`, resolve every `ViewModel`, call `.Execute()` on every
  `[RelayCommand]`, capture any thrown exception.
- For `SetupWizardWindow`: iterate `WizardStep` values, capture each
  step's PNG, verify all steps render without binding errors.
