Set-Location "G:\Project Folder\Master FM"
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public class CredMgr12 {
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

$token = [CredMgr12]::ReadPassword('git:https://github.com')
if (-not $token) { Write-Host "ERROR: no GitHub token in credential manager"; exit 1 }
Write-Host "Token read OK (length=$($token.Length))"

# 1. Push the v12.0.0 commit to main FIRST (release needs the tag to point to a pushed commit)
Write-Host "Pushing v12.0.0 commit to origin/main..."
git push origin main
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git push failed"; exit 2 }

$hdrs = @{ Authorization = "token $token"; Accept = 'application/vnd.github.v3+json' }

$releaseBody = @"
v12.0.0 - Major architectural refactor

Master's FM no longer lags your PC on track changes.

What changed:
The SMTC integration was rewritten from polling-based to event-driven.
Previously, the tray polled Windows about playing media every 100-600ms,
which contended with media apps (especially soundcloud-rpc) during track
transitions, causing 3-5 second system-wide lag spikes (games dropping
600 to 0 FPS, mouse stutter, Windows feeling frozen).

The new architecture:
- Subscribes to Windows SMTC events instead of polling
- Maintains an in-memory snapshot updated by event handlers
- Zero ALPC traffic to the SMTC service in steady state
- Same data, same features, no more contention

Measured improvements:
- Single track skip: 600 to 0 FPS for 3-5s, now ~10 FPS dip
- Rapid track skipping: now within 30 FPS of 'Master's FM closed entirely'

Preserved:
- All v11.2.1/2/3 fixes (cache key stability, idle CPU fix, art refresh, Discord RPC)
- All 4 audio backends (SMTC, foobar2000, WMP, VLC)
- Auto-update flow

This is a major version bump because a core subsystem was replaced.
Note: shipped without the full 4-hour soak validation. If you observe
memory growth in the next 24-48 hours, please report - we will ship
v12.0.1 quickly.
"@

$body = @{ tag_name = 'v12.0.0'; name = 'v12.0.0'; prerelease = $false; draft = $false; body = $releaseBody } | ConvertTo-Json
$rel  = Invoke-RestMethod -Uri 'https://api.github.com/repos/MasterShadex/Masters-FM/releases' `
    -Method Post -Headers $hdrs -Body $body -ContentType 'application/json'
Write-Host "Release created: id=$($rel.id) url=$($rel.html_url)"

$msiPath = "G:\Project Folder\Master FM\Master's FM Install\MastersFM_Setup.msi"
$msi = [System.IO.File]::ReadAllBytes($msiPath)
Write-Host "Uploading MSI ($([Math]::Round($msi.Length/1MB,1)) MB)..."
$up = Invoke-RestMethod -Uri "https://uploads.github.com/repos/MasterShadex/Masters-FM/releases/$($rel.id)/assets?name=Masters-FM-V12.0.0.msi" `
    -Method Post -Headers @{ Authorization="token $token"; 'Content-Type'='application/octet-stream' } -Body $msi
Write-Host "Asset uploaded: id=$($up.id) size=$($up.size) name=$($up.name)"

Write-Host "Running _push_update.ps1..."
& "G:\Project Folder\Master FM\_push_update.ps1"
