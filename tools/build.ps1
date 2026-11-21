#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MasterPath,
    [string]$PluginProject = (Join-Path $PSScriptRoot '../../URA-Plugins/PioneerAiPlugin/PioneerAiPlugin.csproj')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$master = (Resolve-Path -LiteralPath $MasterPath).Path
$pluginProjectPath = (Resolve-Path -LiteralPath $PluginProject).Path
$pluginDirectory = Split-Path -Parent $pluginProjectPath
if (-not (Test-Path -LiteralPath (Join-Path $pluginDirectory 'deps/PioneerScenarioAnalyzer/PioneerScenarioAnalyzer/PioneerScenarioAnalyzer.csproj') -PathType Leaf)) {
    throw "请在 $pluginDirectory 初始化 deps/PioneerScenarioAnalyzer 子模块。"
}

$obj = (New-Item -ItemType Directory -Path (Join-Path $repo 'obj') -Force).FullName
$stage = (New-Item -ItemType Directory -Path (Join-Path $obj "pioneer-build-$([guid]::NewGuid())")).FullName
Push-Location -LiteralPath $repo
try {
    # Isolate the plugin project from the engine's central package and build settings.
    Set-Content -LiteralPath (Join-Path $stage 'Directory.Build.props') -Value '<Project />'
    Set-Content -LiteralPath (Join-Path $stage 'Directory.Packages.props') -Value '<Project />'
    $stagedPlugin = Join-Path $stage 'PioneerAiPlugin'
    $PSNativeCommandUseErrorActionPreference = $false
    robocopy $pluginDirectory $stagedPlugin /E /XJ /R:0 /W:0 /NFL /NDL /NJH /NJS /NP `
        /XD .git bin obj artifacts /XF .git pioneer_rules.br
    if ($LASTEXITCODE -ge 8) { throw "复制插件源码失败，Robocopy 退出码 $LASTEXITCODE。" }
    $PSNativeCommandUseErrorActionPreference = $true

    $project = Join-Path $stagedPlugin (Split-Path -Leaf $pluginProjectPath)
    $resources = Join-Path $stagedPlugin 'Resources'
    $generator = 'tools/UmaAiSharp.Pioneer.DataGenerator/UmaAiSharp.Pioneer.DataGenerator.csproj'
    $generatorOutput = Join-Path $stage 'generator'
    dotnet restore $generator --locked-mode -m:1 -nodeReuse:false
    dotnet build $generator --no-restore -c Release -m:1 -nodeReuse:false -o $generatorOutput
    dotnet (Join-Path $generatorOutput 'UmaAiSharp.Pioneer.DataGenerator.dll') $master (Join-Path $resources 'pioneer_rules.br') `
        (Join-Path $resources 'turn-calibration.json') (Join-Path $resources 'inheritance-calibration.json')

    $version = dotnet msbuild $project -getProperty:Version -nologo
    $feed = Join-Path $stagedPlugin 'obj/nuget'
    foreach ($name in 'UmaAiSharp.Core', 'UmaAiSharp.Base', 'UmaAiSharp.Pioneer', 'UmaAiSharp.Pioneer.Game') {
        dotnet pack "src/$name/$name.csproj" -c Release --no-restore -m:1 -nodeReuse:false "-p:Version=$version" -o $feed
    }

    # Releases with the same project version must pick up this build's engine packages.
    $props = @(
        '-p:RuntimeIdentifier=win-x64'
        '-p:SelfContained=false'
        '-p:PlatformTarget=AnyCPU'
        '-p:DeployUraPluginToLocalAppDataOnBuild=false'
        "-p:RestorePackagesPath=$stage/packages"
    )
    dotnet restore $project --force-evaluate -m:1 -nodeReuse:false @props
    dotnet build $project --no-restore -c Release -m:1 -nodeReuse:false @props

    $artifacts = (New-Item -ItemType Directory -Path (Join-Path $repo 'artifacts') -Force).FullName
    $zip = Join-Path $artifacts 'PioneerAiPlugin.zip'
    [IO.File]::Move((Join-Path $stagedPlugin 'bin/Release/PioneerAiPlugin.zip'), $zip, $true)
    Write-Output $zip
}
finally {
    Pop-Location
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    if ($resolvedStage -eq $stage -and (Split-Path -Parent $resolvedStage) -eq $obj) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
