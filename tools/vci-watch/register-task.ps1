param(
    [int]$IntervalMinutes = 10,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$TaskName = 'TiaVciWatch'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
# 必须用 pythonw.exe：python.exe 是控制台程序，计划任务每次触发都会在用户桌面闪一个黑窗。
$py = (Get-Command pythonw -ErrorAction SilentlyContinue).Source
if (-not $py) { $py = (Get-Command python).Source -replace 'python\.exe$','pythonw.exe' }
if (-not (Test-Path $py)) { throw "找不到 pythonw.exe（$py）" }
$script = Join-Path $here 'watch.py'

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    "已删除计划任务 $TaskName"
    return
}

if (-not (Test-Path $script)) { throw "找不到 $script" }

# 只在当前用户登录时跑：看门狗要附着到工程师自己开着的博途，跑在别的会话里没有意义。
# 注意：-RepetitionInterval 必须配 -RepetitionDuration，否则 Windows PowerShell 5.1 会
# 报 ParameterArgumentValidationError（踩过）。用 10 年当"永久"。
$action  = New-ScheduledTaskAction -Execute $py -Argument ('"{0}"' -f $script) -WorkingDirectory $here
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
           -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
           -RepetitionDuration (New-TimeSpan -Days 3650)
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 15) `
            -MultipleInstances IgnoreNew -Priority 7

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -Description "博途程序变更自动导出+git提交（只读附着，绝不打开工程、绝不写工程）" -Force | Out-Null

"已注册计划任务 $TaskName，每 $IntervalMinutes 分钟一轮。"
"查看：Get-ScheduledTask $TaskName | Get-ScheduledTaskInfo"
"暂停：Disable-ScheduledTask $TaskName    恢复：Enable-ScheduledTask $TaskName"
"卸载：.\register-task.ps1 -Remove"
