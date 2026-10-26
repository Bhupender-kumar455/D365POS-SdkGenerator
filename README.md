# D365 Commerce SDK Generator

Windows desktop app for bootstrapping a combined Dynamics 365 Commerce
**Store Commerce + Scale Unit** extension solution.

Pick a Commerce SDK release, name the project, choose an output folder and press
**Generate project**. The app downloads the official Microsoft templates, merges them into
one solution, applies your project identity and validates the result. A live activity pane
shows every step; **Cancel** rolls the partial project back.

## Requirements

| Requirement | Why | Install |
| --- | --- | --- |
| Windows 10/11 (x64 or ARM64) | The app is a native WPF window | — |
| .NET SDK 8 or later | `dotnet sln add` places the Scale Unit projects in the solution, and `global.json` alignment is verified with `dotnet --version` | `winget install --id Microsoft.DotNet.SDK.8` |
| Node.js (LTS) | The POS project compiles TypeScript with `Microsoft.TypeScript.MSBuild`, which needs `node.exe` | `winget install --id OpenJS.NodeJS.LTS` |
| .NET 10 runtime, **only for SDK 9.59+** | Those packages ship a .NET 10 POS proxy generator; without it the build falls back to the .NET 8 copy and fails with `No types could be loaded ... IEdmModelFactory` | `winget install --id Microsoft.DotNet.AspNetCore.10` |
| PowerShell 7 (`pwsh`), **only for SDK 9.59+** | The proxy generator is invoked through `pwsh` | `winget install --id Microsoft.PowerShell` |
| .NET Framework targeting packs | The installer projects target `net472` / `net48` | Visual Studio Build Tools |
| Long paths enabled, **or** a short output folder | The generated layout (Scale Unit packaging output) exceeds the 260-character limit | See below |

The app checks most of these before generation starts and shows a warning in the form
(with the exact `winget` command). Warnings do not block generation; they tell you what the
generated solution will need at build time. Only a missing .NET SDK is blocking.

The **Build requirements** strip under the form always shows the detected toolchain, so the
build prerequisites are visible before and after generating:

```text
Build requirements · needed to build the generated solution
  ● .NET SDK 9.0.319   ● Node.js 24.22.1   ● Git 2.56.0   ● PowerShell 7.6.8   ● .NET 10 runtime not found
```

A green dot means the tool was found (its detected version is shown). A red dot means it is
missing; missing chips also show a **Download** action (`Learn more` for long paths). Click the
chip to open the official vendor page in your browser — or hover it to see why the tool is
needed and the exact `winget` command that installs it. The strip adapts to the selection: Git
appears when **Initialize a Git repository** is checked, PowerShell 7 and the .NET 10 runtime
only for SDK 9.59+, and a **Long paths** chip appears when Windows long path support is off and
the target path would exceed the limit. It is re-probed when the window regains focus, so
installing a missing tool is picked up without restarting the app.

### Long paths

Windows limits paths to 260 characters unless long path support is enabled. In an
administrator PowerShell:

```powershell
reg add "HKLM\SYSTEM\CurrentControlSet\Control\FileSystem" /v LongPathsEnabled /t REG_DWORD /d 1 /f
```

Reopen the terminal and Visual Studio afterwards. Alternatively generate into a short root
such as `C:\D365`: when the selected output folder is too deep, the app offers a one-click
**Use a short output folder (C:\D365)** link. A short project name (for example
`Contoso.SCP` instead of `Contoso.StoreCommercePackaging`) buys another ~20 characters.

## Run

Published build (self-contained, no .NET installation needed to *run* the app):

```text
publish\win-x64\D365SdkGenerator.exe
```

From source:

```powershell
dotnet run --project .\src\D365SdkGenerator.App
```

## Using the app

| Field | Notes |
| --- | --- |
| Commerce SDK | Read from `sdk-catalog.json` (9.53 – 9.59, plus a custom placeholder). Defaults to the latest release. |
| Project name | Used for the solution, every project, the packages and the C# namespace root. Letters, digits, `.`, `_`; no spaces. |
| Publisher | Package manifest and certificate subject (`CN=<publisher>`). |
| Output folder | `<output>\<project name>` is created. Existing non-empty folders are rejected up front. |
| Initialize Git | Runs `git init` in the generated project. |

Other behaviour:

* **Creates** shows the exact target path before anything is downloaded.
* **Activity** streams download URLs, per-step progress, validation results and the solution path.
* **Cancel** stops the download/copy and removes the partially generated project.
* **Open folder** / **Open solution** appear after a successful run.
* The moon/sun button in the top bar switches between the light and dark themes. The native
  title bar follows the theme, and the choice is remembered across launches.
* The last used SDK, project name, publisher, output folder, Git choice and theme are remembered
  in `%LOCALAPPDATA%\D365SdkGenerator\app-settings.json`.

## What the generator does

1. Downloads the Store Commerce template from `Dynamics365Commerce.InStore` as a ZIP archive.
2. Downloads the Scale Unit template from `Dynamics365Commerce.ScaleUnit` as a ZIP archive.
3. Uses the official `StoreCommerceApp` packaging sample as the base solution.
4. Adds the Scale Unit and Scale Unit installer projects as siblings to the existing Channel
   Database, Commerce Runtime and POS projects.
5. Applies project/namespace/publisher substitutions and patches the POS project so the
   TypeScript build also works from folders whose names contain spaces.
6. Preserves `CustomizationPackage.props`, `nuget.config` and `repo.props` from the Store
   Commerce repository (`global.json` is copied when that branch ships one).
7. Turns off the SDK's "install the extensions after every Debug build" hook in `repo.props`
   (see [Builds do not deploy](#builds-do-not-deploy)).
8. Rewrites the Scale Unit project references so they reuse the generated Store Commerce
   Channel Database, Commerce Runtime and POS projects.
9. Adds the Scale Unit projects to the existing Store Commerce solution at the solution root
   and verifies they are listed in it.
10. Validates the result (required files, no legacy template identity, solution entries,
    project references) before writing `.d365sdk.json` and optionally initializing Git.

## Defaults

| Field | Default |
| --- | --- |
| Project name | `Contoso.StoreCommercePackaging` |
| Publisher | `Contoso Ltd.` |
| Output folder | `%USERPROFILE%\Documents\D365Projects` |

The two template identities (`Contoso.StoreCommercePackagingSample` / `...Samples`) are
reserved and rejected, because using them would make the rename a no-op.

## Result for POS + CSU

```text
<project>
├── global.json            (only when the selected SDK branch ships one)
├── nuget.config
├── repo.props
├── .d365sdk.json
└── src
    └── PackagingSamples
        └── StoreCommerceApp
            ├── CustomizationPackage.props
            ├── <project>.sln
            ├── ChannelDatabase
            │   └── <project>.ChannelDatabase.csproj
            ├── CommerceRuntime
            │   └── <project>.CommerceRuntime.csproj
            ├── HardwareStation
            │   └── <project>.HardwareStation.csproj
            ├── Pos
            │   └── <project>.Pos.csproj
            ├── StoreCommerce.Installer
            │   └── <project>.StoreCommerce.Installer.csproj
            ├── ScaleUnit
            │   └── <project>.ScaleUnit.csproj
            └── ScaleUnit.Installer
                └── <project>.ScaleUnit.Installer.csproj
```

All seven projects are listed directly at the root of the solution. `dotnet sln add`
normally nests newly added projects in a solution folder named after their parent
directory; the generator requests `--in-root` and additionally normalizes the `.sln`, so
`ScaleUnit` and `ScaleUnit.Installer` do not appear inside a `ScaleUnit` solution folder.

Microsoft publishes the Store Commerce and Scale Unit samples in separate repositories. The
Scale Unit sample's `ScaleUnit.csproj` references Channel Database, Commerce Runtime and POS,
so the generator reuses those projects instead of generating duplicate copies.

## Builds do not deploy

The Commerce SDK imports a target that runs the Store Commerce extensions installer after
every **Debug** build that is started from Visual Studio
(`InstallStoreCommerceExtensionsAfterBuild` defaults to `true` there). Left alone, building
the solution quietly installs the Store Commerce app and the extension on the machine and
prints the installer output in the Build window.

The generator disables that hook by adding the following to the generated `repo.props`:

```xml
<PropertyGroup>
  <InstallStoreCommerceExtensionsAfterBuild>false</InstallStoreCommerceExtensionsAfterBuild>
</PropertyGroup>
```

Building is therefore side-effect free; `dotnet build` from a terminal, Release builds and
F5 in Visual Studio all behave the same way. Deployment is an explicit step — run the
installer yourself when you actually want to deploy:

```powershell
cd src\PackagingSamples\StoreCommerceApp\StoreCommerce.Installer
.\bin\Debug\net472\<project>.StoreCommerce.Installer.exe install --skipversioncheck=true
```

To restore the SDK default for a debugging session, set the property to `true` in
`repo.props` (or pass `-p:InstallStoreCommerceExtensionsAfterBuild=true` to a build).

## Identities the generator rewrites

| What | From | To |
| --- | --- | --- |
| Solution / project names | `Contoso.StoreCommercePackagingSample(s)` | `<project name>` |
| Store Commerce package | `Contoso Store Commerce Customization Packaging Sample` | `<project name> Store Commerce Customization` |
| Scale Unit package | `Contoso.Commerce` | `<project name>.ScaleUnit` |
| Publisher (cert subject) | `CN=Contoso Ltd.` | `CN=<publisher>` |
| Publisher display name | `Contoso Ltd.` | `<publisher>` |
| POS manifest publisher | `"Contoso"` | `<publisher>` |
| C# namespace root | `Contoso` | first segment of the project name |
| Extension settings key | `ext.Contoso` | `ext.<namespace root>` |

The `CONTOSO_*` SQL object names in the Channel Database sample are intentionally left alone:
they are sample schema identifiers, not branding.

## Publish

```powershell
.\scripts-publish.ps1
# or
dotnet publish .\src\D365SdkGenerator.App\D365SdkGenerator.App.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The result is a single `D365SdkGenerator.exe` plus `sdk-catalog.json`.

## Configuration

`sdk-catalog.json` lists the selectable SDK versions. Each entry stores the repository,
branch and source root for both templates. Microsoft uses lowercase `release/<version>`
branches in both repositories; the downloader still tries the opposite casing as a fallback.

Package versions inside the generated project come from the template's `repo.props` on the
selected branch (for example `[9.55.*-*,9.56)`). The generator reads that value and records it
in `.d365sdk.json`.

The Scale Unit repository references its packages without versions and relies on a central
`Directory.Packages.props`. That file is not copied as-is, because central package management
would reject the explicit versions used by the Store Commerce projects. Instead, the generator
adds `Version="$(CommerceSdkPackagesVersion)"` to the versionless Scale Unit package references.

## Project layout

```text
src
├── D365SdkGenerator.Core   # models, templates, identity rewrite, validation, preflight (net8.0)
└── D365SdkGenerator.App    # WPF single-window app (net8.0-windows)
```

The pipeline has no UI dependencies: services report progress through `IGeneratorLog`, which
the app forwards to its activity pane.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `MSB4018` / `MSB3021` / `PathTooLongException` | Windows long paths are disabled and the output folder is too deep. Enable long paths or generate into `C:\D365` (the app warns before generation). |
| `MSB3073 ... No types could be loaded ... IEdmModelFactory` | SDK 9.59+ without the .NET 10 runtime. Install `Microsoft.DotNet.AspNetCore.10`. |
| `MSB3073` `pwsh ... exited with code 9009` | PowerShell 7 is not installed (SDK 9.59+). Install `Microsoft.PowerShell`. |
| `The build task could not find node.exe` | Node.js is missing. Install `OpenJS.NodeJS.LTS` and reopen the terminal/Visual Studio. |
| `MSB3030` for `should_not_exist.file` | Fixed by the generator: the POS project now compiles TypeScript during the installer packaging query and packages only files that exist. Regenerate the project with this version. |
| Installer resolves old Commerce packages (`9.29 ...`) or reports a missing `Extension.config` | Fixed by the generator: Scale Unit package references now carry explicit `$(CommerceSdkPackagesVersion)` versions instead of relying on the Scale Unit repository's central package management file. Regenerate. |
| Building in Debug launches `StoreCommerce.Installer.exe install` in the Build window | The Commerce SDK installs the extensions after every Debug build started from Visual Studio. The generator disables this in `repo.props`; projects generated before that change need `<InstallStoreCommerceExtensionsAfterBuild>false</InstallStoreCommerceExtensionsAfterBuild>` in `repo.props`, or build `Release`. |
| `tsc : error TS5042: Option 'project' cannot be mixed with source files on a command line` | The Commerce POS SDK hands the project path to `tsc` unquoted, so any folder name with a space (for example `Desktop\New folder`) breaks the TypeScript build and every installer that packages POS output. Fixed by the generator: the POS project re-declares the same `sourceRoot`/`mapRoot` flags with quoted paths. Projects generated before that change must be regenerated. |
| **Generate project** is greyed out | A field is invalid: the project name is reserved/malformed, or `<output>\<project name>` already exists and is not empty. |
