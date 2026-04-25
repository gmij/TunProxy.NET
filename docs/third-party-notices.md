# Third-party Notices

This file records dependencies that should be reviewed before public releases. It is not a substitute for a project license.

## Runtime packages

- .NET 8 runtime and SDK
- `Serilog.AspNetCore`
- `Serilog.Sinks.Console`
- `Serilog.Sinks.File`
- `MaxMind.GeoIP2`
- `Microsoft.Extensions.Hosting.WindowsServices`
- `System.ServiceProcess.ServiceController`

## Test packages

- `xunit`
- `xunit.runner.visualstudio`
- `Microsoft.NET.Test.Sdk`
- `coverlet.collector`

## Bundled binaries

- `wintun.dll` is bundled for Windows TUN support. Confirm the exact upstream Wintun version, license terms, and redistribution requirements before each public release.

## Open item

The repository still needs an explicit top-level license selected by the maintainers before it should be treated as a complete open-source release.
