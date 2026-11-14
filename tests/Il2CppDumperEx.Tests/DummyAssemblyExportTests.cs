using System;
using System.IO;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class DummyAssemblyExportTests
{
    [Fact]
    public void AssemblyDefinition_RoundTrip_WritesValidAssemblyBytes()
    {
        var assemblyName = new AssemblyNameDefinition("MockUnityAssembly", new Version(1, 0, 0, 0));
        using var assembly = AssemblyDefinition.CreateAssembly(assemblyName, "MockUnityAssembly.dll", ModuleKind.Dll);

        var module = assembly.MainModule;
        var typeDef = new TypeDefinition("GameNamespace", "PlayerController", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(typeDef);

        var methodDef = new MethodDefinition("Update", MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Void);
        typeDef.Methods.Add(methodDef);

        using var ms = new MemoryStream();
        assembly.Write(ms);
        byte[] bytes = ms.ToArray();

        Assert.NotEmpty(bytes);

        // Verify Mono.Cecil can read the emitted assembly bytes back
        using var readMs = new MemoryStream(bytes);
        using var roundTripAssembly = AssemblyDefinition.ReadAssembly(readMs);
        Assert.Equal("MockUnityAssembly", roundTripAssembly.Name.Name);
        Assert.Contains(roundTripAssembly.MainModule.Types, t => t.FullName == "GameNamespace.PlayerController");
    }

    [Fact]
    public void WorkingDirectory_RemainsUnchanged_DuringAssemblyExportWorkflows()
    {
        string currentDirBefore = Directory.GetCurrentDirectory();
        string tempDir = Path.Combine(Path.GetTempPath(), "dummy_export_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var assemblyName = new AssemblyNameDefinition("TestAssembly", new Version(1, 0));
            using var assembly = AssemblyDefinition.CreateAssembly(assemblyName, "TestAssembly.dll", ModuleKind.Dll);

            string dummyDir = Path.Combine(tempDir, "DummyDll");
            Directory.CreateDirectory(dummyDir);
            string targetDllPath = Path.Combine(dummyDir, "TestAssembly.dll");
            assembly.Write(targetDllPath);

            Assert.True(File.Exists(targetDllPath));
            string currentDirAfter = Directory.GetCurrentDirectory();
            Assert.Equal(currentDirBefore, currentDirAfter);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
