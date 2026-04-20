namespace TunProxy.CLI;

public sealed record RuleResourcesStatus(
    RuleResourceStatus Geo,
    RuleResourceStatus Gfw);

public sealed record RuleResourceStatus(
    string Name,
    bool Enabled,
    string Path,
    bool Exists,
    bool Ready);
