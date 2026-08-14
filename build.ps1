# Build MemoryCleaner.exe (standalone, no runtime install needed)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    Write-Error "csc.exe compiler not found"
    exit 1
}
& $csc /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /codepage:65001 `
    /win32icon:"$root\src\app.ico" `
    /out:"$root\dist\MemoryCleaner.exe" `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    "$root\src\MemoryCleaner.cs"
if ($LASTEXITCODE -eq 0) {
    $f = Get-Item "$root\dist\MemoryCleaner.exe"
    Write-Output ("Build OK: {0} ({1:N0} KB)" -f $f.FullName, ($f.Length / 1KB))
} else {
    Write-Error "Build failed, exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
