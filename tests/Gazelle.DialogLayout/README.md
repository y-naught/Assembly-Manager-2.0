# BOM dialog layout check

This Windows-only check instantiates the production `BomColumnsDialog` using the Eto/WPF libraries bundled with Rhino 8. It never shows a desktop window, starts RhinoCore, registers a plugin, or opens a user model. This avoids the installed/development Gazelle plugin UUID collision.

Build the production plugin first, then run from the repository root:

```powershell
dotnet build AssemblyManagerPlugin/AssemblyManagerPlugin.csproj -c Release
dotnet build tests/Gazelle.DialogLayout/Gazelle.DialogLayout.csproj -c Release
dotnet tests/Gazelle.DialogLayout/bin/Release/net7.0-windows/Gazelle.DialogLayout.dll
```

The optional first argument is the path to another Gazelle RHP, such as the Debug build. `RhinoSystemPath` and `GazellePluginPath` are build overrides; `GAZELLE_TEST_RHINO_SYSTEM` selects the runtime Rhino library directory. The default is `C:\Program Files\Rhino 8\System`. The runner needs the .NET 7 Windows Desktop runtime, like the existing geometry regression runner.

Checks cover:

- A compact initial client width of 390 logical pixels.
- Separate single-column sections, with action buttons outside the scrolling checklist.
- Layout at 390×480, 330×280 (a deliberately tight content-size stress check), and 600×600.
- No extra horizontal space requested, no clipped instructions, and visible action buttons at every checked size.
- Ten column choices, eight default selections, Select All/Clear behavior, and Confirm enabled only when a column is selected.

The test explicitly invokes Eto's normal preload/load callbacks so its deferred tables are built, then measures/arranges the actual WPF control tree off-screen. It writes `bom-dialog-default.png`, `bom-dialog-small.png`, and `bom-dialog-expanded.png` beside the executable, under ignored `bin/`, for visual inspection. The previews omit the native window frame and use a system window-color backdrop.

This does not certify Rhino-hosted theme/DPI behavior, live modal keyboard handling, or actual mouse prompts. Test those with the Visual Studio debugger launch, not by normally loading a second Gazelle plugin with the same UUID. BOM table geometry/layout is covered separately by [Gazelle.Regression](../Gazelle.Regression/README.md).
