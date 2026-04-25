# Contributing

Thanks for helping make TunProxy.NET easier to run, review, and maintain.

## Development setup

Use .NET 8 SDK on Windows for the full CLI, service, and tray workflow. The core library and tests are portable, but the Windows tray and service control paths require Windows APIs.

```powershell
dotnet restore tests\TunProxy.Tests\TunProxy.Tests.csproj
dotnet build src\TunProxy.CLI\TunProxy.CLI.csproj -v minimal
dotnet build src\TunProxy.Tray\TunProxy.Tray.csproj -v minimal
dotnet test tests\TunProxy.Tests\TunProxy.Tests.csproj -v minimal
git diff --check
```

## Pull requests

- Keep behavioral changes small and explain the user-facing impact.
- Add focused tests for routing decisions, config workflows, DNS behavior, and pure helper logic.
- Do not require a real TUN device, administrator rights, or live network access in unit tests.
- Preserve local-proxy setup mode when rule resources are missing or invalid.
- Keep DNS cache and observed hostname state in `DnsResolutionStore`.
- Keep `IpCacheManager` limited to route and IP state.

## Code style

Follow `.editorconfig`. Prefer small services with explicit dependencies over large static helpers. Keep platform-specific code at the edge of the app and push pure policy decisions into testable classes.
