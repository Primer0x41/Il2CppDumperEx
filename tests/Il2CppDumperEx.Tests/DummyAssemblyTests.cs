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
}
