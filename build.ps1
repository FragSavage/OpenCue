param([string]$ZigPath = '')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if ($ZigPath) {
    & $ZigPath cc -target x86_64-windows -std=c11 -O2 -fno-fast-math -shared -o (Join-Path $projectRoot 'engine/OpenCueDynamics.dll') (Join-Path $projectRoot 'source/Dynamics/OpenCueDynamics.c') -lm
    if ($LASTEXITCODE) { throw 'Dynamics build failed.' }
    & $ZigPath cc -target x86_64-windows -std=c11 -O2 -fno-fast-math -shared -o (Join-Path $projectRoot 'engine/OpenCueSpatial.dll') (Join-Path $projectRoot 'source/Spatial/OpenCueSpatial.c') -lm
    if ($LASTEXITCODE) { throw 'Spatial build failed.' }
}
dotnet publish (Join-Path $projectRoot 'source/OpenCue.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o (Join-Path $projectRoot 'build')
if ($LASTEXITCODE) { throw 'OpenCue build failed.' }
Write-Output 'Built build/OpenCue.exe. Copy it beside the original tools and licenses folders to retain the optional LEQ launcher. Keep the supplied source and licence files with a redistributed release.'
