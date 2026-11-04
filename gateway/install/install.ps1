# r8er gateway: install, or update in place (item 4). The web app's
# Gateways -> Add gateway shows the first-run command (it sets R8ER_API_URL +
# R8ER_ENROLL_TOKEN); later, updating is just:
#   irm https://github.com/bifi627/r8er/releases/latest/download/install.ps1 | iex
# The state dir (cert, credential, paired browsers) is never touched.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes downloads ~10x slower
$dir = if ($env:R8ER_DIR) { $env:R8ER_DIR } else { Join-Path $env:LOCALAPPDATA 'r8er' }
$dl = if ($env:R8ER_DOWNLOAD) { $env:R8ER_DOWNLOAD } else { 'https://github.com/bifi627/r8er/releases/latest/download' }
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'amd64' }
$bin = "r8er-gateway-windows-$arch.exe"
$exe = Join-Path $dir 'gateway.exe'
$envFile = Join-Path $dir 'gateway.env'

if (-not (Test-Path $envFile) -and -not $env:R8ER_API_URL) {
    throw 'r8er: first install needs the command from the web app (Gateways -> Add gateway)'
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) "r8er-$([guid]::NewGuid())"
New-Item -ItemType Directory $tmp | Out-Null
try {
    Invoke-WebRequest "$dl/$bin" -OutFile (Join-Path $tmp $bin)
    Invoke-WebRequest "$dl/SHA256SUMS" -OutFile (Join-Path $tmp 'SHA256SUMS')
    $want = (Get-Content (Join-Path $tmp 'SHA256SUMS') | Where-Object { $_ -match "[ *]$([regex]::Escape($bin))$" }) -split '\s+' | Select-Object -First 1
    $got = (Get-FileHash (Join-Path $tmp $bin) -Algorithm SHA256).Hash
    if (-not $want -or $got -ne $want) { throw "r8er: checksum mismatch for $bin, not installing" }

    New-Item -ItemType Directory -Force (Join-Path $dir 'state') | Out-Null
    # Running copy (update): stop it so the exe can be replaced.
    Get-Process gateway -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    Start-Sleep -Seconds 1
    Move-Item -Force (Join-Path $tmp $bin) $exe
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

if (-not (Test-Path $envFile)) {
    $lines = @("R8ER_API_URL=$env:R8ER_API_URL", "R8ER_STATE_DIR=$(Join-Path $dir 'state')")
    # Optional first-install settings persist, so updates keep them.
    foreach ($k in 'R8ER_NAME', 'R8ER_PAGE_PORT') {
        $v = [Environment]::GetEnvironmentVariable($k)
        if ($v) { $lines += "$k=$v" }
    }
    Set-Content -Encoding ascii $envFile $lines
}

# The enrollment token only matters on the first start (inherited env); never stored.
Start-Process $exe -WorkingDirectory $dir -WindowStyle Hidden `
    -RedirectStandardError (Join-Path $dir 'gateway.log') -RedirectStandardOutput (Join-Path $dir 'gateway.out.log')
Remove-Item Env:R8ER_ENROLL_TOKEN -ErrorAction SilentlyContinue
Write-Host "r8er gateway started"
$port = (Get-Content $envFile | Where-Object { $_ -match '^R8ER_PAGE_PORT=' }) -replace '^R8ER_PAGE_PORT=', ''
Write-Host "  local page: http://localhost:$(if ($port) { $port } else { '8097' })"
Write-Host "  log:        $(Join-Path $dir 'gateway.log')"
Write-Host "  Windows Firewall may ask once: allow on private networks."
Write-Host "  autostart (not set up for you):"
Write-Host "    schtasks /Create /SC ONLOGON /TN r8er-gateway /TR `"'$exe'`""
