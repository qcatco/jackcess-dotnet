// BCL-surface compatibility helpers for the net472 target. Language-level
// polyfills (Index/Range, required members, init-only setters) come from the
// PolySharp source generator; this file covers missing runtime METHODS only.
using System.Text;

namespace JackcessDotNet.Util
{
    internal static class EncodingCompat
    {
#if NETFRAMEWORK || NETSTANDARD2_0
        // Encoding.Latin1 was added in .NET 5; 28591 is ISO-8859-1.
        internal static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
#else
        internal static readonly Encoding Latin1 = Encoding.Latin1;
#endif
    }
}

namespace JackcessDotNet
{
    using System;
    using System.Security.Cryptography;

    // One-shot hash statics (MD5.HashData etc.) arrived in .NET 5. A single
    // Create()-based implementation keeps net472 and net8 byte-identical.
    internal static class CryptoCompat
    {
        internal static byte[] Md5(ReadOnlySpan<byte> data) { using var h = MD5.Create(); return h.ComputeHash(data.ToArray()); }
        internal static byte[] Sha1(ReadOnlySpan<byte> data) { using var h = SHA1.Create(); return h.ComputeHash(data.ToArray()); }
        internal static byte[] Sha256(ReadOnlySpan<byte> data) { using var h = SHA256.Create(); return h.ComputeHash(data.ToArray()); }
        internal static byte[] Sha384(ReadOnlySpan<byte> data) { using var h = SHA384.Create(); return h.ComputeHash(data.ToArray()); }
        internal static byte[] Sha512(ReadOnlySpan<byte> data) { using var h = SHA512.Create(); return h.ComputeHash(data.ToArray()); }

        // Constant-time comparison (CryptographicOperations.FixedTimeEquals is .NET Core 2.1+).
        internal static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length) return false;
            var acc = 0;
            for (var i = 0; i < left.Length; i++) acc |= left[i] ^ right[i];
            return acc == 0;
        }
    }
}

namespace JackcessDotNet.Util
{
    using System;

    internal static class ArrayCompat
    {
        // Replaces C# range indexing on arrays (buf[a..b]), whose lowering needs
        // RuntimeHelpers.GetSubArray — a runtime member that cannot be polyfilled
        // on net472 (RuntimeHelpers already exists and can't be extended).
        internal static byte[] Slice(this byte[] source, int start, int endExclusive)
        {
            var result = new byte[endExclusive - start];
            Array.Copy(source, start, result, 0, result.Length);
            return result;
        }
    }
}

#if NETFRAMEWORK || NETSTANDARD2_0
namespace System.Buffers.Binary
{
    internal static class BinaryPrimitivesCompat
    {
        // BitConverter.Int32BitsToSingle/SingleToInt32Bits were added after
        // net472; reinterpret via byte round-trip (endianness-neutral here
        // because both sides use the platform representation).
        internal static float Int32BitsToSingle(int value)
            => BitConverter.ToSingle(BitConverter.GetBytes(value), 0);

        internal static int SingleToInt32Bits(float value)
            => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
    }
}

namespace System.Collections.Generic
{
    // KeyValuePair<,>.Deconstruct arrived in netstandard2.1; this extension
    // lets `foreach (var (k, v) in dict)` compile unchanged on net472.
    internal static class KeyValuePairCompatExtensions
    {
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }

    // Dictionary.GetValueOrDefault arrived in netstandard2.1 (CollectionExtensions).
    internal static class DictionaryCompatExtensions
    {
        public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key)
            => dictionary.TryGetValue(key, out var value) ? value : default;

        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
            => dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }
}

namespace System.IO
{
    // Stream.Read(Span<byte>) arrived in netstandard2.1; extension form lets
    // `fs.Read(spanBuffer)` compile unchanged on net472 (instance method wins on net8).
    internal static class StreamCompatExtensions
    {
        public static int Read(this Stream stream, Span<byte> buffer)
        {
            var tmp = new byte[buffer.Length];
            var read = stream.Read(tmp, 0, tmp.Length);
            new ReadOnlySpan<byte>(tmp, 0, read).CopyTo(buffer);
            return read;
        }
    }
}
#endif
