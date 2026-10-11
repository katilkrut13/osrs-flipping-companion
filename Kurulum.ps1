$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'Flipping-Bot.exe'
$jar = Join-Path $root 'flip-bridge.jar'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $jar)) { throw 'Paket dosyalari eksik.' }
if (Get-Process RuneLite,javaw -ErrorAction SilentlyContinue) { throw 'Kurulumdan once RuneLite ve Java pencerelerini kapatin.' }
$runeLite = Join-Path $env:LOCALAPPDATA 'RuneLite'
$runeConfig = Join-Path $runeLite 'config.json'
if (-not (Test-Path -LiteralPath $runeConfig)) { throw 'RuneLite bulunamadi. Once resmi RuneLite/Jagex Launcher ile RuneLite kurulumunu tamamlayin.' }
$data = Join-Path $env:LOCALAPPDATA 'OsrsFlipping'
$bridgeDir = Join-Path $data 'bridge\build\libs'
New-Item -ItemType Directory -Path $bridgeDir -Force | Out-Null
Copy-Item -LiteralPath $jar -Destination (Join-Path $bridgeDir 'flip-bridge.jar') -Force
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Copy-Item -LiteralPath $runeConfig -Destination ($runeConfig + '.backup-' + $stamp) -Force
$cfg = Get-Content -LiteralPath $runeConfig -Raw | ConvertFrom-Json
$vm = @($cfg.vmArgs | Where-Object { $_ -notlike '-Dflip.directory=*' -and $_ -ne '-Dflip.allowInput=true' -and $_ -ne '-ea' })
$vm += '-ea'
$vm += '-Dflip.directory=' + $data
$vm += '-Dflip.allowInput=true'
$cfg.mainClass = 'local.flip.BridgeMain'
$cfg.classPath = @((Join-Path $bridgeDir 'flip-bridge.jar'))
$cfg.vmArgs = $vm
$cfg | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $runeConfig -Encoding UTF8
$desktop = [Environment]::GetFolderPath('Desktop')
Copy-Item -LiteralPath $exe -Destination (Join-Path $desktop 'Flipping-Bot.exe') -Force
Write-Host ''
Write-Host 'Kurulum tamamlandi.' -ForegroundColor Green
Write-Host 'Jagex Launcher ile RuneLite acin, Grand Exchange ekranina gelin ve masaustundeki Flipping-Bot.exe dosyasini calistirin.'
Write-Host ''
Read-Host 'Kapatmak icin Enter'
