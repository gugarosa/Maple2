using System;
using System.IO;
using Maple2.Tools;

namespace Maple2.Server.Tests.Tools;

[NonParallelizable]
public class PathsTests {
    [Test]
    public void AssetOverridesApplyAfterTheSolutionRootWasAlreadyInitialized() {
        _ = Paths.SOLUTION_DIR;
        string? web = Environment.GetEnvironmentVariable("WEB_DATA_DIR");
        string? nav = Environment.GetEnvironmentVariable("MS2_NAVMESH_DIR");
        string root = Path.Combine(Path.GetTempPath(), "maple2-late-paths-" + Guid.NewGuid().ToString("N"));
        try {
            Environment.SetEnvironmentVariable("WEB_DATA_DIR", Path.Combine(root, "Web"));
            Environment.SetEnvironmentVariable("MS2_NAVMESH_DIR", Path.Combine(root, "Navigation"));
            Assert.That(Paths.WEB_DATA_DIR, Is.EqualTo(Path.Combine(root, "Web")));
            Assert.That(Paths.NAVMESH_DIR, Is.EqualTo(Path.Combine(root, "Navigation")));
            Assert.That(Paths.NAVMESH_HASH_DIR, Is.EqualTo(Path.Combine(root, "Navigation", "Hashes")));
        } finally {
            Environment.SetEnvironmentVariable("WEB_DATA_DIR", web);
            Environment.SetEnvironmentVariable("MS2_NAVMESH_DIR", nav);
        }
    }

    [Test]
    public void DevelopmentAssetsResolveToTheirProjectInTheCheckout() {
        string root = Path.Combine(Path.GetTempPath(), "maple2-paths-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(root, "Maple2.Server.Game", "bin", "Debug", "net8.0");
        Directory.CreateDirectory(output);
        try {
            System.IO.File.WriteAllText(Path.Combine(root, "Maple2.sln"), "");
            Assert.That(Paths.ResolveAssetDirectory(output, "Maple2.Server.Game", "Navmeshes"),
                Is.EqualTo(Path.Combine(root, "Maple2.Server.Game", "Navmeshes")));
        } finally {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void PublishedAssetsStayBesideTheApplicationRatherThanAtTheFilesystemRoot() {
        string root = Path.Combine(Path.GetTempPath(), "maple2-published-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            Assert.That(Paths.ResolveAssetDirectory(root, "Maple2.Server.Web", "Data"), Is.EqualTo(Path.Combine(root, "Data")));
            Assert.That(Paths.ResolveAssetDirectory(root, "Maple2.Server.Game", "Navmeshes"), Is.EqualTo(Path.Combine(root, "Navmeshes")));
        } finally {
            Directory.Delete(root, true);
        }
    }
}
