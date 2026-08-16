# AM-IMG full build + test pipeline
# Builds AM-IMG.exe and AM-IMG-Setup.exe using only the C# compiler that ships
# with Windows (.NET Framework 4.x) — no SDK or Visual Studio required.
$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$src   = Join-Path $root 'src'
$build = Join-Path $root 'build'
$dist  = Join-Path $root 'dist'
$csc   = "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
New-Item -ItemType Directory -Force $build | Out-Null
New-Item -ItemType Directory -Force $dist  | Out-Null

function Invoke-Csc([string[]]$cscArgs) {
    & $csc /nologo @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc failed: $($cscArgs -join ' ')" }
}

Write-Host '[1/7] Engine test harness'
Invoke-Csc @('/target:exe', "/out:$build\amimg-test.exe", '/r:System.Management.dll',
    "$src\Engine.cs", "$src\DriveList.cs", "$src\NativeMethods.cs", "$src\TestMain.cs")
& "$build\amimg-test.exe"
if ($LASTEXITCODE -ne 0) { throw 'ENGINE TESTS FAILED' }

Write-Host '[2/7] AM-IMG.exe (release)'
Invoke-Csc @('/target:winexe', '/platform:anycpu', '/optimize+', "/out:$dist\AM-IMG.exe",
    "/win32manifest:$src\app.manifest", "/win32icon:$src\AM-IMG.ico",
    '/r:System.Management.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    "$src\Engine.cs", "$src\DriveList.cs", "$src\NativeMethods.cs",
    "$src\MainForm.cs", "$src\Program.cs")

Write-Host '[3/7] App UI smoke build + render'
Invoke-Csc @('/target:exe', "/out:$build\amimg-smoke.exe",
    '/r:System.Management.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    "$src\Engine.cs", "$src\DriveList.cs", "$src\NativeMethods.cs",
    "$src\MainForm.cs", "$src\Program.cs")
& "$build\amimg-smoke.exe" --smoke "$build\ui-app.png"
if ($LASTEXITCODE -ne 0) { throw 'APP UI SMOKE FAILED' }

$logoPng = Join-Path $root 'design\logo\badge-256.png'
$logoRes = @()
if (Test-Path $logoPng) { $logoRes = @("/res:$logoPng,logo.png") }

Write-Host '[4/7] AM-IMG-Setup.exe (release, payload embedded)'
Invoke-Csc (@('/target:winexe', '/platform:anycpu', '/optimize+', "/out:$dist\AM-IMG-Setup.exe",
    "/win32manifest:$src\app.manifest", "/win32icon:$src\AM-IMG.ico",
    "/res:$dist\AM-IMG.exe,AM-IMG.exe") + $logoRes + @(
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:Microsoft.CSharp.dll',
    "$src\Setup.cs"))

Write-Host '[5/7] Setup test build (no elevation needed)'
Invoke-Csc (@('/target:exe', "/out:$build\setup-test.exe",
    "/res:$dist\AM-IMG.exe,AM-IMG.exe") + $logoRes + @(
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:Microsoft.CSharp.dll',
    "$src\Setup.cs"))

Write-Host '[6/7] Installer round-trip test (install + verify + uninstall)'
$testRoot = Join-Path $env:TEMP ('amimg-setup-test-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
& "$build\setup-test.exe" /testinstall $testRoot
$code = $LASTEXITCODE
if (Test-Path "$testRoot\setup-test.log") { Get-Content "$testRoot\setup-test.log" | ForEach-Object { "    $_" } }
Remove-Item -Recurse -Force $testRoot -ErrorAction SilentlyContinue
if ($code -ne 0) { throw "SETUP ROUND-TRIP TEST FAILED (exit $code)" }

Write-Host '[7/7] Setup UI smoke + render'
& "$build\setup-test.exe" /smoke "$build\ui-setup.png"
if ($LASTEXITCODE -ne 0) { throw 'SETUP UI SMOKE FAILED' }

Write-Host ''
Write-Host 'BUILD OK:'
Get-ChildItem $dist | ForEach-Object { Write-Host ("    {0}  {1:N0} KB" -f $_.Name, ($_.Length/1KB)) }
