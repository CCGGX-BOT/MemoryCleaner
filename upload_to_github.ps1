# ============================================================
# 一键上传 MemoryCleaner 到 GitHub（纯 REST API，无需安装 git）
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File upload_to_github.ps1 `
#       -Token "ghp_你的令牌" -Owner "你的GitHub用户名" [-Repo MemoryCleaner] [-Visibility public]
#
# Token 创建方法（github.com → Settings → Developer settings → Personal access tokens
#   → Tokens (classic) → Generate new token，勾选 repo 权限，或使用 fine-grained token
#   并授予该仓库 Contents: Read/Write）
# 注意：token 只建议本次使用，用完可在 GitHub 上删除。
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$Token,
    [Parameter(Mandatory = $true)][string]$Owner,
    [string]$Repo = "MemoryCleaner",
    [ValidateSet("public", "private")][string]$Visibility = "public",
    [string]$ProjectDir = "",
    [string]$CommitMessage = "Initial release: MemoryCleaner v1.0 (memory release tool)"
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrEmpty($ProjectDir)) { $ProjectDir = $PSScriptRoot }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$api = "https://api.github.com"
$headers = @{ Authorization = "Bearer $Token"; "User-Agent" = "MemoryCleaner-Uploader" }
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Invoke-GH {
    param([string]$Method, [string]$Uri, [object]$Body = $null, [switch]$AllowError)
    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers; ContentType = "application/json; charset=utf-8" }
    if ($null -ne $Body) {
        $json = ($Body | ConvertTo-Json -Depth 10 -Compress)
        # 关键：显式转成 UTF-8 字节，避免 PowerShell 5.1 用系统 ANSI 编码发送导致中文乱码
        $params.Body = $utf8.GetBytes($json)
    }
    try {
        return Invoke-RestMethod @params
    } catch {
        if ($AllowError) { return $null }
        $code = ""
        $detail = ""
        try {
            $resp = $_.Exception.Response
            if ($resp) {
                $code = [int]$resp.StatusCode
                $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
                $detail = $sr.ReadToEnd()
            }
        } catch { }
        Write-Error ("API 请求失败 [{0}] {1}  HTTP {2}: {3}" -f $Method, $Uri, $code, $detail)
        throw
    }
}

Write-Host "==> 1/5 校验 Token..." -ForegroundColor Cyan
$me = Invoke-GH "GET" "$api/user"
Write-Host ("    Token 有效，登录用户: " + $me.login) -ForegroundColor Green
if ($me.login -ne $Owner) {
    Write-Warning ("    注意：Token 属于 " + $me.login + "，将以 " + $me.login + " 的名义操作。")
    $Owner = $me.login
}

Write-Host "==> 2/5 检查/创建仓库 $Owner/$Repo ($Visibility)..." -ForegroundColor Cyan
$existingRepo = Invoke-GH "GET" "$api/repos/$Owner/$Repo" -AllowError
if ($null -eq $existingRepo) {
    Invoke-GH "POST" "$api/user/repos" @{
        name = $Repo; private = ($Visibility -eq "private"); auto_init = $false
        description = "一键释放 Windows 内存的小工具：EmptyWorkingSet 清空进程工作集，支持图形界面 / 自动清理 / 命令行静默模式"
    }
    Write-Host "    仓库已创建" -ForegroundColor Green
} else {
    Write-Host "    仓库已存在，继续使用" -ForegroundColor Yellow
}
# 修正仓库描述（UTF-8）
Invoke-GH "PATCH" "$api/repos/$Owner/$Repo" @{
    description = "一键释放 Windows 内存的小工具：EmptyWorkingSet 清空进程工作集，支持图形界面 / 自动清理 / 命令行静默模式"
} | Out-Null

Write-Host "==> 3/5 清理测试残留..." -ForegroundColor Cyan
$test = Invoke-GH "GET" "$api/repos/$Owner/$Repo/contents/test.txt" -AllowError
if ($null -ne $test) {
    Invoke-GH "DELETE" "$api/repos/$Owner/$Repo/contents/test.txt" @{ message = "remove test file"; sha = $test.sha }
    Write-Host "    已删除残留 test.txt" -ForegroundColor Yellow
}

Write-Host "==> 4/5 上传文件..." -ForegroundColor Cyan
$files = @(
    "README.md",
    "LICENSE",
    ".gitignore",
    "build.ps1",
    "upload_to_github.ps1",
    "src\MemoryCleaner.cs",
    "src\app.ico",
    "src\make_icon.ps1",
    "dist\MemoryCleaner.exe"
)
$branch = "main"
foreach ($f in $files) {
    $full = Join-Path $ProjectDir $f
    if (-not (Test-Path $full)) { Write-Warning ("    跳过（不存在）: " + $f); continue }
    $b64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($full))
    $path = ($f -replace "\\", "/")
    try {
        $r = Invoke-GH "PUT" "$api/repos/$Owner/$Repo/contents/$path" @{
            message = $CommitMessage; content = $b64; branch = $branch
        }
        $sizeKB = [math]::Round((Get-Item $full).Length / 1KB, 1)
        Write-Host ("    已上传: " + $f + "  (" + $sizeKB + " KB)") -ForegroundColor Green
    } catch {
        # 文件已存在时改为更新（需要 sha）
        $existing = Invoke-GH "GET" "$api/repos/$Owner/$Repo/contents/$path" -AllowError
        if ($null -ne $existing) {
            $r = Invoke-GH "PUT" "$api/repos/$Owner/$Repo/contents/$path" @{
                message = $CommitMessage; content = $b64; branch = $branch; sha = $existing.sha
            }
            Write-Host ("    已更新: " + $f) -ForegroundColor Green
        } else {
            throw
        }
    }
}

Write-Host "==> 5/5 完成！" -ForegroundColor Cyan
Write-Host ("    仓库地址: https://github.com/" + $Owner + "/" + $Repo) -ForegroundColor Green
Write-Host ("    克隆地址: https://github.com/" + $Owner + "/" + $Repo + ".git") -ForegroundColor Green
