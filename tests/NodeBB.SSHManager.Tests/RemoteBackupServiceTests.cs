using System;
using System.IO;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class RemoteBackupServiceTests
{
    [Fact]
    public async Task ValidateArchive_AcceptsValidTarGzipAndReportsContents()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "valid.tar.gz");
        try
        {
            await CreateArchiveAsync(path, new (string Name, string Contents)[]
            {
                ("config.json", "{\"database\":\"mongo\"}"),
                ("public/uploads/image.jpg", "image-data")
            });
            var result = await RemoteBackupService.ValidateArchiveAsync(path);
            Assert.Equal(2, result.EntryCount);
            Assert.True(result.ExpandedBytes > 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ValidateArchive_RejectsNonGzipAndTruncatedFiles()
    {
        var directory = CreateTempDirectory();
        var plain = Path.Combine(directory, "plain.tar.gz");
        var truncated = Path.Combine(directory, "truncated.tar.gz");
        try
        {
            await File.WriteAllTextAsync(plain, "not a gzip archive with enough bytes");
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteBackupService.ValidateArchiveAsync(plain));

            await CreateArchiveAsync(truncated, new (string Name, string Contents)[] { ("config.json", "{}") });
            var bytes = await File.ReadAllBytesAsync(truncated);
            await File.WriteAllBytesAsync(truncated, bytes[..^8]);
            await Assert.ThrowsAnyAsync<Exception>(() => RemoteBackupService.ValidateArchiveAsync(truncated));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ValidateArchive_RejectsEmptyArchive()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "empty.tar.gz");
        try
        {
            await CreateArchiveAsync(path);
            var result = await RemoteBackupService.ValidateArchiveAsync(path);
            Assert.Equal(0, result.EntryCount);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBRemoteBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task CreateArchiveAsync(string path, params (string Name, string Contents)[] entries)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var item in entries)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, item.Name)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(item.Contents))
            };
            writer.WriteEntry(entry);
        }
    }
}
