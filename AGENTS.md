# Source Switcher publishing

- Work in the canonical Source Switcher repository. Commit reviewed project changes on `codex/source-switcher` and verify they reach `private/main` at `EnzoChizari/source-switcher`. Never push to upstream `origin`.
- Stage only task files. Keep credentials, runtime settings, diagnostics, research, and generated builds outside release commits.
- Bump the tray project version by one patch for a fix or by one minor version for a larger feature. Documentation-only changes can keep the version. GitHub Actions builds and publishes a Windows x64 release for a new version after tests pass. Check the workflow and release before reporting publication.
- After a new stable release, run `tools/Install-PrivateRelease.ps1` with the published version and scratch space under the canonical `work/` directory. The local Git install path names the existing Windows app folder. Replace that folder without a backup, restart the app, and verify the new process.
