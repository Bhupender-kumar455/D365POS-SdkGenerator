param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\D365SdkGenerator.App\D365SdkGenerator.App.csproj"
$output = Join-Path $root "publish\$Runtime"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found in PATH. Install the .NET SDK first."
}

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o $output

# The SDK catalog is copied by the project; keep the fallback for older builds.
if (-not (Test-Path (Join-Path $output "sdk-catalog.json"))) {
    Copy-Item (Join-Path $root "config\sdk-catalog.json") $output -Force
}

$exe = Join-Path $output "D365SdkGenerator.exe"

if (-not (Test-Path $exe)) {
    throw "Publish finished but $exe was not created."
}

Write-Host ""
Write-Host "Published to : $output"
Write-Host "Executable   : $exe"
