using TunProxy.CLI;

namespace TunProxy.Tests;

public class ProgramTests
{
    [Fact]
    public void ResolveApiListenHost_DefaultsToLocalhostOnWindows()
    {
        Assert.Equal("127.0.0.1", TunProxy.CLI.Program.ResolveApiListenHost([], isWindows: true));
    }

    [Fact]
    public void ResolveApiListenHost_DefaultsToAnyIpOnNonWindows()
    {
        Assert.Equal("0.0.0.0", TunProxy.CLI.Program.ResolveApiListenHost([], isWindows: false));
    }

    [Fact]
    public void ResolveApiListenHost_UsesCommandLineOverride()
    {
        Assert.Equal(
            "192.168.10.20",
            TunProxy.CLI.Program.ResolveApiListenHost(["--api-host", "192.168.10.20"], isWindows: false));
    }

    [Fact]
    public void BuildForegroundArguments_RemovesBackgroundFlag()
    {
        Assert.Equal(
            ["--api-host", "0.0.0.0", "--proxy", "127.0.0.1:7890"],
            TunProxy.CLI.Program.BuildForegroundArguments(["--background", "--api-host", "0.0.0.0", "--proxy", "127.0.0.1:7890"]));
    }

    [Fact]
    public void BuildBackgroundLaunchCommand_UsesNohupAndRemovesBackgroundFlag()
    {
        var command = TunProxy.CLI.Program.BuildBackgroundLaunchCommand(
            "/opt/tunproxy/TunProxy.CLI",
            ["--background", "--api-host", "0.0.0.0"]);

        Assert.Contains("nohup '/opt/tunproxy/TunProxy.CLI' '--api-host' '0.0.0.0'", command);
        Assert.DoesNotContain("'--background'", command);
        Assert.EndsWith(">/dev/null 2>&1 < /dev/null & echo $!", command);
    }
}
