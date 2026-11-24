using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Il2CppDumper;
using Il2CppDumperEx.Core;
#if WINDOWS
using Il2CppDumperEx.UI;
#endif

namespace Il2CppDumperEx;

internal static class Program
{
#if WINDOWS
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    private const int AttachParentProcess = -1;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private static bool _allocatedOwnConsole = false;
#endif

    [STAThread]
    private static int Main(string[] args)
    {
#if WINDOWS
        if (args.Length > 0 && args[0].Equals("--gui", StringComparison.OrdinalIgnoreCase))
        {
            string? initialPath = args.Length > 1 ? args[1] : null;
            var app = new App();
            app.InitializeComponent();
            return app.Run(new MainWindow(initialPath));
        }

        if (args.Length > 0)
        {
            SetupCliConsole();
            int exitCode = RunCli(args);
            if (_allocatedOwnConsole)
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                try { Console.ReadKey(true); } catch { }
            }
            return exitCode;
        }

        var appGui = new App();
        appGui.InitializeComponent();
        return appGui.Run(new MainWindow());
#else
        if (args.Length > 0 && args[0].Equals("--gui", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Error: GUI mode is only supported on Windows (WPF). CLI mode is fully supported on Linux and macOS.");
            return 1;
        }

        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        return RunCli(args);
#endif
    }

#if WINDOWS
    private static void SetupCliConsole()
    {
        if (Console.IsOutputRedirected && Console.OpenStandardOutput() != Stream.Null)
        {
            var stdOut = Console.OpenStandardOutput();
            Console.SetOut(new StreamWriter(stdOut, Console.OutputEncoding) { AutoFlush = true });

            if (Console.IsErrorRedirected && Console.OpenStandardError() != Stream.Null)
            {
                var stdErr = Console.OpenStandardError();
                Console.SetError(new StreamWriter(stdErr, Console.OutputEncoding) { AutoFlush = true });
            }
            return;
        }

        if (AttachConsole(AttachParentProcess))
        {
            RedirectConsoleStreams();
            return;
        }

        FreeConsole();
        if (AllocConsole())
        {
            _allocatedOwnConsole = true;
            RedirectConsoleStreams();
        }
    }

    private static void RedirectConsoleStreams()
    {
        try
        {
            var hConOut = CreateFile("CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (hConOut != IntPtr.Zero && hConOut != new IntPtr(-1))
            {
                var safeOut = new SafeFileHandle(hConOut, ownsHandle: true);
                var fsOut = new FileStream(safeOut, FileAccess.Write);
                Console.SetOut(new StreamWriter(fsOut, Console.OutputEncoding) { AutoFlush = true });
            }

            var hConIn = CreateFile("CONIN$", GenericRead, FileShareRead, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (hConIn != IntPtr.Zero && hConIn != new IntPtr(-1))
            {
                var safeIn = new SafeFileHandle(hConIn, ownsHandle: true);
                var fsIn = new FileStream(safeIn, FileAccess.Read);
                Console.SetIn(new StreamReader(fsIn, Console.InputEncoding));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RedirectConsoleStreams] Exception: {ex.Message}");
        }
    }
#endif

    internal static int RunCli(string[] args)
    {
        string? gamePath = null;
        string? outDir = null;
        int? forceVersion = null;
        string? decryptOutPath = null;
        bool decryptOnly = false;
        bool verbose = false;

        var dumperConfig = new Config
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

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help" or "/?" or "-?")
            {
                PrintHelp();
                return 0;
            }

            if (arg is "-v" or "--verbose")
            {
                verbose = true;
            }
            else if (arg is "--decrypt-only" or "-d")
            {
                decryptOnly = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    decryptOutPath = args[++i];
                }
            }
            else if (arg is "--out" && i + 1 < args.Length)
            {
                outDir = args[++i];
            }
            else if (arg is "--force-version" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int fv))
                {
                    forceVersion = fv;
                }
                else
                {
                    Console.Error.WriteLine("[!] Invalid argument for --force-version: must be an integer.");
                    return 1;
                }
            }
            else if (arg is "--no-cs")
            {
                dumperConfig.DumpCs = false;
            }
            else if (arg is "--no-dummy-dll")
            {
                dumperConfig.GenerateDummyDll = false;
            }
            else if (arg is "--no-struct")
            {
                dumperConfig.GenerateStruct = false;
            }
            else if (arg is "--no-method")
            {
                dumperConfig.DumpMethod = false;
            }
            else if (arg is "--no-field")
            {
                dumperConfig.DumpField = false;
            }
            else if (arg is "--no-property")
            {
                dumperConfig.DumpProperty = false;
            }
            else if (arg is "--no-attribute")
            {
                dumperConfig.DumpAttribute = false;
            }
            else if (arg is "--no-field-offset")
            {
                dumperConfig.DumpFieldOffset = false;
            }
            else if (arg is "--no-method-offset")
            {
                dumperConfig.DumpMethodOffset = false;
            }
            else if (arg is "--no-typedef-index")
            {
                dumperConfig.DumpTypeDefIndex = false;
            }
            else if (arg is "--no-token")
            {
                dumperConfig.DummyDllAddToken = false;
            }
            else if (arg is "--unified-json")
            {
                dumperConfig.GenerateUnifiedJson = true;
            }
            else if (arg is "--gen-mod-project")
            {
                dumperConfig.GenerateModProject = true;
            }
            else if (arg is "--split-mode" && i + 1 < args.Length)
            {
                string modeStr = args[++i].ToLowerInvariant();
                dumperConfig.SplitMode = modeStr switch
                {
                    "assembly" or "asm" => DecompileSplitMode.Assembly,
                    "namespace" or "ns" => DecompileSplitMode.Namespace,
                    _ => DecompileSplitMode.None
                };
            }
            else if (!arg.StartsWith('-') && gamePath == null)
            {
                gamePath = arg;
            }
        }

        if (string.IsNullOrEmpty(gamePath))
        {
            Console.Error.WriteLine("[!] Error: No game path or metadata path specified.");
            PrintHelp();
            return 1;
        }

        if (decryptOnly)
        {
            try
            {
                string metadataFile;
                if (File.Exists(gamePath) && (gamePath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || gamePath.EndsWith(".dec", StringComparison.OrdinalIgnoreCase)))
                {
                    metadataFile = gamePath;
                }
                else
                {
                    var discovered = GameDiscovery.DiscoverGame(gamePath);
                    metadataFile = discovered.MetadataPath;
                }

                string? effectiveOut = !string.IsNullOrEmpty(decryptOutPath) ? decryptOutPath : outDir;
                string destination = !string.IsNullOrEmpty(effectiveOut)
                    ? (Directory.Exists(effectiveOut) 
                        ? Path.Combine(effectiveOut, "global-metadata.dat") 
                        : effectiveOut)
                    : (File.Exists(gamePath) 
                        ? Path.Combine(Path.GetDirectoryName(gamePath) ?? ".", "global-metadata-decrypted.dat")
                        : Path.Combine(gamePath, "global-metadata-decrypted.dat"));

                Console.WriteLine("==================================================");
                Console.WriteLine("Il2CppDumperEx - Standalone Metadata Decryption");
                Console.WriteLine("==================================================");

                var decResult = DumpPipeline.DecryptMetadataFile(
                    inputPath: metadataFile,
                    outputPath: destination,
                    forceVersion: forceVersion,
                    logCallback: Console.WriteLine
                );

                Console.WriteLine();
                Console.WriteLine("==================================================");
                Console.WriteLine("Decryption & Verification Complete!");
                Console.WriteLine($"Input:       {decResult.InputPath}");
                Console.WriteLine($"Output:      {decResult.OutputPath}");
                Console.WriteLine($"Status:      {(decResult.NeededDecryption ? "NEP2 Decrypted" : "Already Decrypted")}");
                Console.WriteLine($"Version:     v{decResult.ValidatedVersion}");
                Console.WriteLine($"Strings:     {decResult.SaneStringCount:N0} valid strings verified in heap");
                Console.WriteLine($"Duration:    {decResult.DurationSeconds:F4}s");
                Console.WriteLine("==================================================");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"[!] Decryption Error: {ex.Message}");
                if (verbose)
                {
                    Console.Error.WriteLine(ex.StackTrace);
                }
                return 1;
            }
        }

        try
        {
            var pipeline = new DumpPipeline(
                gamePath: gamePath,
                outputDir: outDir,
                forceVersion: forceVersion,
                dumperConfig: dumperConfig,
                progressCallback: (step, total, status) =>
                {
                    Console.WriteLine($"[{step}/{total}] {status}");
                },
                logCallback: msg =>
                {
                    if (verbose || msg.StartsWith("[*]") || msg.StartsWith("[!]"))
                    {
                        Console.WriteLine(msg);
                    }
                }
            );

            var result = pipeline.Run();
            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("                DUMP COMPLETED                    ");
            Console.WriteLine("==================================================");
            Console.WriteLine($"Game Name:        {result.GameName}");
            Console.WriteLine($"Unity Version:    {result.UnityVersion ?? "Unknown"}");
            Console.WriteLine($"Metadata Version: {result.MetadataVersionUsed}");
            Console.WriteLine($"Decryption:       {(result.NeededDecryption ? "NEP2 Decrypted" : "Unencrypted")}");
            Console.WriteLine($"Output Directory: {result.OutputDir}");
            Console.WriteLine("==================================================");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[!] Error: {ex.Message}");
            if (verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"Il2CppDumperEx - Extended Unity IL2CPP Reverse Engineering Suite

Usage:
  Il2CppDumperEx <game_path_or_metadata> [options]

Arguments:
  game_path             Path to game folder, <Game>_Data directory, .exe, or global-metadata.dat

Options:
  -d, --decrypt-only [out] Decrypt and verify global-metadata.dat without running decompilation
  --out <dir>           Custom output directory (default: ./output/<GameName>)
  --force-version <N>   Force specific metadata version (e.g. 29, 31)
  -v, --verbose         Print detailed diagnostic output
  -h, --help            Show this help message

Dump Options (All enabled by default):
  --split-mode <mode>   C# dump output mode: none (dump.cs), assembly (by dll), namespace (folder tree)
  --unified-json        Generate unified metadata.json symbol map for disassemblers
  --gen-mod-project     Generate a ready-to-build .NET plugin C# project referencing DummyDll
  --no-cs               Disable C# decompilation (dump.cs / CSharp folder)
  --no-dummy-dll        Disable generation of DummyDll assemblies
  --no-struct           Disable generation of struct definitions (il2cpp.h, script.json, stringliteral.json)
  --no-method           Disable dumping method signatures
  --no-field            Disable dumping fields
  --no-property         Disable dumping properties
  --no-attribute        Disable dumping attributes
  --no-field-offset     Disable dumping field offsets
  --no-method-offset    Disable dumping method offsets
  --no-typedef-index    Disable dumping TypeDef indices
  --no-token            Disable adding metadata tokens to DummyDlls
");
    }
}
