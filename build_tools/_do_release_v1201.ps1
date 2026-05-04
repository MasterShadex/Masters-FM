Set-Location "G:\Project Folder\Master FM"
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public class CredMgr1201 {
    [DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")] public static extern void CredFree(IntPtr buffer);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct CREDENTIAL {
        public uint Flags; public uint Type; public string TargetName; public string Comment;
        public long LastWritten; public uint CredentialBlobSize; public IntPtr CredentialBlob;
        public uint Persist; public uint AttributeCount; public IntPtr Attributes;
        public string TargetAlias; public string UserName;
    }
    public static string ReadPassword(string target) {
        IntPtr ptr;
        if (!CredRead(target, 1, 0, out ptr)) return null;
        try {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (c.CredentialBlobSize == 0) return "";
            byte[] blob = new byte[(int)c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, blob, 0, (int)c.CredentialBlobSize);
            return Encoding.Unicode.GetString(blob);
        } finally { CredFree(ptr); }
    }
}
'@

$token = [CredMgr1201]::ReadPassword('git:https://github.com')
if (-not $token) { Write-Host "ERROR: no GitHub token in credential manager"; exit 1 }
Write-Host "Token read OK (length=$($token.Length))"

# Stage and commit source state first
Write-Host "Staging + committing v12.0.1 source..."
git add src/tray.ps1 VERSIONING_POLICY.md V1201_LOG.md build_tools/_do_release_v1201.ps1 V1200_PATCH_NOTES_CRASH_LOG.md V1200_PATCH_NOTES_CRASH_DIAGNOSIS.md
git commit -m "v12.0.1 - Patch notes performance + crash hardening (Bug A/B/C) + versioning policy"
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git commit failed (exit $LASTEXITCODE)"; exit 2 }

Write-Host "Pushing v12.0.1 commit to origin/main..."
git push origin main
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git push failed"; exit 3 }

$hdrs = @{ Authorization = "token $token"; Accept = 'application/vnd.github.v3+json' }

$releaseBody = @"
v12.0.1 - Patch notes performance + crash hardening

Bug fixes shipped:
- First-launch: tray icon now appears before patch notes processing (Bug A)
- Patch notes: virtualized owner-draw rendering, opens in ~2 seconds instead of 10+ (Bug B)
- Patch notes: defensive timer disposal cleanup on close (Bug C)

This is a patch release per the new versioning policy:
- patch increments for every routine update (12.0.0 -> 12.0.1)
- minor reserved for when patch rolls over from .9 (12.0.9 -> 12.1.0)
- major reserved for architectural refactors where a whole subsystem is replaced (v11.x -> v12.0.0 was SMTC polling -> event-driven)

See VERSIONING_POLICY.md in the repo root for full rules.

Built and tested with the first Ruflo swarm coordinated task on Master's FM.
"@

$body = @{ tag_name = 'v12.0.1'; name = 'v12.0.1'; prerelease = $false; draft = $false; body = $releaseBody } | ConvertTo-Json
$rel  = Invoke-RestMethod -Uri 'https://api.github.com/repos/MasterShadex/Masters-FM/releases' `
    -Method Post -Headers $hdrs -Body $body -ContentType 'application/json'
Write-Host "Release created: id=$($rel.id) url=$($rel.html_url)"

$msiPath = "G:\Project Folder\Master FM\Master's FM Install\MastersFM_Setup.msi"
$msi = [System.IO.File]::ReadAllBytes($msiPath)
Write-Host "Uploading MSI ($([Math]::Round($msi.Length/1MB,1)) MB)..."
$up = Invoke-RestMethod -Uri "https://uploads.github.com/repos/MasterShadex/Masters-FM/releases/$($rel.id)/assets?name=Masters-FM-V12.0.1.msi" `
    -Method Post -Headers @{ Authorization="token $token"; 'Content-Type'='application/octet-stream' } -Body $msi
Write-Host "Asset uploaded: id=$($up.id) size=$($up.size) name=$($up.name)"

Write-Host "Running _push_update.ps1 to flip autoInstall=true..."
& "G:\Project Folder\Master FM\_push_update.ps1"
