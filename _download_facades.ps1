# NAudio.Asio (netstandard2.0) pulls a chain of netstandard BCL facade
# assemblies when loaded on .NET Framework 4.x:
#   - Microsoft.Win32.Registry            (registry I/O, HKLM\SOFTWARE\ASIO)
#   - System.Security.AccessControl       (registry ACLs)
#   - System.Security.Principal.Windows   (Windows identity)
#   - System.Buffers                      (ArrayPool<T>)
#   - System.Memory                       (Span/Memory<T>)
#   - System.Numerics.Vectors             (SIMD helpers)
#   - System.Runtime.CompilerServices.Unsafe  (unsafe BCL helpers)
# We grab the RUNTIME DLL from each package (runtimes\win\lib or
# lib\netstandard2.0) and deposit it at project root. Builds that don't
# need a given facade just won't load it - no runtime cost on machines
# where a given backend isn't exercised.
$ErrorActionPreference = 'Stop'

$outDir   = 'F:\Claude AI\Master FM\build_tools\naudio'
$rootProj = 'F:\Claude AI\Master FM'

$packages = @(
    @{ Id = 'System.Buffers';                         Version = '4.5.1'  },
    @{ Id = 'System.Memory';                          Version = '4.5.5'  },
    @{ Id = 'System.Numerics.Vectors';                Version = '4.5.0'  },
    @{ Id = 'System.Runtime.CompilerServices.Unsafe'; Version = '6.0.0'  },
    @{ Id = 'System.Security.AccessControl';          Version = '5.0.0'  },
    @{ Id = 'System.Security.Principal.Windows';      Version = '5.0.0'  }
)

function Try-Copy-Dll($extRoot, $dllName, $dstRoot) {
    $candidates = @(
        (Join-Path $extRoot "runtimes\win\lib\netstandard2.0\$dllName"),
        (Join-Path $extRoot "lib\netstandard2.0\$dllName"),
        (Join-Path $extRoot "lib\net461\$dllName"),
        (Join-Path $extRoot "runtimes\win\lib\net461\$dllName")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $dst = Join-Path $dstRoot $dllName
            Copy-Item $c $dst -Force
            return $dst
        }
    }
    # Last-ditch: find any copy of the DLL in the extracted tree
    $hit = Get-ChildItem $extRoot -Recurse -Filter $dllName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) {
        $dst = Join-Path $dstRoot $dllName
        Copy-Item $hit.FullName $dst -Force
        return $dst
    }
    return $null
}

foreach ($p in $packages) {
    $url = "https://www.nuget.org/api/v2/package/$($p.Id)/$($p.Version)"
    $zip = Join-Path $outDir "$($p.Id).zip"
    $ext = Join-Path $outDir $p.Id
    Write-Host "Downloading $($p.Id) $($p.Version)..."
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 60
    if (Test-Path $ext) { Remove-Item $ext -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $ext -Force

    $dll = "$($p.Id).dll"
    $out = Try-Copy-Dll $ext $dll $rootProj
    if ($out) {
        Write-Host "  -> $out ($((Get-Item $out).Length) bytes)"
    } else {
        Write-Host "  WARN: no $dll found in package"
    }
}

Write-Host ""
Write-Host "All facade DLLs at project root:"
Get-ChildItem $rootProj -Filter 'System.*.dll' | Select-Object Name, Length | Format-Table -AutoSize
Get-ChildItem $rootProj -Filter 'Microsoft.Win32.*.dll' | Select-Object Name, Length | Format-Table -AutoSize
