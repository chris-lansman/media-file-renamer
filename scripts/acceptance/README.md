# Native Windows UI acceptance

`Invoke-UiAcceptance.ps1` exercises the exact packaged WPF executable through
Windows UI Automation. It creates a marked, isolated application data directory
and passes it through `--data-root`, so settings, operation journals, and logs
do not touch the tester's normal `%LOCALAPPDATA%\MediaFileRenamer` directory.

Run it in a fresh Windows PowerShell 5.1 STA process:

```powershell
$exe = Resolve-Path .\publish\MediaFileRenamer.exe
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$version = (Get-Item $exe).VersionInfo.ProductVersion

powershell.exe -NoProfile -STA -ExecutionPolicy Bypass `
  -File .\scripts\acceptance\Invoke-UiAcceptance.ps1 `
  -ExecutablePath $exe `
  -ExpectedSha256 $hash `
  -ExpectedProductVersion $version `
  -DataRoot C:\MediaFileRenamer-Acceptance\data-dpi100 `
  -ArtifactRoot C:\MediaFileRenamer-Acceptance\evidence-dpi100 `
  -ExpectedDpi 96 `
  -ExpectedHighContrast Disabled
```

Both output paths must be absolute, non-root, new or empty, and must not contain
one another. The harness refuses to run when another `MediaFileRenamer` process
exists. It never deletes either output directory.

Run separate disposable Windows user or VM snapshots at 96, 144, and 192 DPI.
Run another snapshot with High Contrast enabled. The harness verifies and
records those settings; it deliberately does not change Windows display or
accessibility configuration.

Evidence includes:

- executable hash and version;
- OS, PowerShell, DPI, text scale, and High Contrast state;
- per-window UIA control trees with credential values redacted;
- bounds and supported accessibility patterns;
- forward and reverse Tab traversal;
- keyboard menu and Escape behavior;
- window screenshots; and
- a machine-readable `results.json`.

The UIA audit covers the same names, roles, states, and patterns consumed by
screen readers. Actual Narrator speech remains a manual acceptance check because
Windows does not provide a supported API for capturing Narrator's spoken output.
