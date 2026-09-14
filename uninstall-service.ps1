$ErrorActionPreference = 'Stop'

$serviceName = 'PhoneUnlockService'

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
}

Write-Host "Removed $serviceName service."
