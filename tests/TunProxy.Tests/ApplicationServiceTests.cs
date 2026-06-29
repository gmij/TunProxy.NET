using System.Text.Json;
using TunProxy.CLI;
using TunProxy.Core;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class ApplicationServiceTests
{
    [Fact]
    public void ApplyIncomingConfigPreservingSystemProxyBackup_KeepsActiveBackupWhenIncomingOmitsIt()
    {
        var active = new AppConfig
        {
            LocalProxy =
            {
                SystemProxyBackup = new SystemProxyBackupConfig
                {
                    Captured = true,
                    ProxyEnable = 1,
                    ProxyServer = "original:8080",
                    ProxyOverride = "<local>",
                    AutoConfigUrl = "http://original/pac"
                }
            }
        };
        var incoming = new AppConfig
        {
            Proxy = new ProxyConfig { Host = "new.local", Port = 1080 }
        };

        ConfigWorkflowService.ApplyIncomingConfigPreservingSystemProxyBackup(active, incoming);

        Assert.Equal("new.local", active.Proxy.Host);
        Assert.True(active.LocalProxy.SystemProxyBackup.Captured);
        Assert.Equal("original:8080", active.LocalProxy.SystemProxyBackup.ProxyServer);
        Assert.Equal("http://original/pac", active.LocalProxy.SystemProxyBackup.AutoConfigUrl);
    }

    [Fact]
    public async Task ApplyAndSaveAsync_PersistsConfigAndRefreshesRuleResources()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-workflow-{Guid.NewGuid():N}.json");
        try
        {
            var active = new AppConfig();
            var incoming = new AppConfig
            {
                Proxy = new ProxyConfig { Host = "saved.local", Port = 7891 }
            };
            var proxy = new FakeProxyService();
            var workflow = new ConfigWorkflowService(
                new AppConfigStore(path),
                _ => { });

            await workflow.ApplyAndSaveAsync(active, incoming, proxy, CancellationToken.None);

            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                AppJsonContext.Default.AppConfig);

            Assert.NotNull(saved);
            Assert.Equal("saved.local", saved.Proxy.Host);
            Assert.Equal(7891, saved.Proxy.Port);
            Assert.Equal(1, proxy.RefreshCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EnableTunAsync_SetsTunModeAndRunsTunSideEffectBeforeSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-workflow-{Guid.NewGuid():N}.json");
        var sideEffectCount = 0;
        try
        {
            var config = new AppConfig();
            var workflow = new ConfigWorkflowService(
                new AppConfigStore(path),
                _ => sideEffectCount++);

            await workflow.EnableTunAsync(config, CancellationToken.None);

            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                AppJsonContext.Default.AppConfig);

            Assert.NotNull(saved);
            Assert.True(saved.Tun.Enabled);
            Assert.Equal(SystemProxyModes.Tun, saved.LocalProxy.SystemProxyMode);
            Assert.Equal(ConfigWorkflowService.CurrentDefaultTunIpAddress, saved.Tun.IpAddress);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(1, sideEffectCount);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NormalizeTunDefaultsForSave_DoesNotChangeCustomTunAddress()
    {
        var config = new AppConfig
        {
            Tun =
            {
                Enabled = true,
                IpAddress = "172.31.255.1"
            }
        };

        ConfigWorkflowService.NormalizeTunDefaultsForSave(config);

        Assert.Equal("172.31.255.1", config.Tun.IpAddress);
    }

    [Theory]
    [InlineData(null, "all")]
    [InlineData("", "all")]
    [InlineData(" GEO ", "geo")]
    [InlineData("gFw", "gfw")]
    public void NormalizeResourceName_NormalizesRouteResourceNames(string? value, string expected)
    {
        Assert.Equal(expected, RuleResourceService.NormalizeResourceName(value));
    }

    [Fact]
    public void BuildWindowsRestartCommand_UsesServiceRestartWhenInstalled()
    {
        var command = RestartCoordinator.BuildWindowsRestartCommand(
            serviceInstalled: true,
            processPath: @"C:\Apps\TunProxy.CLI.exe");

        Assert.Contains($"sc stop {TunProxyProduct.ServiceName}", command);
        Assert.Contains($"sc start {TunProxyProduct.ServiceName}", command);
    }

    [Fact]
    public void BuildWindowsRestartCommand_UsesProcessStartWhenServiceIsNotInstalled()
    {
        var command = RestartCoordinator.BuildWindowsRestartCommand(
            serviceInstalled: false,
            processPath: @"C:\Apps\TunProxy.CLI.exe");

        Assert.Contains("start \"\" \"C:\\Apps\\TunProxy.CLI.exe\"", command);
    }

    [Fact]
    public async Task ProxyHostedService_StopAndStartProxy_KeepsControllerAvailable()
    {
        var proxy = new LifecycleProxyService();
        var hosted = new ProxyHostedService<LifecycleProxyService>(proxy);

        await hosted.StartAsync(CancellationToken.None);
        Assert.True(hosted.IsRunning);

        var stopped = await hosted.StopProxyAsync(CancellationToken.None);
        Assert.True(stopped);
        Assert.False(hosted.IsRunning);

        var started = await hosted.StartProxyAsync(CancellationToken.None);
        Assert.True(started);
        Assert.True(hosted.IsRunning);

        await hosted.StopAsync(CancellationToken.None);
        Assert.False(hosted.IsRunning);
        Assert.True(proxy.StartCount >= 2);
    }

    [Fact]
    public async Task ProxyHostedService_RestartProxy_RestartsUnderlyingService()
    {
        var proxy = new LifecycleProxyService();
        var hosted = new ProxyHostedService<LifecycleProxyService>(proxy);

        await hosted.StartAsync(CancellationToken.None);
        var restarted = await hosted.RestartProxyAsync(CancellationToken.None);

        Assert.True(restarted);
        Assert.True(hosted.IsRunning);
        Assert.True(proxy.StartCount >= 2);
        Assert.True(proxy.StopCount >= 1);

        await hosted.StopAsync(CancellationToken.None);
    }

    private sealed class FakeProxyService : IProxyService
    {
        public int RefreshCount { get; private set; }

        public ServiceStatus GetStatus() => new();

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public Task RefreshRuleResourcesAsync(CancellationToken ct)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleProxyService : IProxyService
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public ServiceStatus GetStatus() => new();

        public async Task StartAsync(CancellationToken ct)
        {
            StartCount++;
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public Task RefreshRuleResourcesAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
