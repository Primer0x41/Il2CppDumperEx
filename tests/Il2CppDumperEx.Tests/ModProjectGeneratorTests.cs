using System.IO;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class ModProjectGeneratorTests
{
    [Fact]
    public void Generate_CreatesValidCsprojAndPluginFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Il2CppDumperEx_ModProjectTest_" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            var config = new Config { GenerateModProject = true };

            ModProjectGenerator.Generate(tempDir, config, "MyAwesomeGame");

            string modDir = Path.Combine(tempDir, "ModProject");
            Assert.True(Directory.Exists(modDir));

            string csprojPath = Path.Combine(modDir, "MyAwesomeGame.csproj");
            Assert.True(File.Exists(csprojPath));
            string csprojText = File.ReadAllText(csprojPath);
            Assert.Contains("<TargetFramework>netstandard2.1</TargetFramework>", csprojText);
            Assert.Contains("<AssemblyName>MyAwesomeGame</AssemblyName>", csprojText);
            Assert.Contains("../DummyDll/*.dll", csprojText);

            string pluginPath = Path.Combine(modDir, "Plugin.cs");
            Assert.True(File.Exists(pluginPath));
            string pluginText = File.ReadAllText(pluginPath);
            Assert.Contains("namespace MyAwesomeGame", pluginText);
            Assert.Contains("public static class Plugin", pluginText);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Generate_SanitizesSpecialCharactersInProjectName()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Il2CppDumperEx_ModProjectTest_" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            var config = new Config { GenerateModProject = true };

            ModProjectGenerator.Generate(tempDir, config, "123-Game@Name.Test!");

            string modDir = Path.Combine(tempDir, "ModProject");
            Assert.True(Directory.Exists(modDir));

            string csprojPath = Path.Combine(modDir, "_123_Game_Name_Test.csproj");
            Assert.True(File.Exists(csprojPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Generate_WhenOutputDirectoryIsFile_ThrowsIOException()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mod_invalid_dir_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tempFile, "not a directory");
        try
        {
            var config = new Config { GenerateModProject = true };
            Assert.Throws<IOException>(() => ModProjectGenerator.Generate(tempFile, config, "TestGame"));
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}
