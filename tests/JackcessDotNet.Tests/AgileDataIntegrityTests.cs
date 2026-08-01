using System.Security.Cryptography;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The Agile data-integrity hash (MS-OFFCRYPTO §2.3.4.14).
/// <para>
/// These check the transforms against each other and against the properties the specification
/// requires — they do not check the result against Microsoft's, which would need an Agile-encrypted
/// file carrying a <c>&lt;dataIntegrity&gt;</c> element to recompute, or real Access to open one
/// written here. Neither is available on this machine, and that limit is recorded on
/// <see cref="AgileDataIntegrity"/> rather than papered over.
/// </para>
/// </summary>
public sealed class AgileDataIntegrityTests
{
    private const string Hash = "SHA512";
    private const int BlockSize = 16;

    private static byte[] KeySalt  => Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static byte[] KeyValue => Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
    private static byte[] Content  => Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public void AComputedHash_VerifiesAgainstTheContentItCovers()
    {
        var values = AgileDataIntegrity.Compute(Hash, KeySalt, BlockSize, KeyValue, Content);

        Assert.True(AgileDataIntegrity.Verify(
            Hash, KeySalt, BlockSize, KeyValue, Content,
            values.EncryptedHmacKey, values.EncryptedHmacValue));
    }

    [Fact]
    public void AlteredContent_FailsVerification()
    {
        var values = AgileDataIntegrity.Compute(Hash, KeySalt, BlockSize, KeyValue, Content);

        byte[] tampered = Content;
        tampered[2500] ^= 0x01;   // one bit, in the middle

        Assert.False(AgileDataIntegrity.Verify(
            Hash, KeySalt, BlockSize, KeyValue, tampered,
            values.EncryptedHmacKey, values.EncryptedHmacValue));
    }

    [Fact]
    public void TheWrongKeyValue_FailsVerification()
    {
        var values = AgileDataIntegrity.Compute(Hash, KeySalt, BlockSize, KeyValue, Content);

        byte[] otherKey = KeyValue;
        otherKey[0] ^= 0xFF;

        Assert.False(AgileDataIntegrity.Verify(
            Hash, KeySalt, BlockSize, otherKey, Content,
            values.EncryptedHmacKey, values.EncryptedHmacValue));
    }

    /// <summary>
    /// The two ciphertexts take initialisation vectors from different block keys, so swapping them
    /// has to fail. Deriving both from one block key would still round-trip, and this is what
    /// catches that.
    /// </summary>
    [Fact]
    public void SwappingTheTwoCiphertexts_FailsVerification()
    {
        var values = AgileDataIntegrity.Compute(Hash, KeySalt, BlockSize, KeyValue, Content);

        Assert.False(AgileDataIntegrity.Verify(
            Hash, KeySalt, BlockSize, KeyValue, Content,
            values.EncryptedHmacValue, values.EncryptedHmacKey));
    }

    [Fact]
    public void TheSameKeyAndContent_GiveTheSameHashEveryTime()
    {
        byte[] hmacKey = RandomNumberGenerator.GetBytes(64);

        var first  = AgileDataIntegrity.ComputeWithKey(Hash, KeySalt, BlockSize, KeyValue, Content, hmacKey);
        var second = AgileDataIntegrity.ComputeWithKey(Hash, KeySalt, BlockSize, KeyValue, Content, hmacKey);

        Assert.Equal(first.EncryptedHmacKey,   second.EncryptedHmacKey);
        Assert.Equal(first.EncryptedHmacValue, second.EncryptedHmacValue);
    }

    /// <summary>Each save invents a new HMAC key, so two saves of one file must not match.</summary>
    [Fact]
    public void ComputeGeneratesAFreshKeyEachTime()
    {
        var first  = AgileDataIntegrity.Compute(Hash, KeySalt, BlockSize, KeyValue, Content);
        var second = AgileDataIntegrity.Compute(Hash, KeySalt, BlockSize, KeyValue, Content);

        Assert.NotEqual(first.EncryptedHmacKey,   second.EncryptedHmacKey);
        Assert.NotEqual(first.EncryptedHmacValue, second.EncryptedHmacValue);
    }

    [Theory]
    [InlineData("SHA512", 64)]
    [InlineData("SHA384", 48)]
    [InlineData("SHA256", 32)]
    [InlineData("SHA1",   20)]
    public void EveryHashTheAgileSchemesUse_RoundTrips(string hashAlgorithm, int hashSize)
    {
        var values = AgileDataIntegrity.Compute(hashAlgorithm, KeySalt, BlockSize, KeyValue, Content);

        // The ciphertext covers the hash padded up to whole cipher blocks.
        int expected = (hashSize + BlockSize - 1) / BlockSize * BlockSize;
        Assert.Equal(expected, values.EncryptedHmacValue.Length);

        Assert.True(AgileDataIntegrity.Verify(
            hashAlgorithm, KeySalt, BlockSize, KeyValue, Content,
            values.EncryptedHmacKey, values.EncryptedHmacValue));
    }

    [Fact]
    public void MalformedCiphertext_ReportsAFailedCheckRatherThanThrowing()
    {
        Assert.False(AgileDataIntegrity.Verify(
            Hash, KeySalt, BlockSize, KeyValue, Content,
            encryptedHmacKey: new byte[] { 1, 2, 3 },       // not a whole cipher block
            encryptedHmacValue: new byte[] { 4, 5, 6 }));

        Assert.False(AgileDataIntegrity.Verify(
            Hash, KeySalt, BlockSize, KeyValue, Content, null!, null!));
    }

    [Fact]
    public void EmptyContent_IsStillCoveredRatherThanSkipped()
    {
        var values = AgileDataIntegrity.Compute(Hash, KeySalt, BlockSize, KeyValue, Array.Empty<byte>());

        Assert.True(AgileDataIntegrity.Verify(
            Hash, KeySalt, BlockSize, KeyValue, Array.Empty<byte>(),
            values.EncryptedHmacKey, values.EncryptedHmacValue));

        Assert.False(AgileDataIntegrity.Verify(
            Hash, KeySalt, BlockSize, KeyValue, new byte[] { 0 },
            values.EncryptedHmacKey, values.EncryptedHmacValue));
    }

    [Fact]
    public void AnUnsupportedHash_SaysSo()
    {
        Assert.Throws<NotSupportedException>(
            () => AgileDataIntegrity.Compute("MD5", KeySalt, BlockSize, KeyValue, Content));
    }

    /// <summary>The base64 the descriptor XML carries, rather than callers encoding it themselves.</summary>
    [Fact]
    public void ValuesConvertToTheBase64TheDescriptorCarries()
    {
        var values = AgileDataIntegrity.Compute(Hash, KeySalt, BlockSize, KeyValue, Content);
        var (key, value) = values.ToBase64();

        Assert.Equal(values.EncryptedHmacKey,   Convert.FromBase64String(key));
        Assert.Equal(values.EncryptedHmacValue, Convert.FromBase64String(value));
    }
}
