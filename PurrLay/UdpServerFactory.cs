using System.Reflection;

namespace PurrLay;

/// <summary>
/// Creates IUdpServer instances from plugin directories using isolated AssemblyLoadContexts.
/// This allows V1 and V2 to each use their own version of LiteNetLib without conflict.
/// </summary>
public static class UdpServerFactory
{
    public static IUdpServer CreateV1(int port, UdpServerCallbacks callbacks)
    {
        return Create(
            Path.Combine(AppContext.BaseDirectory, "plugins", "udp-v1", "PurrLay.UdpV1.dll"),
            "PurrLay.UdpServerV1",
            port, callbacks);
    }

    public static IUdpServer CreateV2(int port, UdpServerCallbacks callbacks)
    {
        return Create(
            Path.Combine(AppContext.BaseDirectory, "plugins", "udp-v2", "PurrLay.UdpV2.dll"),
            "PurrLay.UdpServerV2",
            port, callbacks);
    }

    private static IUdpServer Create(string dllPath, string typeName, int port, UdpServerCallbacks callbacks)
    {
        var fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"UDP server plugin not found: {fullPath}");

        var context = new PluginLoadContext(fullPath);
        var assembly = context.LoadFromAssemblyPath(fullPath);
        var type = assembly.GetType(typeName)
                   ?? throw new TypeLoadException($"Type {typeName} not found in {fullPath}");
        return (IUdpServer)(Activator.CreateInstance(type, port, callbacks)
                            ?? throw new InvalidOperationException($"Failed to create {typeName}"));
    }
}
