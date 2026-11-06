# 把上一步插进去的 HeaderIcon 块按父卡片缩进对齐（插入时缩进算错了，全是顶格）。
$root = 'E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src\Desktop\BiliCopilot.UI\Controls\Settings'

foreach ($file in (Get-ChildItem -Path $root -Filter *.xaml)) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $changed = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -ne '<base:SettingsCard.HeaderIcon>') { continue }

        # 往上找所属卡片，取它的缩进
        $cardIndent = ''
        for ($j = $i - 1; $j -ge 0; $j--) {
            $t = $lines[$j].TrimStart()
            if ($t -eq '<base:SettingsCard' -or $t -eq '<base:SettingsCard>' -or $t.StartsWith('<base:SettingsCard ')) {
                $cardIndent = $lines[$j].Substring(0, $lines[$j].Length - $lines[$j].TrimStart().Length)
                break
            }
        }

        $indent = if ($cardIndent -match "`t") { $cardIndent + "`t" } else { $cardIndent + '    ' }
        $lines[$i] = $indent + '<base:SettingsCard.HeaderIcon>'
        if ($i + 1 -lt $lines.Count) { $lines[$i + 1] = $indent + '    ' + $lines[$i + 1].TrimStart() }
        if ($i + 2 -lt $lines.Count) { $lines[$i + 2] = $indent + '</base:SettingsCard.HeaderIcon>' }
        $changed = $true
    }

    if ($changed) {
        [System.IO.File]::WriteAllLines($file.FullName, $lines)
        Write-Output ($file.Name + " 缩进已对齐")
    }
}

