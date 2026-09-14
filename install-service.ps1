$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $projectDir 'publish'
$serviceName = 'PhoneUnlockService'

Push-Location $projectDir
try {
    dotnet publish -c Release -r win-x64 --self-contained false -o $publishDir
}
finally {
    Pop-Location
}

$exePath = Join-Path $publishDir 'PhoneUnlockService.exe'
if (-not (Test-Path $exePath)) {
    throw "Expected published executable at $exePath"
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
}

sc.exe create $serviceName binPath= "$exePath" start= auto
sc.exe failure $serviceName reset= 0 actions= restart/1000
sc.exe start $serviceName

Write-Host "Installed $serviceName as a LocalSystem service."
Write-Host "Make sure appsettings.json in $publishDir points to the correct relay URL before first start."
