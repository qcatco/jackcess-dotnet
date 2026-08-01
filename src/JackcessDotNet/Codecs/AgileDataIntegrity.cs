using System.Security.Cryptography;

namespace JackcessDotNet;

/// <summary>
/// The Agile-encryption data-integrity hash — MS-OFFCRYPTO §2.3.4.14, the <c>&lt;dataIntegrity&gt;</c>
/// element's <c>encryptedHmacKey</c> and <c>encryptedHmacValue</c>.
/// <para>
/// A random HMAC key is encrypted under the file's own key value, the content is HMAC'd with it,
/// and that HMAC is encrypted the same way. Each of the two ciphertexts uses an initialisation
/// vector derived from the key-data salt plus a block key defined by the spec, which is what keeps
/// the two from being interchangeable.
/// </para>
/// <para>
/// <b>Verified by round-trip only.</b> Confirming the values against Microsoft's requires either an
/// Agile-encrypted file that already carries a <c>&lt;dataIntegrity&gt;</c> element to recompute —
/// none is present in either test corpus on this machine — or opening a file written here in real
/// Access, which the ACE and DAO engines available here cannot stand in for. The transforms below
/// follow the specification and are self-consistent; that is a weaker claim than the rest of this
/// library's crypto, and deliberately stated as such.
/// </para>
/// </summary>
public static class AgileDataIntegrity
{
    /// <summary>Block key for the HMAC key's IV (MS-OFFCRYPTO §2.3.4.14).</summary>
    private static readonly byte[] HmacKeyBlock   = { 0x5f, 0xb2, 0xad, 0x01, 0x0c, 0xb9, 0xe1, 0xf6 };

    /// <summary>Block key for the HMAC value's IV (MS-OFFCRYPTO §2.3.4.14).</summary>
    private static readonly byte[] HmacValueBlock = { 0xa0, 0x67, 0x7f, 0x02, 0xb2, 0x2c, 0x84, 0x33 };

    /// <summary>The two ciphertexts that make up a <c>&lt;dataIntegrity&gt;</c> element.</summary>
    /// <param name="EncryptedHmacKey">The HMAC key, encrypted under the file's key value.</param>
    /// <param name="EncryptedHmacValue">The content's HMAC, encrypted the same way.</param>
    public readonly record struct Values(byte[] EncryptedHmacKey, byte[] EncryptedHmacValue)
    {
        /// <summary>The pair as the base64 the descriptor XML carries.</summary>
        public (string HmacKey, string HmacValue) ToBase64()
            => (Convert.ToBase64String(EncryptedHmacKey), Convert.ToBase64String(EncryptedHmacValue));
    }

    /// <summary>
    /// Produces a fresh data-integrity pair over <paramref name="content"/>.
    /// </summary>
    /// <param name="hashAlgorithm">The <c>keyData</c> hash algorithm, e.g. <c>SHA512</c>.</param>
    /// <param name="keySalt">The <c>keyData</c> salt value, which seeds both IVs.</param>
    /// <param name="blockSize">The <c>keyData</c> block size, which both IVs are cut to.</param>
    /// <param name="keyValue">The file's decrypted key value — the AES key for both ciphertexts.</param>
    /// <param name="content">
    /// The bytes the HMAC covers. Which bytes those are is the caller's to decide and not something
    /// this type can know: the specification defines the hash over an OOXML package's encrypted
    /// stream, and an Access database has no such stream.
    /// </param>
    public static Values Compute(
        string hashAlgorithm, byte[] keySalt, int blockSize, byte[] keyValue, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(hashAlgorithm);
        ArgumentNullException.ThrowIfNull(keySalt);
        ArgumentNullException.ThrowIfNull(keyValue);

        byte[] hmacKey = RandomNumberGenerator.GetBytes(HashSize(hashAlgorithm));
        return Encrypt(hashAlgorithm, keySalt, blockSize, keyValue, content, hmacKey);
    }

    /// <summary>
    /// The same as <see cref="Compute"/> with the HMAC key supplied rather than generated, so a
    /// test can produce a known answer. Callers writing a file want <see cref="Compute"/>: reusing
    /// an HMAC key across saves would leak that two files share one.
    /// </summary>
    public static Values ComputeWithKey(
        string hashAlgorithm, byte[] keySalt, int blockSize, byte[] keyValue,
        ReadOnlySpan<byte> content, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(hmacKey);
        return Encrypt(hashAlgorithm, keySalt, blockSize, keyValue, content, hmacKey);
    }

    /// <summary>
    /// Recomputes the HMAC over <paramref name="content"/> and compares it with the stored one.
    /// </summary>
    /// <returns>
    /// True when they agree. False when they do not, and when either ciphertext is malformed —
    /// a hash that cannot be read is a failed integrity check, not an error to raise.
    /// </returns>
    public static bool Verify(
        string hashAlgorithm, byte[] keySalt, int blockSize, byte[] keyValue,
        ReadOnlySpan<byte> content, byte[] encryptedHmacKey, byte[] encryptedHmacValue)
    {
        ArgumentNullException.ThrowIfNull(hashAlgorithm);
        ArgumentNullException.ThrowIfNull(keySalt);
        ArgumentNullException.ThrowIfNull(keyValue);
        if (encryptedHmacKey is null || encryptedHmacValue is null) return false;

        try
        {
            int hashSize = HashSize(hashAlgorithm);

            byte[] hmacKey = Truncate(
                AesCbcDecrypt(keyValue, Iv(hashAlgorithm, keySalt, blockSize, HmacKeyBlock), encryptedHmacKey),
                hashSize);

            byte[] storedHmac = Truncate(
                AesCbcDecrypt(keyValue, Iv(hashAlgorithm, keySalt, blockSize, HmacValueBlock), encryptedHmacValue),
                hashSize);

            byte[] actualHmac = Hmac(hashAlgorithm, hmacKey, content);

            // Fixed-time: an integrity check that leaks where two hashes diverge invites forgery
            // one byte at a time.
            return CryptographicOperations.FixedTimeEquals(storedHmac, actualHmac);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static Values Encrypt(
        string hashAlgorithm, byte[] keySalt, int blockSize, byte[] keyValue,
        ReadOnlySpan<byte> content, byte[] hmacKey)
    {
        byte[] hmacValue = Hmac(hashAlgorithm, hmacKey, content);

        return new Values(
            AesCbcEncrypt(keyValue, Iv(hashAlgorithm, keySalt, blockSize, HmacKeyBlock),   Pad(hmacKey,   blockSize)),
            AesCbcEncrypt(keyValue, Iv(hashAlgorithm, keySalt, blockSize, HmacValueBlock), Pad(hmacValue, blockSize)));
    }

    /// <summary>H(salt ‖ blockKey), cut or padded to the cipher's block size.</summary>
    private static byte[] Iv(string hashAlgorithm, byte[] keySalt, int blockSize, byte[] blockKey)
    {
        using var hash = Create(hashAlgorithm);
        hash.AppendData(keySalt);
        hash.AppendData(blockKey);
        return Fit(hash.GetHashAndReset(), blockSize, 0x36);
    }

    private static byte[] Hmac(string hashAlgorithm, byte[] key, ReadOnlySpan<byte> content)
        => hashAlgorithm.ToUpperInvariant().Replace("-", "") switch
        {
            "SHA512" => HMACSHA512.HashData(key, content),
            "SHA384" => HMACSHA384.HashData(key, content),
            "SHA256" => HMACSHA256.HashData(key, content),
            "SHA1"   => HMACSHA1.HashData(key, content),
            _ => throw new NotSupportedException(
                     $"Data-integrity hashing with '{hashAlgorithm}' is not supported; " +
                     "the Agile schemes this codec reads use SHA1, SHA256, SHA384 or SHA512."),
        };

    private static IncrementalHash Create(string hashAlgorithm)
        => IncrementalHash.CreateHash(hashAlgorithm.ToUpperInvariant().Replace("-", "") switch
        {
            "SHA512" => HashAlgorithmName.SHA512,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA1"   => HashAlgorithmName.SHA1,
            _ => throw new NotSupportedException($"Unsupported data-integrity hash '{hashAlgorithm}'."),
        });

    private static int HashSize(string hashAlgorithm)
        => hashAlgorithm.ToUpperInvariant().Replace("-", "") switch
        {
            "SHA512" => 64,
            "SHA384" => 48,
            "SHA256" => 32,
            "SHA1"   => 20,
            _ => throw new NotSupportedException($"Unsupported data-integrity hash '{hashAlgorithm}'."),
        };

    /// <summary>Pads to a whole number of cipher blocks with 0x00, as Agile does.</summary>
    private static byte[] Pad(byte[] value, int blockSize)
    {
        if (blockSize <= 0 || value.Length % blockSize == 0) return value;

        var padded = new byte[(value.Length / blockSize + 1) * blockSize];
        Array.Copy(value, padded, value.Length);
        return padded;
    }

    private static byte[] Truncate(byte[] value, int length)
        => value.Length <= length ? value : value[..length];

    /// <summary>Cuts a hash to <paramref name="length"/>, or pads it with <paramref name="fill"/>.</summary>
    private static byte[] Fit(byte[] hash, int length, byte fill)
    {
        if (hash.Length == length) return hash;

        var result = new byte[length];
        if (hash.Length > length)
        {
            Array.Copy(hash, result, length);
            return result;
        }

        Array.Copy(hash, result, hash.Length);
        for (int i = hash.Length; i < length; i++) result[i] = fill;
        return result;
    }

    private static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV  = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        return aes.EncryptCbc(plain, iv, PaddingMode.None);
    }

    private static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] cipher)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV  = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        return aes.DecryptCbc(cipher, iv, PaddingMode.None);
    }
}
