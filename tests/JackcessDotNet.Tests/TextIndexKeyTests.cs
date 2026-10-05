using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The key a text value gets in an index must be the one Access writes, byte for
/// byte, or Access cannot find the row (or the table, when the value is a table
/// name in MSysObjects' ParentIdName index). IndexKeys/general-legacy-ace16.tsv
/// holds the keys ACE 16 wrote for 1,863 values, ascending and descending: every
/// Latin-1 character alone and in three settings, the characters Access sorts as
/// two or three letters, a sample of the rest of the BMP, supplementary
/// characters, and names. Each value was indexed by ACE and its entries read back.
/// </summary>
public sealed class TextIndexKeyTests
{
    [Fact]
    public void Every_value_encodes_as_ACE_16_wrote_it()
    {
        var failures = new List<string>();
        int checkedValues = 0;
        foreach (var (value, codePoints, ascending, descending) in GoldenKeys())
        {
            checkedValues++;
            string asc  = "7F" + Hex(GeneralLegacyIndexCodes.EncodeText(value, isAscending: true));
            string desc = "80" + Hex(GeneralLegacyIndexCodes.EncodeText(value, isAscending: false));
            if (asc != ascending)   failures.Add($"{codePoints} ascending: ACE {ascending}, engine {asc}");
            if (desc != descending) failures.Add($"{codePoints} descending: ACE {descending}, engine {desc}");
        }

        Assert.Equal(1863, checkedValues);
        Assert.True(failures.Count == 0,
            $"{failures.Count} keys differ from ACE's:\n" + string.Join("\n", failures.Take(25)));
    }

    // The codes files drop a code's leading zero: '#' is "SC", 0x0C. Read as no
    // bytes at all, '#', '$' and the space vanished from keys.
    [Fact]
    public void A_code_written_without_its_leading_zero_gets_it_back()
    {
        Assert.Equal("0C0100", Hex(GeneralLegacyIndexCodes.EncodeText("#", isAscending: true)));
        Assert.Equal("4A07510102020E00", Hex(GeneralLegacyIndexCodes.EncodeText("a é", isAscending: true)));
    }

    // U+3041's line is "Z7F02,,1": no extra codes. Counted as a character with an
    // empty run, it moved the accent of every later character one place left.
    [Fact]
    public void A_character_without_extra_codes_adds_none()
    {
        Assert.Equal("7F025101020E0101A0FF0280FF8000",
            Hex(GeneralLegacyIndexCodes.EncodeText("\u3041é", isAscending: true)));
    }

    // Access sorts Æ as AE and ﬃ as ffi, and gives each letter its own place in the
    // accents that follow: é after "aÆ" is at the fourth place, not the third.
    [Fact]
    public void A_character_sorted_as_several_letters_takes_a_place_for_each()
    {
        Assert.Equal("4A4A5151010202020E00", Hex(GeneralLegacyIndexCodes.EncodeText("aÆé", isAscending: true)));
        Assert.Equal("4A5353595101020202020E00", Hex(GeneralLegacyIndexCodes.EncodeText("a\uFB03é", isAscending: true)));
    }

    // ACE leaves a surrogate pair out of the key altogether.
    [Fact]
    public void A_surrogate_pair_is_left_out()
    {
        Assert.Equal(
            Hex(GeneralLegacyIndexCodes.EncodeText("aé", isAscending: true)),
            Hex(GeneralLegacyIndexCodes.EncodeText("a\U0001F600é", isAscending: true)));
    }

    private static IEnumerable<(string Value, string CodePoints, string Ascending, string Descending)> GoldenKeys()
    {
        using var stream = typeof(TextIndexKeyTests).Assembly
            .GetManifestResourceStream("JackcessDotNet.Tests.general-legacy-ace16.tsv")
            ?? throw new InvalidOperationException("The golden keys resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.Split('\t');
            var value = new StringBuilder();
            foreach (string cp in fields[0].Split(' '))
                value.Append(char.ConvertFromUtf32(int.Parse(cp, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            yield return (value.ToString(), fields[0], fields[1], fields[2]);
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}
