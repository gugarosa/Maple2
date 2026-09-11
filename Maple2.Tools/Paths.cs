// ReSharper disable InconsistentNaming
using System;
using System.IO;

namespace Maple2.Tools;

public static class Paths {
    public static readonly string SOLUTION_DIR = FindSolutionDirectory(AppContext.BaseDirectory) ?? Path.GetFullPath(AppContext.BaseDirectory);

    public static readonly string DEBUG_TRIGGERS_DIR = ResolveAssetDirectory(AppContext.BaseDirectory, "Maple2.Server.Game", "DebugTriggers");

    private static readonly string DefaultWebData = ResolveAssetDirectory(AppContext.BaseDirectory, "Maple2.Server.Web", "Data");
    private static readonly string DefaultNavmeshes = ResolveAssetDirectory(AppContext.BaseDirectory, "Maple2.Server.Game", "Navmeshes");

    public static string WEB_DATA_DIR => ConfiguredAssetDirectory("WEB_DATA_DIR", DefaultWebData);
    public static string NAVMESH_DIR => ConfiguredAssetDirectory("MS2_NAVMESH_DIR", DefaultNavmeshes);
    public static string NAVMESH_HASH_DIR => Path.Combine(NAVMESH_DIR, "Hashes");

    public static string ResolveAssetDirectory(string baseDirectory, string project, string folder) {
        string? solution = FindSolutionDirectory(baseDirectory);
        return solution == null
            ? Path.GetFullPath(Path.Combine(baseDirectory, folder))
            : Path.Combine(solution, project, folder);
    }

    private static string ConfiguredAssetDirectory(string variable, string defaultDirectory) {
        string? configured = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(configured)
            ? defaultDirectory
            : Path.GetFullPath(configured, AppContext.BaseDirectory);
    }

    private static string? FindSolutionDirectory(string baseDirectory) {
        for (DirectoryInfo? directory = new(baseDirectory); directory != null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Maple2.sln"))) {
                return directory.FullName;
            }
        }
        return null;
    }
}
