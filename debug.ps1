dotnet publish Flow.Launcher.Plugin.YtmPlugin -c Debug -r win-x64 --no-self-contained

$AppDataFolder = [Environment]::GetFolderPath("ApplicationData")
$flowLauncherExe = "$env:LOCALAPPDATA\FlowLauncher\Flow.Launcher.exe"

if (Test-Path $flowLauncherExe) {
    Stop-Process -Name "Flow.Launcher" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    if (Test-Path "$AppDataFolder\FlowLauncher\Plugins\YtmPlugin") {
        Remove-Item -Recurse -Force "$AppDataFolder\FlowLauncher\Plugins\YtmPlugin"
    }

    Copy-Item "Flow.Launcher.Plugin.YtmPlugin\bin\Debug\win-x64\publish" "$AppDataFolder\FlowLauncher\Plugins\" -Recurse -Force
    Rename-Item -Path "$AppDataFolder\FlowLauncher\Plugins\publish" -NewName "YtmPlugin"

    Start-Sleep -Seconds 2
    Start-Process $flowLauncherExe
} else {
    Write-Host "Flow.Launcher.exe not found. Please install Flow Launcher first"
}
