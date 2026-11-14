using System;
using System.Linq;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class BoyerMooreHorspoolTests
{
    [Fact]
    public void Search_BytePattern_FindsOccurrences()
    {
        byte[] source = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x02, 0x03, 0x05 };
        byte[] pattern = new byte[] { 0x02, 0x03 };

        var matches = source.Search(pattern).ToList();

        Assert.Equal(2, matches.Count);
        Assert.Equal(1, matches[0]);
        Assert.Equal(4, matches[1]);
    }

    [Fact]
    public void Search_StringPattern_ExactHex_FindsOccurrences()
    {
        byte[] source = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xBB, 0xCC, 0xEE };
        string pattern = "BB CC";

        var matches = source.Search(pattern).ToList();

        Assert.Equal(2, matches.Count);
        Assert.Equal(1, matches[0]);
        Assert.Equal(4, matches[1]);
    }

    [Fact]
    public void Search_StringPattern_WithWildcard_FindsOccurrences()
    {
        byte[] source = new byte[] { 0x10, 0x20, 0x30, 0x00, 0x00, 0x10, 0xFF, 0x30 };
        string pattern = "10 ? 30";

        var matches = source.Search(pattern).ToList();

        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0]);
        Assert.Equal(5, matches[1]);
    }

    [Fact]
    public void Search_StringPattern_NotFound_ReturnsEmpty()
    {
        byte[] source = new byte[] { 0x01, 0x02, 0x03 };
        string pattern = "FF EE";

        var matches = source.Search(pattern).ToList();

        Assert.Empty(matches);
    }

    [Fact]
    public void Search_PatternLongerThanSource_ReturnsEmpty()
    {
        byte[] source = new byte[] { 0x01, 0x02 };
        string pattern = "01 02 03";

        var matches = source.Search(pattern).ToList();

        Assert.Empty(matches);
    }
}
