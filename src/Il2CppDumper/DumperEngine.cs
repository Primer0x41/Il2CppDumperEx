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

        byte[] il2cppBytes;
        MemoryStream il2CppMemory;

        if (ProcessMapReader.IsProcessMapFile(il2cppPath) && ProcessMapReader.TryReconstructElf(il2cppPath, out var reconElf, out ulong baseAddress))
        {
            Console.WriteLine($"[*] Reconstructed ELF from process memory map: base 0x{baseAddress:X}");
            il2cppBytes = reconElf!.ToArray();
            il2CppMemory = new MemoryStream(il2cppBytes);
        }
        else
        {
            il2cppBytes = File.ReadAllBytes(il2cppPath);
            il2CppMemory = new MemoryStream(il2cppBytes);
        }

        var il2cppMagic = BitConverter.ToUInt32(il2cppBytes, 0);

        switch (il2cppMagic)
        {
            default:
                throw new NotSupportedException($"ERROR: il2cpp file format not supported (magic: 0x{il2cppMagic:X8}).");
            case 0xBEBAFECA: // Mach-O Fat Binary (Little-Endian)
            case 0xCAFEBABE: // Mach-O Fat Binary (Big-Endian)
                var machoFat = new MachoFat(new MemoryStream(il2cppBytes));
                int selectedIndex = 0;
                for (int i = 0; i < machoFat.fats.Length; i++)
                {
                    if (machoFat.fats[i].magic == 0xFEEDFACF)
                    {
                        selectedIndex = i;
                        break;
                    }
                }
                Console.WriteLine($"[*] Detected Mach-O Universal Binary ({machoFat.fats.Length} slices). Automatically selected slice {selectedIndex + 1} ({(machoFat.fats[selectedIndex].magic == 0xFEEDFACF ? "64-bit" : "32-bit")}).");
                var slicedBytes = machoFat.GetMacho(selectedIndex);
                var slicedMemory = new MemoryStream(slicedBytes);
                if (machoFat.fats[selectedIndex].magic == 0xFEEDFACF)
                {
                    il2Cpp = new Macho64(slicedMemory);
                }
                else
                {
                    il2Cpp = new Macho(slicedMemory);
                }
                break;
            case 0x1D3D154F: // PlayStation SElf / FSelf (PS4 / PS5)
            case 0xEEF51454: // PlayStation PRX SElf / FSelf (PS4 / PS5)
                Console.WriteLine("[*] Detected Sony PlayStation SElf / FSelf container. Unwrapping internal ELF...");
                var unwrappedElf = SElf.UnwrapToElf(il2CppMemory);
                il2Cpp = new Elf64(unwrappedElf, isSElfContainer: true);
                break;
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

        if (!flag && il2Cpp is PE)
        {
            Console.WriteLine("[*] Trying custom PE loader...");
            il2Cpp = PELoader.Load(il2cppPath);
            il2Cpp.SetProperties(version, metadata.metadataUsagesCount, metadata);
            flag = il2Cpp.PlusSearch(metadata.methodDefs.Count(x => x.methodIndex >= 0), metadata.typeDefs.Length, metadata.imageDefs.Length);
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
        var executor = new Il2CppExecutor(metadata, il2Cpp);

        if (config.DumpCs)
        {
            Console.WriteLine("[*] Decompiling and dumping...");
            stageCallback?.Invoke("Decompiling classes and methods...");
            var decompiler = new Il2CppDecompiler(executor);
            decompiler.Decompile(config, outputDir);
            Console.WriteLine("[*] Decompilation finished.");
        }
        else
        {
            Console.WriteLine("[*] C# decompilation skipped (--no-cs specified).");
        }

        ScriptJson? scriptJson = null;
        var scriptGenerator = new StructGenerator(executor);

        if (config.GenerateStruct)
        {
            Console.WriteLine("[*] Generating struct definitions (il2cpp.h, script.json, stringliteral.json)...");
            stageCallback?.Invoke("Generating struct definitions (il2cpp.h, script.json)...");
            try
            {
                scriptJson = scriptGenerator.WriteScript(outputDir, config);
                Console.WriteLine("[*] Struct definitions written.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[!] Error generating structs: {e.Message}");
                throw new DumperException($"Failed to generate struct definitions (il2cpp.h, script.json, stringliteral.json): {e.Message}", e);
            }
        }
        else if (config.GenerateUnifiedJson)
        {
            scriptJson = scriptGenerator.GenerateScriptJson();
        }

        if (config.GenerateUnifiedJson && scriptJson != null)
        {
            Console.WriteLine("[*] Generating unified metadata.json...");
            stageCallback?.Invoke("Generating unified metadata.json...");
            try
            {
                UnifiedJsonExporter.Export(scriptJson, outputDir);
                Console.WriteLine("[*] Unified metadata.json written.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[!] Error generating unified metadata.json: {e.Message}");
                throw new DumperException($"Failed to generate unified metadata.json: {e.Message}", e);
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
