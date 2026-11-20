using System.Security.Cryptography;
using System.Text;

namespace NodeBB.SSHManager.Services;

/// <summary>
/// Streaming authenticated backup encryption: AES-256-CBC with encrypt-then-HMAC-SHA256.
/// The password is never persisted. Format: 8-byte magic, 16-byte salt, 16-byte IV, ciphertext, 32-byte HMAC.
/// </summary>
public static class BackupEncryptionService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NBBKENC1");
    private const int SaltLength = 16;
    private const int IvLength = 16;
    private const int TagLength = 32;
    private const int HeaderLength = 8 + SaltLength + IvLength;
    private const int KeyLength = 64;
    private const int Iterations = 600_000;
    private const int BufferSize = 128 * 1024;

    public static async Task EncryptFileAsync(string sourcePath, string destinationPath, string password,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        var fullSource = Path.GetFullPath(sourcePath);
        var fullDestination = Path.GetFullPath(destinationPath);
        if (string.Equals(fullSource, fullDestination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("קובץ המקור והיעד חייבים להיות שונים.", nameof(destinationPath));
        if (!File.Exists(fullSource)) throw new FileNotFoundException("קובץ הגיבוי המקורי לא נמצא.", fullSource);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        salt.CopyTo(header, Magic.Length);
        iv.CopyTo(header, Magic.Length + SaltLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        var directory = Path.GetDirectoryName(fullDestination) ?? throw new InvalidOperationException("נתיב היעד אינו תקין.");
        Directory.CreateDirectory(directory);
        var cipherTemp = fullDestination + "." + Guid.NewGuid().ToString("N") + ".cipher.tmp";
        var outputTemp = fullDestination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var aesKey = key.AsSpan(0, 32).ToArray();
        byte[]? tag = null;

        try
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = iv;
                await using var input = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
                await using var cipherOutput = new FileStream(cipherTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true);
                await using (var crypto = new CryptoStream(cipherOutput, aes.CreateEncryptor(), CryptoStreamMode.Write, true))
                {
                    await input.CopyToAsync(crypto, BufferSize, cancellationToken);
                    crypto.FlushFinalBlock();
                }
                await cipherOutput.FlushAsync(cancellationToken);
            }

            tag = await ComputeTagAsync(header, cipherTemp, key.AsMemory(32, 32), cancellationToken);
            await using (var output = new FileStream(outputTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                await output.WriteAsync(header, cancellationToken);
                await using var cipherInput = new FileStream(cipherTemp, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
                await cipherInput.CopyToAsync(output, BufferSize, cancellationToken);
                await output.WriteAsync(tag, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(outputTemp, fullDestination, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            if (tag is not null) CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(key);
            try { if (File.Exists(cipherTemp)) File.Delete(cipherTemp); } catch { }
            try { if (File.Exists(outputTemp)) File.Delete(outputTemp); } catch { }
        }
    }

    public static async Task DecryptFileAsync(string sourcePath, string destinationPath, string password,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        var fullSource = Path.GetFullPath(sourcePath);
        var fullDestination = Path.GetFullPath(destinationPath);
        if (string.Equals(fullSource, fullDestination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("קובץ המקור והיעד חייבים להיות שונים.", nameof(destinationPath));
        if (!File.Exists(fullSource)) throw new FileNotFoundException("קובץ הגיבוי המוצפן לא נמצא.", fullSource);

        var directory = Path.GetDirectoryName(fullDestination) ?? throw new InvalidOperationException("נתיב היעד אינו תקין.");
        Directory.CreateDirectory(directory);
        var outputTemp = fullDestination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var key = Array.Empty<byte>();
        var aesKey = Array.Empty<byte>();
        byte[]? expectedTag = null;
        byte[]? storedTag = null;

        try
        {
            await using var input = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
            if (input.Length < HeaderLength + TagLength + 16)
                throw new InvalidDataException("קובץ הגיבוי קצר מדי או פגום.");

            var header = new byte[HeaderLength];
            await input.ReadExactlyAsync(header, cancellationToken);
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("סוג קובץ הגיבוי אינו מוכר.");

            var salt = header.AsSpan(Magic.Length, SaltLength).ToArray();
            var iv = header.AsSpan(Magic.Length + SaltLength, IvLength).ToArray();
            var cipherLength = input.Length - HeaderLength - TagLength;
            if (cipherLength <= 0) throw new InvalidDataException("אין נתונים בקובץ הגיבוי.");

            key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
            expectedTag = await ComputeTagFromStreamAsync(header, input, cipherLength, key.AsMemory(32, 32), cancellationToken);
            storedTag = new byte[TagLength];
            await input.ReadExactlyAsync(storedTag, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(expectedTag, storedTag))
                throw new CryptographicException("הסיסמה שגויה או שקובץ הגיבוי שונה או פגום.");

            input.Position = HeaderLength;
            using var aes = Aes.Create();
            aesKey = key.AsSpan(0, 32).ToArray();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = aesKey;
            aes.IV = iv;
            await using (var limited = new LimitedReadStream(input, cipherLength))
            await using (var crypto = new CryptoStream(limited, aes.CreateDecryptor(), CryptoStreamMode.Read))
            await using (var output = new FileStream(outputTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                await crypto.CopyToAsync(output, BufferSize, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(outputTemp, fullDestination, true);
        }
        finally
        {
            if (key.Length > 0) CryptographicOperations.ZeroMemory(key);
            if (aesKey.Length > 0) CryptographicOperations.ZeroMemory(aesKey);
            if (expectedTag is not null) CryptographicOperations.ZeroMemory(expectedTag);
            if (storedTag is not null) CryptographicOperations.ZeroMemory(storedTag);
            try { if (File.Exists(outputTemp)) File.Delete(outputTemp); } catch { }
        }
    }

    private static async Task<byte[]> ComputeTagAsync(byte[] header, string cipherPath, ReadOnlyMemory<byte> macKey,
        CancellationToken cancellationToken)
    {
        var macKeyBytes = macKey.ToArray();
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKeyBytes);
            hmac.AppendData(header);
            await using var input = new FileStream(cipherPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                hmac.AppendData(buffer.AsSpan(0, read));
            return hmac.GetHashAndReset();
        }
        finally { CryptographicOperations.ZeroMemory(macKeyBytes); }
    }

    private static async Task<byte[]> ComputeTagFromStreamAsync(byte[] header, Stream input, long cipherLength,
        ReadOnlyMemory<byte> macKey, CancellationToken cancellationToken)
    {
        var macKeyBytes = macKey.ToArray();
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKeyBytes);
            hmac.AppendData(header);
            var buffer = new byte[BufferSize];
            var remaining = cipherLength;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("קובץ הגיבוי נקטע לפני סופו.");
                hmac.AppendData(buffer.AsSpan(0, read));
                remaining -= read;
            }
            return hmac.GetHashAndReset();
        }
        finally { CryptographicOperations.ZeroMemory(macKeyBytes); }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new ArgumentException("סיסמת ההצפנה חייבת להכיל לפחות 12 תווים.", nameof(password));
    }

    private sealed class LimitedReadStream(Stream inner, long remaining) : Stream
    {
        private long _remaining = remaining;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
