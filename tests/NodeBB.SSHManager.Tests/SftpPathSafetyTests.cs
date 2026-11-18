using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Tests;

public sealed class SftpPathSafetyTests
{
    [Theory]
    [InlineData("/", "file.txt", "/file.txt")]
    [InlineData("/var/www", "config.json", "/var/www/config.json")]
    [InlineData("/var//www/", "with spaces.txt", "/var/www/with spaces.txt")]
    [InlineData("/srv/nodebb", "file'name.txt", "/srv/nodebb/file'name.txt")]
    [InlineData("/srv/nodebb", "literal\\backslash.txt", "/srv/nodebb/literal\\backslash.txt")]
    [InlineData("/srv/nodebb", "   ", "/srv/nodebb/   ")]
    public void Combine_ReturnsNormalizedRemotePath(string directory, string name, string expected)
    {
        Assert.Equal(expected, SftpPathSafety.Combine(directory, name));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("folder/file.txt")]
    [InlineData("bad\nname")]
    public void ValidateName_RejectsUnsafeNames(string name)
    {
        Assert.Throws<ArgumentException>(() => SftpPathSafety.ValidateName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("../etc")]
    [InlineData("/var/../etc")]
    [InlineData("/./etc")]
    [InlineData("/var/\nname")]
    public void NormalizeAbsolutePath_RejectsUnsafePaths(string path)
    {
        Assert.Throws<ArgumentException>(() => SftpPathSafety.NormalizeAbsolutePath(path));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("////", "/")]
    [InlineData("/var//www/", "/var/www")]
    [InlineData("/home/user", "/home/user")]
    public void NormalizeAbsolutePath_CanonicalizesPath(string path, string expected)
    {
        Assert.Equal(expected, SftpPathSafety.NormalizeAbsolutePath(path));
    }
}
