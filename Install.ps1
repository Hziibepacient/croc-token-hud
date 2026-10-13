$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskExe = Join-Path $taskRoot 'CodexTokenHud.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { & (Join-Path $taskRoot 'Build.ps1') }
$taskSetup = Start-Process -FilePath $taskExe -ArgumentList '--enable-app-follow' -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
if (-not $taskSetup.WaitForExit(35000)) { throw '启用随应用启动超时。' }
if ($taskSetup.ExitCode -ne 0) { throw '无法启用随应用启动。请检查权限，或先关闭另一副本的自动启动。' }
$taskShell = New-Object -ComObject WScript.Shell
$taskLink = $taskShell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Token 状态条.lnk'))
$taskLink.TargetPath = $taskExe
$taskLink.WorkingDirectory = $taskRoot
$taskLink.Description = '显示当前 Codex 对话的 token 速率与缓存命中率，支持拖动和调整大小'
$taskLink.WindowStyle = 1
$taskLink.Save()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskLink) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell) | Out-Null
Write-Output '已创建桌面入口并启用随 Codex / CLI / IDE 启动。未运行客户端时只等待，不打开状态条或采集对话。'
