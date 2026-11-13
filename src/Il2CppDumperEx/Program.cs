using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Il2CppDumper;
using Il2CppDumperEx.Core;
using Il2CppDumperEx.UI;

namespace Il2CppDumperEx;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

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

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            SetupCliConsole();
            return RunCli(args);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run(new MainWindow());
    }

    private static void SetupCliConsole()
    {
        if (Console.IsOutputRedirected)
        {
            var stdOut = Console.OpenStandardOutput();
            if (stdOut != Stream.Null)
            {
                Console.SetOut(new StreamWriter(stdOut, Console.OutputEncoding) { AutoFlush = true });
            }
        }
        else if (AttachConsole(AttachParentProcess))
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
            }
            catch { }
        }

        if (Console.IsErrorRedirected)
        {
            var stdErr = Console.OpenStandardError();
            if (stdErr != Stream.Null)
            {
                Console.SetError(new StreamWriter(stdErr, Console.OutputEncoding) { AutoFlush = true });
            }
        }
        else
        {
            try
            {
                var hConErr = CreateFile("CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (hConErr != IntPtr.Zero && hConErr != new IntPtr(-1))
                {
                    var safeErr = new SafeFileHandle(hConErr, ownsHandle: true);
                    var fsErr = new FileStream(safeErr, FileAccess.Write);
                    Console.SetError(new StreamWriter(fsErr, Console.OutputEncoding) { AutoFlush = true });
                }
            }
            catch { }
        }
    }

    private static int RunCli(string[] args)
    {
        string? gamePath = null;
        string? outDir = null;
        int? forceVersion = null;
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

            if (arg is "--version" or "-V")
            {
                Console.WriteLine("Il2CppDumperEx v1.0.0 (Extended Edition with NEP2 Decryption)");
                return 0;
            }

            if (arg is "-v" or "--verbose")
            {
                verbose = true;
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
            else if (arg is "--backend" or "--dumper-repo" or "--dumper-choice" or "--dumper-commit")
            {
                i++;
            }
            else if (arg is "--update-tools" or "--keep-temp")
            {
            }
            else if (!arg.StartsWith('-') && gamePath == null)
            {
                gamePath = arg;
            }
        }

        if (string.IsNullOrEmpty(gamePath))
        {
            Console.Error.WriteLine("[!] Error: No game path specified.");
            PrintHelp();
            return 1;
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
  Il2CppDumperEx <game_path> [options]

Arguments:
  game_path             Path to game folder, <Game>_Data directory, or .exe

Options:
  --out <dir>           Custom output directory (default: ./output/<GameName>)
  --force-version <N>   Force specific metadata version (e.g. 29, 31)
  -v, --verbose         Print detailed diagnostic output
  --version             Show tool version
  -h, --help            Show this help message

Dump Options (All enabled by default):
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
