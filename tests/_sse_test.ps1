# Connect to audio_spectrum.exe SSE endpoint, read 3 frames, print them.
# Used to verify the spectrum server is streaming data while audio plays.
$req = [System.Net.WebRequest]::Create('http://127.0.0.1:4243/spectrum')
$req.Timeout = 3000
try {
    $resp = $req.GetResponse()
} catch {
    Write-Host "ERROR: cannot connect: $_"
    exit 1
}
$sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
$n = 0
while ($n -lt 3) {
    $line = $sr.ReadLine()
    if ($line -and $line.StartsWith('data:')) {
        $show = if ($line.Length -gt 200) { $line.Substring(0, 200) + '...' } else { $line }
        Write-Host $show
        $n++
    }
}
$resp.Close()
