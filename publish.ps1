# Сборка RemoteConnect.exe — один файл, без установки .NET на целевом ПК.
# Запуск: powershell -ExecutionPolicy Bypass -File .\publish.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet test RemoteConnect.sln -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли' }

dotnet publish src/RemoteConnect.App -c Release -r win-x64 -p:PublishSingleFile=true -p:DebugType=none -o publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }

Get-ChildItem publish -Filter *.pdb | Remove-Item
Write-Host "Готово: $(Resolve-Path publish\RemoteConnect.exe)"
