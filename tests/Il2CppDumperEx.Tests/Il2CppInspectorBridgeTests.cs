using System;
using System.IO;
using System.Text.Json;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class Il2CppInspectorBridgeTests
{
    [Fact]
    public void ParseAddress_ParsesNumbersAndHexStrings()
    {
        using var doc = JsonDocument.Parse("""
        {
            "num": 4294967296,
            "hexWithPrefix": "0x1800ABCD",
            "hexNoPrefix": "1800ABCD",
            "invalid": "not-a-number"
        }
        """);

        var root = doc.RootElement;
        Assert.Equal(4294967296UL, Il2CppInspectorBridge.ParseAddress(root.GetProperty("num")));
        Assert.Equal(0x1800ABCDUL, Il2CppInspectorBridge.ParseAddress(root.GetProperty("hexWithPrefix")));
        Assert.Equal(0x1800ABCDUL, Il2CppInspectorBridge.ParseAddress(root.GetProperty("hexNoPrefix")));
        Assert.Equal(0UL, Il2CppInspectorBridge.ParseAddress(root.GetProperty("invalid")));
    }

    [Fact]
    public void TryReadAddresses_ParsesScriptJsonWithHexStrings()
    {
        string tmpFile = Path.Combine(Path.GetTempPath(), $"script_test_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmpFile, """
            {
                "codeRegistration": "0x14010000",
                "metadataRegistration": "0x14020000"
            }
            """);

            var result = Il2CppInspectorBridge.TryReadAddresses(tmpFile);
            Assert.NotNull(result);
            Assert.Equal(0x14010000UL, result.Value.codeReg);
            Assert.Equal(0x14020000UL, result.Value.metadataReg);
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [Fact]
    public void TryReadAddresses_ParsesNestedMetadataNumericValues()
    {
        string tmpFile = Path.Combine(Path.GetTempPath(), $"script_test_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmpFile, """
            {
                "metadata": {
                    "codeRegistration": 5000000,
                    "metadataRegistration": 6000000
                }
            }
            """);

            var result = Il2CppInspectorBridge.TryReadAddresses(tmpFile);
            Assert.NotNull(result);
            Assert.Equal(5000000UL, result.Value.codeReg);
            Assert.Equal(6000000UL, result.Value.metadataReg);
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [Fact]
    public void AutoDetect_RelativeFilenameWithoutDirectory_ReturnsNull()
    {
        // When il2cppPath is just a bare filename with no directory, it must not scan current working directory
        var result = Il2CppInspectorBridge.AutoDetect("GameAssembly.dll");
        Assert.Null(result);
    }
}
