# 生成测试清单 Excel（单工作表、测试分类 / 测试内容 / 测试结果三列，结果全部留空）
# 格式遵循记忆库《测试清单规范》：00-System/测试清单规范.md
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-testchecklist.ps1
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = Join-Path $repoRoot 'docs\测试清单-LN-Mirror-0.1.0.xlsx'

$title = 'LN-Mirror 0.1.0 测试清单（USB 有线版，含半透明与鼠标穿透）'
$note = '请把每一条都实际操作一遍，结果栏自己填；不是"通过"的情况照实写现象就行。'

# 每行写成 分类|内容
$rowsText = @'
安装启动|双击 LN-Mirror.exe 能正常打开，出现主窗口，托盘里也有图标。
安装启动|不插手机时打开程序，提示"未发现设备"，不会一直转圈或报错。
安装启动|主窗口点"环境自检"，能看到一份逐项结果清单。
设备连接|插上已授权的手机，设备列表里能看到这台手机，选中后点"连接"能出画面。
设备连接|手机还没授权时，程序提示"等待手机授权"，不会一直加载。
设备连接|插着两台手机时，能选其中一台连接，不会把两台混在一起。
设备连接|手机横竖屏切换后，画面比例正确，鼠标点击的位置对得上。
半透明画面|把不透明度拉到 50%，画面变半透明，能同时看到下面的电脑内容。
半透明画面|不透明度拖到 0，画面完全看不见，但工具条和托盘还看得到、点得到。
半透明画面|从 0 拖回任意非零数值，画面立刻恢复显示。
半透明画面|50% 附近操作手机时没有明显卡顿、闪烁或整块发黑。
半透明画面|如果半透明没效果，按说明改渲染后端再重连，能找到一个能用的组合。
鼠标操作|在画面上单击、长按拖动、滑动、滚轮滚动都能在手机上生效。
鼠标操作|在画面上点右键能返回上一个界面，点中键能回到桌面。
鼠标操作|画面 100% 和半透明两种状态下，点击位置都准确。
穿透与恢复|打开鼠标穿透后点画面，点击落到下面的软件上，不是操作手机。
穿透与恢复|关闭穿透后点画面，点击又回到操作手机。
穿透与恢复|半透明状态下也能单独打开穿透，两项互不影响。
穿透与恢复|点"验证鼠标穿透"，能给出明确结论（已生效 / 未生效 / 未确认）。
穿透与恢复|按住鼠标不放时点穿透切换，会提示等松手，手机不会卡在拖动或长按。
穿透与恢复|点工具条"恢复 100"后：不透明度变 100、穿透关闭、窗口回到可见位置。
穿透与恢复|从托盘和从快捷键也能恢复画面，三种入口都能用。
画质与流畅度|三档画质切换后点"应用设置并重连"生效，主窗口的引擎帧率有数值。
画质与流畅度|流畅档下操作明显比清晰档更跟手。
异常与恢复|连接中拔掉数据线，画面自动停止并给出提示。
异常与恢复|拔掉后重新插上（手机已授权），能自己重新出画。
异常与恢复|手机上撤销授权后，程序回到"等待手机授权"，不会反复重连。
异常与恢复|把画面窗口拖到一块拔掉的显示器上，用"找回画面"能把它拉回屏幕。
异常与恢复|电脑休眠再唤醒后，画面和工具条仍然可用。
异常与恢复|设备列表里拔掉一台未连接的设备，程序不会出错。
退出与清理|关掉主窗口后程序仍在托盘运行，能从托盘重新打开主窗口。
退出与清理|从托盘点退出后，任务管理器里没有残留的 scrcpy.exe 进程。
退出与清理|退出后手机屏幕恢复原状，没有残留投屏状态。
'@

$rows = @($rowsText -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { , $_.Split('|', 2) })

if (Test-Path $outputPath) { Remove-Item $outputPath -Force }

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false

try {
    $workbook = $excel.Workbooks.Add()
    $sheet = $workbook.Worksheets.Item(1)
    $sheet.Name = '测试清单'

    $lastColumn = 3
    $lastRow = $rows.Count + 3

    $sheet.Cells.Item(1, 1).Value2 = $title
    $sheet.Range($sheet.Cells.Item(1, 1), $sheet.Cells.Item(1, $lastColumn)).Merge()
    $sheet.Cells.Item(1, 1).Font.Bold = $true
    $sheet.Cells.Item(1, 1).Font.Size = 13
    $sheet.Rows.Item(1).RowHeight = 26

    $sheet.Cells.Item(2, 1).Value2 = $note
    $sheet.Range($sheet.Cells.Item(2, 1), $sheet.Cells.Item(2, $lastColumn)).Merge()
    $sheet.Cells.Item(2, 1).Font.Size = 10
    $sheet.Cells.Item(2, 1).Font.Color = 8421504
    $sheet.Rows.Item(2).RowHeight = 20

    $headers = @('测试分类', '测试内容', '测试结果')
    for ($c = 0; $c -lt $headers.Count; $c++) {
        $sheet.Cells.Item(3, $c + 1).Value2 = $headers[$c]
    }

    $headerRange = $sheet.Range($sheet.Cells.Item(3, 1), $sheet.Cells.Item(3, $lastColumn))
    $headerRange.Font.Bold = $true
    $headerRange.Interior.Color = 14277081

    for ($r = 0; $r -lt $rows.Count; $r++) {
        $sheet.Cells.Item($r + 4, 1).Value2 = $rows[$r][0]
        $sheet.Cells.Item($r + 4, 2).Value2 = $rows[$r][1]
    }

    $body = $sheet.Range($sheet.Cells.Item(3, 1), $sheet.Cells.Item($lastRow, $lastColumn))
    $body.Borders.LineStyle = 1
    $body.Borders.Color = 12632256
    $body.WrapText = $true
    $body.VerticalAlignment = -4160

    $sheet.Columns.Item(1).ColumnWidth = 14
    $sheet.Columns.Item(2).ColumnWidth = 72
    $sheet.Columns.Item(3).ColumnWidth = 26

    for ($r = 4; $r -le $lastRow; $r++) {
        $sheet.Rows.Item($r).RowHeight = 30
    }

    $sheet.Activate()
    $excel.ActiveWindow.SplitRow = 3
    $excel.ActiveWindow.FreezePanes = $true
    $sheet.Range($sheet.Cells.Item(3, 1), $sheet.Cells.Item($lastRow, $lastColumn)).AutoFilter() | Out-Null
    $sheet.Range('A4').Select() | Out-Null

    $workbook.SaveAs($outputPath, 51)
    $workbook.Close($false)
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}

Write-Output "已生成：$outputPath（$($rows.Count) 条，结果栏留空）"