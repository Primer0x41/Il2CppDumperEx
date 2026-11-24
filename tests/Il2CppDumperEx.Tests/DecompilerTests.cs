using Il2CppDumper;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class DecompilerTests
{
    [Fact]
    public void HeaderConstants_ContainExpectedDefinitions()
    {
        Assert.Contains("Il2CppType", HeaderConstants.GenericHeader);
        Assert.Contains("struct VirtualInvokeData", HeaderConstants.GenericHeader);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("PRN", "_PRN")]
    [InlineData("AUX", "_AUX")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("NormalName", "NormalName")]
    [InlineData("Invalid:Name?Test*", "Invalid_Name_Test_")]
    public void SanitizeFileName_HandlesReservedAndInvalidNames(string input, string expected)
    {
        string actual = Il2CppDecompiler.SanitizeFileName(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NamespaceSplitting_FiltersEmptyAndObfuscatedSegments()
    {
        string ns = "MyGame..Subsystem...Core";
        var parts = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, parts.Length);
        Assert.Equal("MyGame", parts[0]);
        Assert.Equal("Subsystem", parts[1]);
        Assert.Equal("Core", parts[2]);

        string emptyNs = "....";
        var emptyParts = emptyNs.Split('.', StringSplitOptions.RemoveEmptyEntries);
        Assert.Empty(emptyParts);
    }
}
