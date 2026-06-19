param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "dev",
    [string]$OutputRoot = "",
    [string]$DependencyArchiveRoot = "",
    [switch]$SkipBundledDependencies,
    [switch]$Restore,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release"
}

$packageFlavor = if ($FrameworkDependent) { "framework-dependent" } else { "portable" }
$packageName = "VComTunnel-$Version-$Runtime-$packageFlavor"
$packageRoot = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"
$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

if (-not $Restore) {
    Write-Host "Using --no-restore. Ensure packages were restored for runtime '$Runtime', or pass -Restore."
}

if (Test-Path $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

$publishProjects = @(
    "src\VComTunnel.Gui\VComTunnel.Gui.csproj",
    "src\VComTunnel.Service\VComTunnel.Service.csproj",
    "src\VComTunnel.Cli\VComTunnel.Cli.csproj"
)

foreach ($project in $publishProjects) {
    $publishArgs = @(
        "publish",
        (Join-Path $repoRoot $project),
        "-c",
        $Configuration,
        "-r",
        $Runtime,
        "--self-contained",
        $selfContained,
        "-o",
        $packageRoot
    )
    if (-not $Restore) {
        $publishArgs += "--no-restore"
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $project with exit code $LASTEXITCODE."
    }
}

$releaseDocs = @(
    "LICENSE",
    "README.md",
    "README.zh-CN.md",
    "SECURITY.md"
)

foreach ($doc in $releaseDocs) {
    $sourceDoc = Join-Path $repoRoot $doc
    if (Test-Path $sourceDoc) {
        Copy-Item -LiteralPath $sourceDoc -Destination (Join-Path $packageRoot $doc) -Force
    }
}

$launcher = @"
@echo off
setlocal
"%~dp0VComTunnel.Cli.exe" %*
"@
Set-Content -LiteralPath (Join-Path $packageRoot "vcomtunnelctl.cmd") -Value $launcher -Encoding ASCII

$startGui = @"
@echo off
setlocal
start "" "%~dp0VComTunnel.Gui.exe"
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Start-VComTunnel.cmd") -Value $startGui -Encoding ASCII

$startPortable = @"
@echo off
setlocal
set "VCOMTUNNEL_HOME=%~dp0data"
if not exist "%VCOMTUNNEL_HOME%" mkdir "%VCOMTUNNEL_HOME%"
start "" "%~dp0VComTunnel.Gui.exe"
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Start-VComTunnel-Portable.cmd") -Value $startPortable -Encoding ASCII

$setupDependencies = @"
@echo off
setlocal
set "VCOMTUNNEL_HOME=%~dp0data"
if not exist "%VCOMTUNNEL_HOME%" mkdir "%VCOMTUNNEL_HOME%"
"%~dp0VComTunnel.Cli.exe" deps install
set "install_exit=%ERRORLEVEL%"
echo.
if not "%install_exit%"=="0" (
    echo Dependency preparation reported errors. Review the output above.
    pause
    exit /b %install_exit%
)
echo com0com is a Windows driver and may require UAC approval.
choice /C YN /M "Launch the com0com driver installer now"
if errorlevel 2 goto done
"%~dp0VComTunnel.Cli.exe" deps launch-com0com
:done
echo.
pause
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Setup-Dependencies-Portable.cmd") -Value $setupDependencies -Encoding ASCII

$startConsoleService = @"
@echo off
setlocal
set "VCOMTUNNEL_HOME=%~dp0data"
if not exist "%VCOMTUNNEL_HOME%" mkdir "%VCOMTUNNEL_HOME%"
"%~dp0VComTunnel.Service.exe" --console
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Start-Service-Console-Portable.cmd") -Value $startConsoleService -Encoding ASCII

$installServiceCmd = @"
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Windows-Service.ps1"
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Install-Windows-Service.cmd") -Value $installServiceCmd -Encoding ASCII

$uninstallServiceCmd = @"
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Windows-Service.ps1"
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Uninstall-Windows-Service.cmd") -Value $uninstallServiceCmd -Encoding ASCII

$installServicePs1 = @'
$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $script = $PSCommandPath.Replace('"', '\"')
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$script`"" -Verb RunAs
    exit
}

$root = Split-Path -Parent $PSCommandPath
$cli = Join-Path $root "VComTunnel.Cli.exe"
$service = Join-Path $root "VComTunnel.Service.exe"

& $cli service install $service
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $cli service start
exit $LASTEXITCODE
'@
Set-Content -LiteralPath (Join-Path $packageRoot "Install-Windows-Service.ps1") -Value $installServicePs1 -Encoding UTF8

$uninstallServicePs1 = @'
$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $script = $PSCommandPath.Replace('"', '\"')
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$script`"" -Verb RunAs
    exit
}

$root = Split-Path -Parent $PSCommandPath
$cli = Join-Path $root "VComTunnel.Cli.exe"

& $cli service stop
& $cli service uninstall
exit $LASTEXITCODE
'@
Set-Content -LiteralPath (Join-Path $packageRoot "Uninstall-Windows-Service.ps1") -Value $uninstallServicePs1 -Encoding UTF8

$dependencyArchives = @(
    @{
        Name = "hub4com-2.1.0.0-386.zip"
        Url = "https://sourceforge.net/projects/com0com/files/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip/download"
        Description = "hub4com 2.1.0.0 RFC2217 bridge tools"
    },
    @{
        Name = "com0com-3.0.0.0-i386-and-x64-signed.zip"
        Url = "https://sourceforge.net/projects/com0com/files/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip/download"
        Description = "com0com 3.0.0.0 signed installer package"
    }
)

if (-not $SkipBundledDependencies) {
    $dependenciesDir = Join-Path $packageRoot "dependencies"
    New-Item -ItemType Directory -Force -Path $dependenciesDir | Out-Null

    foreach ($archive in $dependencyArchives) {
        $target = Join-Path $dependenciesDir $archive.Name
        $source = if ([string]::IsNullOrWhiteSpace($DependencyArchiveRoot)) {
            $null
        } else {
            Join-Path $DependencyArchiveRoot $archive.Name
        }

        if ($source -and (Test-Path $source)) {
            Copy-Item -LiteralPath $source -Destination $target -Force
        } else {
            Invoke-WebRequest -Uri $archive.Url -OutFile $target
        }
    }
}

$noticeLines = @(
    "VComTunnel third-party dependency archives",
    "",
    "This release package may include unmodified upstream archives used by VComTunnel dependency setup.",
    "hub4com is extracted into the VComTunnel tools cache at first setup.",
    "com0com is a Windows driver package and still requires an interactive elevated installer run.",
    ""
)

foreach ($archive in $dependencyArchives) {
    $noticeLines += "- $($archive.Description)"
    $noticeLines += "  Archive: $($archive.Name)"
    $noticeLines += "  Source: $($archive.Url)"
}

Set-Content -LiteralPath (Join-Path $packageRoot "THIRD-PARTY-NOTICES.txt") -Value $noticeLines -Encoding UTF8

$runtimeLine = if ($FrameworkDependent) {
    "This is a framework-dependent package. Install the .NET 8 Desktop Runtime and ASP.NET Core Runtime before running it."
} else {
    "This is a self-contained package. It does not require a separate .NET runtime installation."
}

$firstReadme = @(
    "VComTunnel release package",
    "",
    $runtimeLine,
    "",
    "Portable use:",
    "1. Extract this folder to a writable location.",
    "2. Run Start-VComTunnel-Portable.cmd.",
    "3. Use Setup deps in the GUI, or run Setup-Dependencies-Portable.cmd.",
    "4. Approve the com0com driver installer when Windows asks. The app files are portable, but the virtual COM driver is still a system-level install.",
    "",
    "Installed service use:",
    "1. Run Install-Windows-Service.cmd and approve UAC.",
    "2. Start VComTunnel.Gui.exe or Start-VComTunnel.cmd to manage mappings.",
    "3. Run Uninstall-Windows-Service.cmd to remove the Windows service.",
    "",
    "Safety notes:",
    "- The stable release path is com0comHub4com.",
    "- The KMDF backend is experimental and should not be treated as a production signed-driver release.",
    "- Keep the local API on 127.0.0.1 and test DTR/RTS/BREAK behavior on safe hardware first."
)
Set-Content -LiteralPath (Join-Path $packageRoot "README-FIRST.txt") -Value $firstReadme -Encoding UTF8

$firstReadmeZh = @(
    "VComTunnel 发布包",
    "",
    $(if ($FrameworkDependent) { "这是 framework-dependent 包，运行前需要安装 .NET 8 Desktop Runtime 和 ASP.NET Core Runtime。" } else { "这是 self-contained 包，不需要用户单独安装 .NET 运行时。" }),
    "",
    "便携使用：",
    "1. 解压到一个当前用户可写的目录。",
    "2. 运行 Start-VComTunnel-Portable.cmd。",
    "3. 在 GUI 里点击 Setup deps，或运行 Setup-Dependencies-Portable.cmd。",
    "4. Windows 请求安装 com0com 驱动时需要用户确认。应用、配置、日志和工具缓存可以便携，但虚拟串口驱动仍然是系统级安装。",
    "",
    "安装为后台服务：",
    "1. 运行 Install-Windows-Service.cmd 并确认 UAC。",
    "2. 用 VComTunnel.Gui.exe 或 Start-VComTunnel.cmd 管理映射。",
    "3. 运行 Uninstall-Windows-Service.cmd 删除 Windows Service。",
    "",
    "安全边界：",
    "- 稳定发布路径是 com0comHub4com。",
    "- KMDF 后端仍是实验路径，不能当作正式签名驱动发布。",
    "- 本机 API 只应使用 127.0.0.1，DTR/RTS/BREAK 等控制线行为先在安全硬件上验证。"
)
Set-Content -LiteralPath (Join-Path $packageRoot "README-FIRST.zh-CN.txt") -Value $firstReadmeZh -Encoding UTF8

$packageRootFull = (Resolve-Path -LiteralPath $packageRoot).Path
$hashLines = Get-ChildItem -LiteralPath $packageRootFull -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($packageRootFull.Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
        "$($hash.Hash)  $relative"
    }
Set-Content -LiteralPath (Join-Path $packageRoot "SHA256SUMS.txt") -Value $hashLines -Encoding ASCII

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force

Write-Host "Release package: $packageRoot"
Write-Host "Archive: $zipPath"
