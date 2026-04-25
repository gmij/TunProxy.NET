## Summary

-

## Validation

- [ ] `dotnet build src\TunProxy.CLI\TunProxy.CLI.csproj -v minimal`
- [ ] `dotnet build src\TunProxy.Tray\TunProxy.Tray.csproj -v minimal`
- [ ] `dotnet test tests\TunProxy.Tests\TunProxy.Tests.csproj -v minimal`
- [ ] `git diff --check`

## Risk

- [ ] Routing behavior
- [ ] DNS behavior
- [ ] Windows service or tray behavior
- [ ] Web console behavior
- [ ] Packaging or release behavior
