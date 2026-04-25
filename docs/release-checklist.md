# Release Checklist

Use this checklist for tagged releases.

## Before tagging

- Run CLI build, Windows tray build, macOS tray build, tests, and `git diff --check`.
- Verify Web Console static assets are embedded in `TunProxy.CLI` and release artifacts do not require a separate `wwwroot` directory.
- Confirm bundled `wintun.dll` source and version are documented.
- Confirm release notes mention routing, DNS, service, tray, and web-console behavior changes.
- Confirm no logs, credentials, or local config files are included.

## Tagging

Use semantic version tags such as `v1.2.3`. The release workflow publishes Windows, Linux, and macOS artifacts from tag pushes.

## After publishing

- Download each artifact from GitHub Actions.
- Smoke test startup and `/api/status`.
- On Windows, smoke test tray start, stop, install, uninstall, and restart.
- Attach or verify generated release notes.
