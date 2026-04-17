namespace TunProxy.Core.Tun;

/// <summary>
/// TUN 设备抽象接口（Windows Wintun / Linux tun0 / macOS utun）
/// </summary>
public interface ITunDevice : IDisposable
{
    /// <summary>
    /// 配置 TUN 接口 IP 地址和子网掩码（设备创建后、Start 之前调用）
    /// </summary>
    void Configure(string ip, string subnetMask, int mtu = 1500);

    /// <summary>启动设备，开始收发数据包</summary>
    void Start();

    /// <summary>停止设备</summary>
    void Stop();

    /// <summary>
    /// 阻塞读取一个 IP 数据包，返回 null 表示设备已停止
    /// </summary>
    byte[]? ReadPacket();

    /// <summary>向 TUN 设备写入一个 IP 数据包</summary>
    void WritePacket(byte[] packet);
}
