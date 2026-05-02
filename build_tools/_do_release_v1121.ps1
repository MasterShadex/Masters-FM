Set-Location "G:\Project Folder\Master FM"
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public class CredMgr4 {
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

$token = [CredMgr4]::ReadPassword('git:https://github.com')
if (-not $token) { Write-Host "ERROR: no GitHub token in credential manager"; exit 1 }
Write-Host "Token read OK (length=$($token.Length))"

$hdrs = @{ Authorization = "token $token"; Accept = 'application/vnd.github.v3+json' }

$releaseBody = @"
v11.2.1 - SMTC cache-key stability fix

Fixes a real memory leak (~185 MB/hour while SoundCloud-RPC is connected)
and a track-change CPU spike (26%+ on high-end CPUs).

Root cause: tray.ps1 used `$Session.GetHashCode()` as a cache key, but the
COM proxy returned by Windows.Media.Control.GetSessions() has unstable
hash codes - each new SMTC manager produces fresh proxy wrappers with
new hashes for the same underlying source. With the manager being
re-acquired every 600ms, the per-session caches grew unbounded forever.

Empirically confirmed: three GetSessions() calls across three managers
produced three distinct hashes (48178028 / 36783249 / 16662697) for
the same SoundCloud-RPC source, while SourceAppUserModelId stayed
stable across all calls.

Fix: replaced `$Session.GetHashCode()` with `$Session.SourceAppUserModelId`
in 3 SMTC cache functions (Get-SMTCMediaPropsCached,
Get-SMTCPlaybackInfoCached, Get-SMTCPosition).
"@

$body = @{ tag_name = 'v11.2.1'; name = 'v11.2.1'; prerelease = $false; draft = $false; body = $releaseBody } | ConvertTo-Json
$rel  = Invoke-RestMethod -Uri 'https://api.github.com/repos/MasterShadex/Masters-FM/releases' `
    -Method Post -Headers $hdrs -Body $body -ContentType 'application/json'
Write-Host "Release created: id=$($rel.id) url=$($rel.html_url)"

$msiPath = "G:\Project Folder\Master FM\Master's FM Install\MastersFM_Setup.msi"
$msi = [System.IO.File]::ReadAllBytes($msiPath)
Write-Host "Uploading MSI ($([Math]::Round($msi.Length/1MB,1)) MB)..."
$up = Invoke-RestMethod -Uri "https://uploads.github.com/repos/MasterShadex/Masters-FM/releases/$($rel.id)/assets?name=Masters-FM-V11.2.1.msi" `
    -Method Post -Headers @{ Authorization="token $token"; 'Content-Type'='application/octet-stream' } -Body $msi
Write-Host "Asset uploaded: id=$($up.id) size=$($up.size) name=$($up.name)"

Write-Host "Running _push_update.ps1..."
& "G:\Project Folder\Master FM\_push_update.ps1"
