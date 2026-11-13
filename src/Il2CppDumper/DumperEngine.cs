#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Il2CppDumper;

public class DumperException : Exception
{
    public DumperException(string message) : base(message) { }
    public DumperException(string message, Exception inner) : base(message, inner) { }
}

public static class DumperEngine
{
    public static bool Run(
        string il2cppPath,
        byte[] metadataBytes,
        string outputDir,
        Config? config = null,
        Action<string>? logCallback = null,
        Action<string>? stageCallback = null)
    {
        config ??= new Config
        {
            DumpMethod = true,
            DumpField = true,
            DumpProperty = true,
            DumpAttribute = true,
            DumpFieldOffset = true,
            DumpMethodOffset = true,
            DumpTypeDefIndex = true,
            GenerateDummyDll = true,
            GenerateStruct = true,
            DummyDllAddToken = true,
            RequireAnyKey = false,
            ForceDump = false,
            NoRedirectedPointer = false
        };

        outputDir = Path.GetFullPath(outputDir);
        if (!outputDir.EndsWith(Path.DirectorySeparatorChar) && !outputDir.EndsWith(Path.AltDirectorySeparatorChar))
        {
            outputDir += Path.DirectorySeparatorChar;
        }

        var originalOut = Console.Out;
        var originalError = Console.Error;

        try
        {
            if (logCallback != null)
            {
                var writer = new CallbackTextWriter(logCallback, originalOut);
                Console.SetOut(writer);
                Console.SetError(writer);
            }

            Console.WriteLine("[*] Starting in-process Il2CppDumper...");
            stageCallback?.Invoke("Initializing binary & metadata...");
            if (!Init(il2cppPath, metadataBytes, config, stageCallback, out var metadata, out var il2Cpp))
            {
                return false;
            }

            ExecuteDump(metadata, il2Cpp, outputDir, config, stageCallback);
            return true;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static bool Init(string il2cppPath, byte[] metadataBytes, Config config, Action<string>? stageCallback, out Metadata metadata, out Il2Cpp il2Cpp)
    {
        Console.WriteLine("[*] Initializing metadata in memory...");
        metadata = new Metadata(new MemoryStream(metadataBytes));
        Console.WriteLine($"[*] Metadata Version: {metadata.Version}");

        Console.WriteLine($"[*] Initializing il2cpp binary: {il2cppPath}");
        var il2cppBytes = File.ReadAllBytes(il2cppPath);
        var il2cppMagic = BitConverter.ToUInt32(il2cppBytes, 0);
        var il2CppMemory = new MemoryStream(il2cppBytes);

        switch (il2cppMagic)
        {
            default:
                throw new NotSupportedException($"ERROR: il2cpp file format not supported (magic: 0x{il2cppMagic:X8}).");
            case 0x6D736100:
                var web = new WebAssembly(il2CppMemory);
                il2Cpp = web.CreateMemory();
                break;
            case 0x304F534E:
                var nso = new NSO(il2CppMemory);
                il2Cpp = nso.UnCompress();
                break;
            case 0x905A4D: // PE
                il2Cpp = new PE(il2CppMemory);
                break;
            case 0x464c457f: // ELF
                if (il2cppBytes[4] == 2) // ELF64
                {
                    il2Cpp = new Elf64(il2CppMemory);
                }
                else
                {
                    il2Cpp = new Elf(il2CppMemory);
                }
                break;
            case 0xFEEDFACF: // 64bit Mach-O
                il2Cpp = new Macho64(il2CppMemory);
                break;
            case 0xFEEDFACE: // 32bit Mach-O
                il2Cpp = new Macho(il2CppMemory);
                break;
        }

        var version = config.ForceIl2CppVersion ? config.ForceVersion : metadata.Version;
        il2Cpp.SetProperties(version, metadata.metadataUsagesCount, metadata);
        Console.WriteLine($"[*] Il2Cpp Version: {il2Cpp.Version}");

        if (config.ForceDump || il2Cpp.CheckDump())
        {
            if (il2Cpp is ElfBase elf)
            {
                Console.WriteLine("[*] Detected potential memory dump file. Using default base address.");
                il2Cpp.IsDumped = true;
                if (!config.NoRedirectedPointer)
                {
                    elf.Reload();
                }
            }
            else
            {
                il2Cpp.IsDumped = true;
            }
        }

        Console.WriteLine("[*] Searching symbols...");
        stageCallback?.Invoke("Searching symbols...");
        var flag = il2Cpp.PlusSearch(metadata.methodDefs.Count(x => x.methodIndex >= 0), metadata.typeDefs.Length, metadata.imageDefs.Length);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!flag && il2Cpp is PE)
            {
                Console.WriteLine("[*] Trying custom PE loader...");
                il2Cpp = PELoader.Load(il2cppPath);
                il2Cpp.SetProperties(version, metadata.metadataUsagesCount, metadata);
                flag = il2Cpp.PlusSearch(metadata.methodDefs.Count(x => x.methodIndex >= 0), metadata.typeDefs.Length, metadata.imageDefs.Length);
            }
        }

        if (!flag)
        {
            flag = il2Cpp.Search();
        }
        if (!flag)
        {
            flag = il2Cpp.SymbolSearch();
        }
        if (!flag)
        {
            var bridgeResult = Il2CppInspectorBridge.AutoDetect(il2cppPath);
            if (bridgeResult.HasValue)
            {
                var (codeRegistration, metadataRegistration) = bridgeResult.Value;
                il2Cpp.Init(codeRegistration, metadataRegistration);
                flag = true;
            }
        }

        if (!flag)
        {
            Console.WriteLine("[!] Auto mode could not locate CodeRegistration / MetadataRegistration.");
            return false;
        }

        if (il2Cpp.Version >= 27 && il2Cpp.IsDumped)
        {
            var typeDef = metadata.typeDefs[0];
            var il2CppType = il2Cpp.types[typeDef.byvalTypeIndex];
            var typeDefinitionsOffset = metadata.Version >= 38
                ? (ulong)metadata.header.typeDefinitions.offset
                : metadata.header.typeDefinitionsOffset;
            metadata.ImageBase = il2CppType.data.typeHandle - typeDefinitionsOffset;
        }

        return true;
    }

    private static void ExecuteDump(Metadata metadata, Il2Cpp il2Cpp, string outputDir, Config config, Action<string>? stageCallback = null)
    {
        outputDir = Path.GetFullPath(outputDir);
        if (!outputDir.EndsWith(Path.DirectorySeparatorChar) && !outputDir.EndsWith(Path.AltDirectorySeparatorChar))
        {
            outputDir += Path.DirectorySeparatorChar;
        }

        Directory.CreateDirectory(outputDir);
        Console.WriteLine("[*] Decompiling and dumping...");
        stageCallback?.Invoke("Decompiling classes and methods...");
        var executor = new Il2CppExecutor(metadata, il2Cpp);
        var decompiler = new Il2CppDecompiler(executor);
        decompiler.Decompile(config, outputDir);
        Console.WriteLine("[*] Decompilation finished.");

        if (config.GenerateStruct)
        {
            Console.WriteLine("[*] Generating struct definitions (il2cpp.h, script.json, stringliteral.json)...");
            stageCallback?.Invoke("Generating struct definitions (il2cpp.h, script.json)...");
            try
            {
                var scriptGenerator = new StructGenerator(executor);
                scriptGenerator.WriteScript(outputDir);
                Console.WriteLine("[*] Struct definitions written.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[!] Error generating structs: {e.Message}");
                throw new DumperException($"Failed to generate struct definitions (il2cpp.h, script.json, stringliteral.json): {e.Message}", e);
            }
        }

        if (config.GenerateDummyDll)
        {
            Console.WriteLine("[*] Generating dummy assemblies...");
            stageCallback?.Invoke("Generating dummy assemblies...");
            try
            {
                DummyAssemblyExporter.Export(executor, outputDir, config.DummyDllAddToken);
                Console.WriteLine("[*] Dummy assemblies written.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[!] Error generating dummy DLLs: {e.Message}");
                throw new DumperException($"Failed to generate dummy DLLs: {e.Message}", e);
            }
        }
    }

    private sealed class CallbackTextWriter : TextWriter
    {
        private readonly Action<string> _callback;
        private readonly TextWriter _passthrough;
        [ThreadStatic]
        private static bool _isWriting;

        public CallbackTextWriter(Action<string> callback, TextWriter passthrough)
        {
            _callback = callback;
            _passthrough = passthrough;
        }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (value == null) return;
            if (_isWriting)
            {
                _passthrough.WriteLine(value);
                return;
            }

            try
            {
                _isWriting = true;
                _callback(value);
            }
            finally
            {
                _isWriting = false;
            }
        }

        public override void Write(char value)
        {
            _passthrough.Write(value);
        }
    }
}
