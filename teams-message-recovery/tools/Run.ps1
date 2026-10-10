# Teams Message History - launcher for Windows PowerShell 5.1.
# Compiles the C# sources in ..\src (including ..\src\brotli, the Google Brotli decoder) at start-up (Add-Type, C# 5 / .NET Framework) and runs them.
# No installation, no administrator rights, no network access.
#
# This script deliberately has no param() block: every argument (-Link, -Source, -Out, -InPlace, ...)
# is handed to the program through $args untouched. A param() block would turn the script into an
# advanced script whose common parameters (-OutVariable, -OutBuffer, ...) swallow -Out.

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# The console must show Japanese; pasted links are read as UTF-8 as well.
try { [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false) } catch { }
try { if (-not [Console]::IsInputRedirected) { [Console]::InputEncoding = New-Object Text.UTF8Encoding($false) } } catch { }
if ([Console]::IsInputRedirected) {
    [Console]::SetIn((New-Object IO.StreamReader([Console]::OpenStandardInput(), (New-Object Text.UTF8Encoding($false)))))
}

if ($PSVersionTable.PSEdition -eq 'Core') {
    [Console]::Error.WriteLine('Windows PowerShell 5.1 (powershell.exe) で実行してください。')
    exit 3
}

$baseDirectory = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $baseDirectory 'src'

$compileWatch = [Diagnostics.Stopwatch]::StartNew()
try {
    $sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File -Recurse | Sort-Object -Property FullName)
    if ($sourceFiles.Count -eq 0) { throw "C# のソースが見つかりません: $sourceDirectory" }

    # Add-Type -TypeDefinition takes one compilation unit, so the per-file using directives
    # are collected once at the top and the bodies are concatenated.
    $combined = ($sourceFiles | ForEach-Object {
        [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
    }) -join [Environment]::NewLine
    $usingPattern = '(?m)^\s*using\s+[A-Za-z_][A-Za-z0-9_.]*\s*;\s*$'
    $usings = [regex]::Matches($combined, $usingPattern) |
        ForEach-Object { $_.Value.Trim() } |
        Sort-Object -Unique
    $body = [regex]::Replace($combined, $usingPattern, '')
    $source = ($usings -join [Environment]::NewLine) + [Environment]::NewLine + [Environment]::NewLine + $body

    Add-Type -TypeDefinition $source -Language CSharp -ReferencedAssemblies @('System.dll', 'System.Core.dll')
}
catch {
    [Console]::Error.WriteLine('起動時のコンパイルに失敗しました: ' + $_.Exception.Message)
    if ($_.Exception.PSObject.Properties['LoaderExceptions']) { $_.Exception.LoaderExceptions | ForEach-Object { [Console]::Error.WriteLine($_.Message) } }
    exit 3
}

# how long the start-up compilation took: the program prints it with its own stage times
$env:TMH_COMPILE_SECONDS = $compileWatch.Elapsed.TotalSeconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)

[string[]]$programArguments = @()
if ($null -ne $args -and $args.Count -gt 0) { $programArguments = [string[]]($args | ForEach-Object { [string]$_ }) }
$exitCode = [TeamsMessageHistory.Program]::Run($programArguments)
exit $exitCode
