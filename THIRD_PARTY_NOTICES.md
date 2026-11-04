# Third-Party Notices

Windows Lossless Switcher is licensed under `GPL-3.0-or-later` (see [LICENSE](LICENSE)) and depends on the following third-party components. Each remains under its own license.

## Runtime dependencies

The DX5 tray host uses the NAudio Core/WASAPI components, TraceEvent and its transitive dependencies, and the Windows SDK .NET/WinRT projection. Its `WindowsSourceSwitcher.WindowsShared` library links selected GPL-3.0-or-later upstream sources. The original application's dependencies remain listed for source builds; TagLibSharp and Velopack are not dependencies of the DX5 tray host as of 0.3.0. Self-contained packages include the .NET/Windows Desktop runtime; framework-dependent packages require it to be installed separately.

| Component | Version | License | Source |
| --------- | ------- | ------- | ------ |
| [NAudio](https://github.com/naudio/NAudio) (DX5: NAudio.Core and NAudio.Wasapi) | 2.2.1 | [MIT](https://github.com/naudio/NAudio/blob/master/license.txt) | NuGet |
| [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) (DX5 tray host) | 3.2.8 | [MIT](notices/TraceEvent-LICENSE.txt) | NuGet |
| [Windows SDK .NET / WinRT projection](https://github.com/microsoft/CsWinRT) (DX5 catalog/media-session support) | Selected by Windows 10 19041 target | [MIT](https://github.com/microsoft/CsWinRT/blob/master/LICENSE) | Microsoft Windows SDK / NuGet |
| [TagLibSharp](https://github.com/mono/taglib-sharp) (original app only) | 2.3.0 | [LGPL-2.1](https://github.com/mono/taglib-sharp/blob/main/COPYING) | NuGet |
| [Velopack](https://github.com/velopack/velopack) (original app only) | 0.0.1298 | [MIT](https://github.com/velopack/velopack/blob/develop/LICENSE) | NuGet |
| [.NET 8 runtime](https://github.com/dotnet/runtime) (self-contained publish) | 8.x | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | Microsoft |

## Bundled assets

These UI assets belong to the original WPF application; the DX5 WinForms tray host does not reference them.

| Component | License | Source |
| --------- | ------- | ------ |
| [Inter](https://github.com/rsms/inter) font (Regular, Medium — `src/Assets/Fonts/`) | [SIL OFL 1.1](src/Assets/Fonts/Inter-LICENSE.txt) | rsms/inter v4.1 |
| [Phosphor Icons](https://github.com/phosphor-icons/core) (vector path data for 18 regular-weight glyphs in `src/Themes/Nocturne.Icons.xaml`) | [MIT](https://github.com/phosphor-icons/core/blob/main/LICENSE) | phosphor-icons/core |

## Build-time only

| Component | Version | License | Source |
| --------- | ------- | ------- | ------ |
| [Microsoft.SourceLink.GitHub](https://github.com/dotnet/sourcelink) | 8.0.0 | [MIT](https://github.com/dotnet/sourcelink/blob/main/License.txt) | NuGet |

## Test-time only

| Component | Version | License | Source |
| --------- | ------- | ------- | ------ |
| [xUnit](https://github.com/xunit/xunit) | 2.9.2 | [Apache-2.0](https://github.com/xunit/xunit/blob/main/LICENSE) | NuGet |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.11.1 | [MIT](https://github.com/microsoft/vstest/blob/main/LICENSE) | NuGet |

## Acknowledgement

This project takes its idea and name from [LosslessSwitcher](https://github.com/vincentneo/LosslessSwitcher) for macOS by Vincent Neo, released under the [MIT license](https://github.com/vincentneo/LosslessSwitcher/blob/main/LICENSE). Windows Lossless Switcher is an independent implementation and shares no code with it.
