# Builds an installer (Map-Setup-<date>.exe), plus a folder + zip, that run on any Windows 10/11 x64 PC with nothing installed:
#   artifacts\package\Map\Map.exe                self-contained single-file app (.NET 10 inside)
#   artifacts\package\Map\climate\python\         embeddable Python 3.12 + numpy/scipy/matplotlib
#   artifacts\package\Map\climate\tools\climate\  the worldclimate model (same layout as the repository,
#   artifacts\package\Map\climate\data\climate\   so engine/profile.py finds data/climate from its own path)
# Downloads (cached in artifacts\package-cache): .NET runtime packs from nuget.org, the Python embeddable zip
# from python.org, wheels from PyPI. Needs the .NET 10 SDK and a Windows Python 3.12 with pip on this PC.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'artifacts\package\Map'
$cache = Join-Path $root 'artifacts\package-cache'
$pythonVersion = '3.12.10'
# numpy and scipy as in the WSL environment the model was checked with; matplotlib 3.6.3 has no Python 3.12 wheel.
$wheels = @('numpy==1.26.4', 'scipy==1.11.4', 'matplotlib==3.8.4')

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out, $cache | Out-Null

# ---- app, published from a copy of src so the repository's obj\ (offline build.ps1) stays untouched
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $cache 'nuget'
$build = Join-Path $cache 'build'
if (Test-Path $build) { Remove-Item -Recurse -Force $build }
New-Item -ItemType Directory -Force $build | Out-Null
Copy-Item (Join-Path $root 'Directory.Build.props') $build
foreach ($project in 'TheGarden.Core', 'TheGarden.Desktop') {
    robocopy (Join-Path $root "src\$project") (Join-Path $build "src\$project") /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { exit $LASTEXITCODE }
}
dotnet publish (Join-Path $build 'src\TheGarden.Desktop\TheGarden.Desktop.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    --source https://api.nuget.org/v3/index.json --nologo --disable-build-servers -m:1 -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# ---- Python
$climate = Join-Path $out 'climate'
$python = Join-Path $climate 'python'
$embed = Join-Path $cache "python-$pythonVersion-embed-amd64.zip"
if (-not (Test-Path $embed)) {
    Invoke-WebRequest "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip" -OutFile $embed -UseBasicParsing
}
Expand-Archive $embed -DestinationPath $python
# The ._pth file fixes sys.path (PYTHONPATH is ignored): the standard library, the wheels and the model.
Set-Content (Join-Path $python 'python312._pth') -Encoding ascii -Value @('python312.zip', '.', 'Lib\site-packages', '..\tools\climate')
py -3.12 -m pip install --disable-pip-version-check --no-compile --only-binary=:all: --platform win_amd64 --python-version 3.12 `
    --implementation cp --cache-dir (Join-Path $cache 'pip') --target (Join-Path $python 'Lib\site-packages') @wheels
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# pip's command-line launchers (f2py.exe, ttx.exe, ...) carry the full path of the Python that installed them, which
# names this computer's user folder; the app never runs them
$launchers = Join-Path $python 'Lib\site-packages\bin'
if (Test-Path $launchers) { Remove-Item -Recurse -Force $launchers }

# ---- model: only what worldclimate imports (worldclimate, vector_terrain, engine.profile/astronomy) and its data
$tools = Join-Path $climate 'tools\climate'
New-Item -ItemType Directory -Force (Join-Path $tools 'worldclimate'), (Join-Path $tools 'engine'), (Join-Path $climate 'data\climate') | Out-Null
Copy-Item (Join-Path $root 'tools\climate\worldclimate\*.py') (Join-Path $tools 'worldclimate')
Copy-Item (Join-Path $root 'tools\climate\vector_terrain.py') $tools
foreach ($f in '__init__.py', 'profile.py', 'astronomy.py') { Copy-Item (Join-Path $root "tools\climate\engine\$f") (Join-Path $tools 'engine') }
Copy-Item (Join-Path $root 'data\climate\earth-mean-v1.json') (Join-Path $climate 'data\climate')

# ---- check: the bundled Python imports the whole model
& (Join-Path $python 'python.exe') -c "import worldclimate.run, numpy, scipy, matplotlib; print('climate imports ok', numpy.__version__, scipy.__version__, matplotlib.__version__)"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item (Join-Path $root 'docs\package-readme.txt') (Join-Path $out 'README.txt')
Copy-Item (Join-Path $root 'docs\package-third-party.txt') (Join-Path $out 'THIRD-PARTY-NOTICES.txt')

# ---- check: no file of the public package names this computer's user folder (C:\Users\<account>), as text or UTF-16
$profiles = @($env:USERPROFILE, $env:USERPROFILE.Replace('\', '/'))
$leaks = foreach ($file in Get-ChildItem $out -Recurse -File) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $text = [Text.Encoding]::GetEncoding(28591).GetString($bytes); $wide = [Text.Encoding]::Unicode.GetString($bytes)
    foreach ($p in $profiles) {
        if ($text.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $wide.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $file.FullName; break }
    }
}
if ($leaks) { "package names the user folder in:"; $leaks; exit 1 }
"no user folder in the package"

# ---- zip
$zip = Join-Path $root ("artifacts\package\Map-" + (Get-Date -Format 'yyyyMMdd') + '.zip')
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
"package: $out"
"zip:     $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"

# ---- installer (Inno Setup 6, installed into artifacts\package-cache\innosetup from jrsoftware's GitHub release)
$iscc = Join-Path $cache 'innosetup\ISCC.exe'
if (-not (Test-Path $iscc)) { "installer skipped: $iscc not found"; exit 0 }
$version = Get-Date -Format 'yyyy.MM.dd'
& $iscc /Qp "/DSrc=$out" "/DVersion=$version" "/DOutDir=$(Join-Path $root 'artifacts\package')" (Join-Path $root 'installer\TheGarden.iss')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$setup = Join-Path $root "artifacts\package\Map-Setup-$version.exe"
"setup:   $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)"
