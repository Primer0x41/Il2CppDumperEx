using System;
using System.IO;
using System.Linq;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class DummyAssemblyTests
{
    [Fact]
    public void MyAssemblyResolver_AddSearchDirectory_RegistersSearchPath()
    {
        var resolver = new MyAssemblyResolver();
        string testPath = Path.Combine(Path.GetTempPath(), "dummy_search_dir");
        resolver.AddSearchDirectory(testPath);

        var searchDirs = resolver.GetSearchDirectories();
        Assert.Contains(testPath, searchDirs);
    }

    [Fact]
    public void ProcessCurrentDirectory_IsNotMutated()
    {
        string before = Directory.GetCurrentDirectory();
        var resolver = new MyAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetTempPath());
        string after = Directory.GetCurrentDirectory();

        Assert.Equal(before, after);
    }

    [Fact]
    public void CreateIl2CppDummyDll_GeneratesExpectedAttributeTypesAndFields()
    {
        var asm = DummyAssemblyGenerator.CreateIl2CppDummyDll();
        Assert.NotNull(asm);
        Assert.Equal("Il2CppDummyDll", asm.Name.Name);

        var module = asm.MainModule;
        Assert.NotNull(module);

        var addressAttr = module.Types.FirstOrDefault(t => t.FullName == "Il2CppDummyDll.AddressAttribute");
        Assert.NotNull(addressAttr);
        Assert.Equal("System.Attribute", addressAttr.BaseType.FullName);
        Assert.Contains(addressAttr.Fields, f => f.Name == "RVA" && f.FieldType.FullName == "System.String");
        Assert.Contains(addressAttr.Fields, f => f.Name == "Offset" && f.FieldType.FullName == "System.String");
        Assert.Contains(addressAttr.Fields, f => f.Name == "VA" && f.FieldType.FullName == "System.String");
        Assert.Contains(addressAttr.Fields, f => f.Name == "Slot" && f.FieldType.FullName == "System.String");

        var attrAttr = module.Types.FirstOrDefault(t => t.FullName == "Il2CppDummyDll.AttributeAttribute");
        Assert.NotNull(attrAttr);
        Assert.Contains(attrAttr.Fields, f => f.Name == "Name" && f.FieldType.FullName == "System.String");
        Assert.Contains(attrAttr.Fields, f => f.Name == "RVA" && f.FieldType.FullName == "System.String");
        Assert.Contains(attrAttr.Fields, f => f.Name == "Offset" && f.FieldType.FullName == "System.String");

        var fieldOffsetAttr = module.Types.FirstOrDefault(t => t.FullName == "Il2CppDummyDll.FieldOffsetAttribute");
        Assert.NotNull(fieldOffsetAttr);
        Assert.Contains(fieldOffsetAttr.Fields, f => f.Name == "Offset" && f.FieldType.FullName == "System.String");

        var metadataOffsetAttr = module.Types.FirstOrDefault(t => t.FullName == "Il2CppDummyDll.MetadataOffsetAttribute");
        Assert.NotNull(metadataOffsetAttr);
        Assert.Contains(metadataOffsetAttr.Fields, f => f.Name == "Offset" && f.FieldType.FullName == "System.String");

        var tokenAttr = module.Types.FirstOrDefault(t => t.FullName == "Il2CppDummyDll.TokenAttribute");
        Assert.NotNull(tokenAttr);
        Assert.Contains(tokenAttr.Fields, f => f.Name == "Token" && f.FieldType.FullName == "System.String");
    }
}
