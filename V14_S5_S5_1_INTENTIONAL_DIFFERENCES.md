# V14 Stage 5 Sub-stage 5.1 -- Intentional Differences

Sub-stage 5.1: project skeleton (netstandard2.0 csproj, dotnet build path).
No behavioral changes. All differences are tooling-level only.

tray.ps1 loads the DLL identically (Add-Type -Path). End-user experience is identical.

---

## ID-36: DLL binary size differs between dotnet build and csc.exe build

**Component:** tray_native.dll

**csc.exe (.NET Framework 4.x compiler, legacy path):**
  Pre-sign:  33,304 bytes
  Post-sign: ~40,304 bytes (csc.exe path, after Authenticode signing)

**dotnet build (Roslyn SDK compiler, new path):**
  Pre-sign:  24,064 bytes
  Post-sign: 31,256 bytes (after Authenticode signing)

**Reason:** The .NET SDK Roslyn compiler emits a more compact PE image than the legacy
csc.exe compiler. Both assemblies are netstandard2.0 targets containing identical IL for
identical source. The binary layout differs due to:
  - Roslyn emitting a more compact metadata heap
  - Different PE header padding conventions
  - Different resource table layout

**Impact:** NONE. The IL is functionally equivalent. PS5.1 loads both via Add-Type -Path
without error. All 9 public types resolve in both cases. The Authenticode signature covers
the whole PE image in both cases (Status=Valid, Subject=CN=MasterShadex for both).

**Rollback:** Set $UseDotnetTrayNative=$false in _full_rebuild.ps1 to revert to the csc.exe
build path and its larger pre-sign binary.

---

## ID-37: dotnet build emits extra files alongside tray_native.dll

**Component:** Project root (G:\Project Folder\Master FM\)

**New files produced by dotnet build -o "$root":**
  tray_native.pdb         -- debug symbols (program database)
  tray_native.deps.json   -- runtime dependency manifest

**Legacy csc.exe path:**
  Only tray_native.dll is produced. No .pdb or .deps.json.

**Reason:** The .NET SDK always emits a .pdb (with /debug:portable by default in Release)
and a .deps.json for dependency resolution. These are benign extra outputs.

**Impact on tray.ps1:** NONE. tray.ps1 calls Add-Type -Path tray_native.dll -- it does not
read, require, or reference the .pdb or .deps.json files. PS5.1 runtime ignores them.

**Impact on MSI:** The MSI build (build_msi.py) explicitly enumerates files for packaging.
tray_native.pdb and tray_native.deps.json are NOT added to the MSI file list. They remain
in the project root as development artifacts, not shipped to end users.

**Note:** Future sub-stage 5.4 (build pipeline polish) may address whether to suppress .pdb
in Release or exclude it more explicitly. For sub-stage 5.1 (MINIMAL scope), these files
are accepted as is.

---

## ID-38: Compiler version changes from Framework 4.x csc.exe to Roslyn SDK

**Component:** tray_native.dll (build toolchain)

**Legacy:** C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
  Compiler: C# compiler from .NET Framework 4.0.30319 distribution
  Language version: implied C# 5 (Framework csc.exe default)
  Target: .NET Framework 4.x class library

**New:** dotnet build with Microsoft.NET.Sdk
  Compiler: Roslyn (Microsoft.CSharp.Core.targets, bundled with .NET SDK)
  Language version: "latest" (LangVersion=latest in csproj)
  Target: netstandard2.0 (compatible with .NET Framework 4.x and .NET Core 2.0+)

**Impact on runtime behavior:** NONE. The IL semantics are identical for the C# subset used
in tray_native.cs. tray_native.cs uses no features above C# 7 (no nullable reference types,
no pattern matching, no async streams). LangVersion=latest allows the compiler to accept
modern syntax but tray_native.cs does not use any of it.

**Impact on PS5.1 Add-Type:** NONE. PS5.1 (.NET Framework 4.8, CLR 4.0.30319.42000) loads
netstandard2.0 assemblies via Add-Type -Path identically to how it loads Framework 4.x
class libraries. Confirmed by PS5.1 load test: all 9 types resolve, PSVersion 5.1.26100.7920,
CLRVersion 4.0.30319.42000.

---

## Summary

| ID  | Component        | Change                                  | Impact          |
|-----|------------------|-----------------------------------------|-----------------|
| 36  | tray_native.dll  | Binary size smaller (24KB vs 33KB)      | None            |
| 37  | Project root     | Extra .pdb + .deps.json emitted         | None (not in MSI) |
| 38  | Build toolchain  | csc.exe -> Roslyn SDK compiler          | None            |

All differences are tooling-level. No behavioral changes. No user-visible changes.
PS5.1 load test PASS. csc.exe rollback path preserved ($UseDotnetTrayNative=$false).
