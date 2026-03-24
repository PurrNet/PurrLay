using System.Reflection;
using System.Runtime.Loader;

namespace PurrLay;

/// <summary>
/// Loads a plugin assembly in isolation so it can use its own version of LiteNetLib.
/// Types from PurrLay.Shared (IUdpServer, PlayerInfo, etc.) are resolved from the
/// default context so they're shared across all plugins.
/// </summary>
public class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginPath)
    {
        _pluginDirectory = Path.GetDirectoryName(pluginPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Let the default context handle shared assemblies (PurrLay.Shared, etc.)
        // so types like IUdpServer are the same across all plugins
        foreach (var loaded in Default.Assemblies)
        {
            if (loaded.GetName().Name == assemblyName.Name)
                return loaded;
        }

        // Try to find the assembly in the plugin directory
        var path = Path.Combine(_pluginDirectory, $"{assemblyName.Name}.dll");
        if (File.Exists(path))
            return LoadFromAssemblyPath(path);

        return null;
    }
}
