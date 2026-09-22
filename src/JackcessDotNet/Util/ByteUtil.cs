using System.Text;

namespace JackcessDotNet.Util;

/// <summary>
/// Low-level byte manipulation helpers for reading/writing Jet format data.
/// All Jet format values are little-endian.
/// </summary>
internal static class ByteUtil
{
    // ── Read helpers ─────────────────────────────────────────────────────────

    public static byte GetByte(byte[] buf, int offset) => buf[offset];

    public static short GetShort(byte[] buf, int offset)
        => (short)(buf[offset] | (buf[offset + 1] << 8));

    public static ushort GetUShort(byte[] buf, int offset)
        => (ushort)(buf[offset] | (buf[offset + 1] << 8));

    public static int GetInt(byte[] buf, int offset)
        => buf[offset]
         | (buf[offset + 1] << 8)
         | (buf[offset + 2] << 16)
         | (buf[offset + 3] << 24);

    public static uint GetUInt(byte[] buf, int offset)
        => (uint)(buf[offset]
                | (buf[offset + 1] << 8)
                | (buf[offset + 2] << 16)
                | (buf[offset + 3] << 24));

    public static long GetLong(byte[] buf, int offset)
        => (long)(uint)GetInt(buf, offset)
         | ((long)(uint)GetInt(buf, offset + 4) << 32);

    public static double GetDouble(byte[] buf, int offset)
        => BitConverter.Int64BitsToDouble(GetLong(buf, offset));

    public static float GetFloat(byte[] buf, int offset)
#if NETFRAMEWORK || NETSTANDARD2_0
        => System.Buffers.Binary.BinaryPrimitivesCompat.Int32BitsToSingle(GetInt(buf, offset));
#else
        => BitConverter.Int32BitsToSingle(GetInt(buf, offset));
#endif

    // ── Write helpers ─────────────────────────────────────────────────────────

    public static void PutByte(byte[] buf, int offset, byte value)
        => buf[offset] = value;

    public static void PutShort(byte[] buf, int offset, short value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void PutUShort(byte[] buf, int offset, ushort value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void PutInt(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    public static void PutUInt(byte[] buf, int offset, uint value)
        => PutInt(buf, offset, (int)value);

    public static void PutLong(byte[] buf, int offset, long value)
    {
        PutInt(buf, offset,     (int)(value & 0xFFFFFFFFL));
        PutInt(buf, offset + 4, (int)((value >> 32) & 0xFFFFFFFFL));
    }

    public static int Get3ByteInt(byte[] buf, int offset)
        => buf[offset]
         | (buf[offset + 1] << 8)
         | (buf[offset + 2] << 16);

    public static void Put3ByteInt(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
    }

    public static void PutDouble(byte[] buf, int offset, double value)
        => PutLong(buf, offset, BitConverter.DoubleToInt64Bits(value));

    public static void PutFloat(byte[] buf, int offset, float value)
#if NETFRAMEWORK || NETSTANDARD2_0
        => PutInt(buf, offset, System.Buffers.Binary.BinaryPrimitivesCompat.SingleToInt32Bits(value));
#else
        => PutInt(buf, offset, BitConverter.SingleToInt32Bits(value));
#endif

    public static void PutBytes(byte[] buf, int offset, byte[] src)
        => Array.Copy(src, 0, buf, offset, src.Length);

    public static void PutBytes(byte[] buf, int offset, byte[] src, int srcOffset, int length)
        => Array.Copy(src, srcOffset, buf, offset, length);

    // ── String helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a string as compressed or uncompressed UTF-16LE for Jet Text columns.
    /// Jet uses a simple compression: if all code points fit in the Latin-1 range (0x00-0xFF)
    /// and the first byte is 0xFF (compression marker), then single bytes are used.
    /// We always write uncompressed UTF-16LE prefixed with the 0xFF 0xFE BOM marker approach,
    /// but Jet Text fields use the 0xFF marker for compressed mode.
    /// </summary>
    public static byte[] EncodeText(string value)
    {
        // Try compressed encoding when all chars fit in Latin-1.
        // Access uses a 2-byte marker (0xFF 0xFE) so the decoder can distinguish
        // compressed text from uncompressed UTF-16LE. NUL is excluded: inside a
        // compressed stream 0x00 is the segment-mode toggle, not a character.
        bool canCompress = value.All(c => c >= 0x01 && c <= 0xFF);
        if (canCompress)
        {
            var bytes = new byte[value.Length + 2];
            bytes[0] = 0xFF;
            bytes[1] = 0xFE;
            for (int i = 0; i < value.Length; i++)
                bytes[i + 2] = (byte)value[i];
            return bytes;
        }
        return Encoding.Unicode.GetBytes(value);
    }

    /// <summary>
    /// Jet4 text decoder. The 0xFF 0xFE marker introduces Jet's compressed text
    /// format: the stream starts in compressed mode (one byte per char, high
    /// byte implicitly 0x00) and every 0x00 byte TOGGLES between compressed and
    /// uncompressed (UTF-16LE) segments. Real Access routinely writes mixed
    /// streams (e.g. an ASCII prefix compressed, then Japanese uncompressed),
    /// so decoding everything after the marker as Latin-1 corrupts non-Latin1
    /// text. Algorithm ported from Java Jackcess ColumnImpl.decodeTextValue.
    /// Text without the marker is plain UTF-16LE.
    /// </summary>
    public static string DecodeText(byte[] data, int offset, int length)
    {
        if (length == 0) return string.Empty;
        if (length >= 2 && data[offset] == 0xFF && data[offset + 1] == 0xFE)
        {
            var text = new StringBuilder(length);
            int end = offset + length;
            int segStart = offset + 2;
            int pos = segStart;
            bool inCompressedMode = true;
            while (pos < end)
            {
                if (data[pos] == 0x00)
                {
                    DecodeTextSegment(data, segStart, pos, inCompressedMode, text);
                    inCompressedMode = !inCompressedMode;
                    pos++;
                    segStart = pos;
                }
                else
                {
                    pos++;
                }
            }
            DecodeTextSegment(data, segStart, end, inCompressedMode, text);
            return text.ToString();
        }
        if (data[offset] == 0xFF && data[offset + 1] != 0x00)
        {
            // Legacy single-byte marker (text written by earlier versions of
            // this library). The second-byte guard keeps genuine UTF-16LE that
            // begins with U+00FF ('ÿ' encodes as FF 00) out of this branch.
            return EncodingCompat.Latin1.GetString(data, offset + 1, length - 1);
        }
        return Encoding.Unicode.GetString(data, offset, length);
    }

    private static void DecodeTextSegment(byte[] data, int start, int end, bool compressed, StringBuilder text)
    {
        if (end <= start) return;
        if (compressed)
        {
            // Each byte is a UTF-16 code unit with an implicit 0x00 high byte.
            for (int i = start; i < end; i++)
                text.Append((char)data[i]);
        }
        else
        {
            text.Append(Encoding.Unicode.GetString(data, start, end - start));
        }
    }

    /// <summary>
    /// Format-aware text decoder.
    /// Jet3 text fields are stored directly as the database charset (cp1252) — no compression marker.
    /// Jet4 text fields use UTF-16LE with an optional 0xFF compression marker for Latin-1 strings.
    /// </summary>
    public static string DecodeText(byte[] data, int offset, int length, JetFormat format)
    {
        if (length == 0) return string.Empty;
        if (format.Version == JetVersion.Jet3)
            return format.TextEncoding.GetString(data, offset, length);
        return DecodeText(data, offset, length);
    }

    // ── Misc ──────────────────────────────────────────────────────────────────

    public static void Fill(byte[] buf, int offset, int count, byte value)
    {
        for (int i = 0; i < count; i++)
            buf[offset + i] = value;
    }

    public static void Clear(byte[] buf, int offset, int count)
        => Fill(buf, offset, count, 0x00);
}
