# Contributing to Source Switcher

Bug reports, device compatibility reports, and focused pull requests are welcome.

## Report a bug or suggest an improvement

Search [existing issues](https://github.com/EnzoChizari/source-switcher/issues), then open an issue with the matching template. For a bug, include the Source Switcher and Windows versions, your playback device model, the source app, and steps to reproduce. If the issue involves a format change, include the observed and expected sample rate or bit depth.

If you include a screenshot or log from Diagnostics, redact personal details and credentials first. For suspected vulnerabilities, follow the [security policy](SECURITY.md).

## Run and test locally

Install the .NET 8 SDK on Windows. From the repository root, run the tray app and its test suites with these commands.

```powershell
dotnet run --project .\systemwide\WindowsSourceSwitcher.Tray
dotnet test .\tests\WindowsSourceSwitcher.Core.Tests
dotnet test .\tests\WindowsSourceSwitcher.Tray.Tests
```

## Open a pull request

Describe the behavior that changed and list the checks you ran. For changes to audio switching or device handling, include the hardware and source apps used for any manual verification. Keep generated builds, local settings, and raw diagnostics out of the pull request.
