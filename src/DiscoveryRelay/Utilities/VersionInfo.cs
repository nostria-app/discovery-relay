using System.Reflection;

namespace DiscoveryRelay.Utilities;

/// <summary>
/// Provides access to application version information
/// </summary>
public static class VersionInfo
{
    /// <summary>
    /// Gets the current application version from the executing assembly
    /// </summary>
    public static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;

        return version?.ToString() ?? "1.0.0";
    }

    /// <summary>
    /// Gets the informational version which may contain additional version details
    /// </summary>
    public static string GetInformationalVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var attributes = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);

        if (attributes.Length > 0)
        {
            return ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
        }

        return GetCurrentVersion();
    }
}
