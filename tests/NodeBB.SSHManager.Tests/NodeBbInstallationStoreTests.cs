using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class NodeBbInstallationStoreTests
{
    [Fact]
    public void SavesAndReloadsPathsPerProfile()
    {
        var root = CreateTempDirectory();
        try
        {
            var first = new NodeBbInstallationStore(Guid.NewGuid(), root);
            var second = new NodeBbInstallationStore(Guid.NewGuid(), root);
            first.Add("/srv/nodebb/");
            first.Add("/opt/forums/nodebb");
            first.Add("/srv/nodebb");

            Assert.Equal(new[] { "/srv/nodebb", "/opt/forums/nodebb" }, first.Load());
            Assert.Empty(second.Load());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("relative/nodebb")]
    [InlineData("/")]
    [InlineData("/srv/nodebb/../other")]
    [InlineData("/srv/nodebb\\other")]
    public void RejectsUnsafePaths(string path)
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new NodeBbInstallationStore(Guid.NewGuid(), root);
            Assert.Throws<ArgumentException>(() => store.Add(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsEmptyProfileId()
    {
        Assert.Throws<ArgumentException>(() => new NodeBbInstallationStore(Guid.Empty));
    }

    [Fact]
    public void RejectsCorruptSavedPathList()
    {
        var root = CreateTempDirectory();
        var profileId = Guid.NewGuid();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, profileId.ToString("N") + ".json"), "[\"relative/path\"]");
            var store = new NodeBbInstallationStore(profileId, root);
            Assert.Throws<ArgumentException>(() => store.Load());
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "NodeBBInstallationStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
