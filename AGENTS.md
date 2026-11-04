# Source Switcher publishing

- Work in the canonical Source Switcher repository. Commit reviewed project changes on `codex/source-switcher` and verify they reach `private/main` at `EnzoChizari/source-switcher`. Never push to upstream `origin`.
- Stage only task files. Keep credentials, runtime settings, diagnostics, research, and generated builds outside release commits.
- Bump the tray project version by one patch for a fix or by one minor version for a larger feature. Add user-facing notes under the new version in `SOURCE_SWITCHER_CHANGELOG.md`. Documentation-only changes can keep the version. GitHub Actions builds and publishes a Windows x64 release for a new version after tests pass. Check the workflow and release before reporting publication.
- Leave the local installation in place after publishing so the owner can test the in-app update badge and changelog. If the owner explicitly asks for a direct replacement, use `tools/Install-PrivateRelease.ps1` with the published version and scratch space under the canonical `work/` directory. Replace the configured install folder without a backup, restart the app, and verify the new process.
