using System.IO.Compression;
using System.Reflection;

namespace JointVentures.Launcher;

/// <summary>
/// Extracts the embedded plugin ZIP to a local cache directory.
/// Always deletes the existing plugin and re-extracts on every launch.
/// </summary>
internal static class BundleExtractor
{
    private const string ResourceName = "plugin-bundle.zip";

    /// <summary>
    /// Returns the cache directory path. Always deletes and re-extracts the
    /// plugin DLL from embedded resources to ensure it is never stale.
    /// </summary>
    public static string EnsureExtracted()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JointVentures");

        var pluginDir = Path.Combine(cacheDir, "BepInEx", "plugins", "COIJointVentures");

        // Always remove the old plugin so it is never stale
        if (Directory.Exists(pluginDir))
            Directory.Delete(pluginDir, recursive: true);

        // Extract plugin
        Directory.CreateDirectory(pluginDir);

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "Embedded plugin bundle not found. The launcher was not built correctly.");

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0) continue;
            entry.ExtractToFile(Path.Combine(pluginDir, entry.Name), overwrite: true);
        }

        return cacheDir;
    }
}
