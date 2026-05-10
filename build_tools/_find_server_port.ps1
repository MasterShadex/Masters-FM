foreach ($p in 3000,8080,5000,5001,7000,7001,9000,9090,4000,4001,54835,54836) {
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:$p/current" -UseBasicParsing -TimeoutSec 1 -ErrorAction Stop
        $preview = $r.Content.Substring(0, [Math]::Min(200, $r.Content.Length))
        Write-Host "PORT $p OK: $preview"
        break
    } catch { }
}
Write-Host "Port scan complete"
