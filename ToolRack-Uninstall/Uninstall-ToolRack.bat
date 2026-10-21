@echo off
setlocal
set "TOOLRACK_REMOVER_FILE=%~f0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s=[IO.File]::ReadAllText($env:TOOLRACK_REMOVER_FILE,[Text.Encoding]::UTF8); & ([scriptblock]::Create(($s -split '(?m)^:TOOLRACK_POWERSHELL\r?$',2)[1]))"
set "TOOLRACK_REMOVER_RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %TOOLRACK_REMOVER_RESULT%
:TOOLRACK_POWERSHELL
$ErrorActionPreference = 'Stop'

function Get-ToolRackRootsFromCommand([string]$Command) {
    foreach ($match in [regex]::Matches($Command, '"([^"]+)"|(\S+)')) {
        $token = $match.Value.Trim('"')
        if ($token -match '^(.*)\\common\\(?:silent\.vbs|launch\.ps1|host\.ps1|start-host\.vbs)$') {
            $matches[1]
        }
    }
}

function Read-ToolRackMenuCommands([Microsoft.Win32.RegistryKey]$Key) {
    if ($Key.GetValue('') -is [string]) {
        Get-ToolRackRootsFromCommand ([string]$Key.GetValue(''))
    }
    foreach ($name in $Key.GetSubKeyNames()) {
        $child = $Key.OpenSubKey($name)
        try { Read-ToolRackMenuCommands $child } finally { $child.Dispose() }
    }
}

function Test-ToolRackRoot([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) { return $false }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { return $false }
    $protected = @($env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, $env:SystemRoot,
        [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('MyDocuments'),
        [IO.Path]::GetPathRoot($full))
    if (@($protected | Where-Object { $_ -and $_.TrimEnd('\') -eq $full }).Count) { return $false }
    if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
    $installer = Join-Path $full 'common\install.ps1'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { return $false }
    $code = [IO.File]::ReadAllText($installer)
    return ($code -match '\$script:KeyName\s*=\s*"ToolRack"' -and
        (Test-Path -LiteralPath (Join-Path $full 'common\launch.ps1') -PathType Leaf))
}

function Remove-ToolRackDirectory([string]$Path) {
    # Do not follow junctions or symbolic links into unrelated directories.
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($item.FullName) }
        else { [IO.File]::Delete($item.FullName) }
        return
    }
    if ($item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) {
            Remove-ToolRackDirectory $child.FullName
        }
        [IO.Directory]::Delete($Path)
    } else {
        if ($item.IsReadOnly) { $item.IsReadOnly = $false }
        [IO.File]::Delete($Path)
    }
}

function Invoke-ToolRackRemoval {
    param(
        [string]$LocalRoot = (Join-Path $env:LOCALAPPDATA 'ToolRack'),
        [string]$ClassesKey = 'Software\Classes',
        [string]$RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
    )
    $roots = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $menuPaths = New-Object 'System.Collections.Generic.List[string]'
    $states = @()
    $registry = [Microsoft.Win32.Registry]::CurrentUser
    $localExists = Test-Path -LiteralPath $LocalRoot -PathType Container
    if ($localExists -and ((Get-Item -LiteralPath $LocalRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "常駐用フォルダーがリンクになっています。変更せずに終了します: $LocalRoot"
    }
    $run = $registry.OpenSubKey($RunKey)
    try {
        $runExists = $null -ne $run -and $run.GetValueNames() -contains 'ToolRackHost'
        if ($runExists) {
            foreach ($root in @(Get-ToolRackRootsFromCommand ([string]$run.GetValue('ToolRackHost')))) {
                [void]$roots.Add($root)
            }
        }
    } finally { if ($run) { $run.Dispose() } }
    foreach ($suffix in @('*\shell', 'Directory\shell', 'Directory\Background\shell')) {
        $base = $ClassesKey + '\' + $suffix
        $key = $registry.OpenSubKey($base)
        try {
            if ($null -eq $key) { continue }
            foreach ($name in $key.GetSubKeyNames()) {
                if ($name -ne 'ToolRack' -and -not $name.StartsWith('ToolRack.', [StringComparison]::OrdinalIgnoreCase)) { continue }
                $path = $base + '\' + $name
                $menuPaths.Add($path)
                $child = $registry.OpenSubKey($path)
                try {
                    foreach ($root in @(Read-ToolRackMenuCommands $child)) { [void]$roots.Add($root) }
                } finally { $child.Dispose() }
            }
        } finally { if ($key) { $key.Dispose() } }
    }
    $stateParent = Join-Path $LocalRoot 'state'
    if (Test-Path -LiteralPath $stateParent -PathType Container) {
        $states = @(Get-ChildItem -LiteralPath $stateParent -Directory |
            Where-Object { $_.Name -cmatch '^[a-f0-9]{32}$' })
        foreach ($state in $states) {
            $rootFile = Join-Path $state.FullName 'root.txt'
            if (Test-Path -LiteralPath $rootFile -PathType Leaf) {
                [void]$roots.Add([IO.File]::ReadAllText($rootFile).Trim())
            }
        }
    }
    if (-not $runExists -and -not $localExists -and $menuPaths.Count -eq 0) {
        Write-Host 'このユーザーには Tool Rack のインストール記録がありません。変更していません。'
        return
    }
    $validRoots = @()
    foreach ($root in $roots) {
        if (Test-ToolRackRoot $root) {
            $validRoots += [IO.Path]::GetFullPath($root).TrimEnd('\')
        } elseif (Test-Path -LiteralPath $root) {
            throw "本体として確認できないフォルダーが登録されています。削除せずに終了します: $root"
        }
    }
    $validRoots = @($validRoots | Sort-Object -Unique)
    Write-Host 'Tool Rack 全体を削除します。保存済みの画像・文書は残します。'
    foreach ($root in $validRoots) { Write-Host "本体: $root" }

    # Select only this user's processes with an exact Tool Rack script argument.
    $entryPaths = @(Join-Path $LocalRoot 'bootstrap\v1\host-bootstrap.ps1')
    foreach ($root in $validRoots) {
        foreach ($name in @('host.ps1', 'launch.ps1', 'silent.vbs', 'start-host.vbs')) {
            $entryPaths += Join-Path $root ('common\' + $name)
        }
        $tools = Join-Path $root 'tool'
        if (Test-Path -LiteralPath $tools -PathType Container) {
            foreach ($directory in @(Get-ChildItem -LiteralPath $tools -Directory)) {
                $manifest = Join-Path $directory.FullName 'tool.json'
                if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { continue }
                $data = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json
                if ($data.run.entry) {
                    $entry = [IO.Path]::GetFullPath((Join-Path $directory.FullName ([string]$data.run.entry)))
                    if ($entry.StartsWith($directory.FullName.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                        $entryPaths += $entry
                    }
                }
            }
        }
    }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe' OR Name='wscript.exe' OR Name='cscript.exe' OR Name='python.exe' OR Name='pythonw.exe'")
    $owned = @()
    foreach ($process in $processes) {
        if ($process.ProcessId -eq $PID) { continue }
        $argumentTokens = @([regex]::Matches([string]$process.CommandLine, '"([^"]+)"|(\S+)') |
            ForEach-Object { $_.Value.Trim('"') })
        if (@($entryPaths | Where-Object { $argumentTokens -contains $_ }).Count -eq 0) { continue }
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
        if ($owner.ReturnValue -ne 0) { throw "Tool Rack プロセスの所有者を確認できません: $($process.ProcessId)" }
        if ($owner.Sid -eq $sid) { $owned += $process }
    }
    foreach ($state in $states) {
        $rootFile = Join-Path $state.FullName 'root.txt'
        if (-not (Test-Path -LiteralPath $rootFile -PathType Leaf)) { continue }
        $root = [IO.File]::ReadAllText($rootFile).Trim()
        $control = Join-Path $root 'common\host-control.ps1'
        if ($validRoots -contains $root -and (Test-Path -LiteralPath $control -PathType Leaf) -and
            @($owned | Where-Object { ([string]$_.CommandLine).Contains($state.FullName) }).Count) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $control -Shutdown -StateRoot $state.FullName
        }
    }
    foreach ($process in $owned) {
        $live = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $live) { continue }
        if ($live.WaitForExit(2000)) { continue }
        $current = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $process.ProcessId)
        if ($current -and $current.CreationDate -eq $process.CreationDate -and
            $current.CommandLine -eq $process.CommandLine) {
            Stop-Process -Id $process.ProcessId -Force
            $live.WaitForExit()
            Write-Host "Tool Rack のプロセスを終了: $($process.ProcessId)"
        }
    }

    $run = $registry.OpenSubKey($RunKey, $true)
    try { if ($run) { $run.DeleteValue('ToolRackHost', $false) } }
    finally { if ($run) { $run.Dispose() } }
    foreach ($path in $menuPaths) { $registry.DeleteSubKeyTree($path, $false) }
    if ($localExists) { Remove-ToolRackDirectory $LocalRoot }

    # These are the application's own paths. Output and unknown user files remain.
    $programPaths = @('common', 'tool', 'test', 'docs', 'install.bat', 'uninstall.bat',
        'bindings.json', 'menu.json', 'README.md', '.gitignore', '.gitattributes',
        'TODO-MANUAL-TEST.md', 'TODO-GLOBAL-LAUNCH-MANUAL-TEST.md')
    foreach ($root in $validRoots) {
        foreach ($name in $programPaths) {
            $path = Join-Path $root $name
            if (Test-Path -LiteralPath $path) { Remove-ToolRackDirectory $path }
        }
        if (@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) {
            [IO.Directory]::Delete($root)
        } else { Write-Host "保存済みのデータなどは残しました: $root" }
    }
    Write-Host ''
    Write-Host '完了しました。Tool Rack の自動起動・右クリックメニュー・常駐処理・本体を削除しました。' -ForegroundColor Green
    if ($validRoots.Count -eq 0) {
        Write-Host '登録された場所に本体は見つからなかったため、残っていた登録と常駐用データを削除しました。'
    }
}

try {
    Invoke-ToolRackRemoval
    exit 0
} catch {
    Write-Host ''
    Write-Host ('削除を完了できませんでした: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'この画面を閉じずに、表示された内容をイリスに伝えてください。'
    exit 1
}
