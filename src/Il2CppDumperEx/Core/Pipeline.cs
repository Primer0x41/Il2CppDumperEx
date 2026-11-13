using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Il2CppDumper;

namespace Il2CppDumperEx.Core;

public class PipelineException : Exception
{
    public PipelineException(string message) : base(message) { }
    public PipelineException(string message, Exception inner) : base(message, inner) { }
}

public class InvalidMetadataException : PipelineException
{
    public InvalidMetadataException(string message) : base(message) { }
}

public record DumpResult(
    string GameName,
    string GameRoot,
    string? UnityVersion,
    int MetadataVersionUsed,
    bool NeededDecryption,
    double DecryptDurationSeconds,
    string OutputDir,
    string Timestamp
);

public class DumpPipeline
{
    public string GamePath { get; }
    public string? OutputDir { get; }
    public int? ForceVersion { get; }
    public Config DumperConfig { get; }
    public Action<int, int, string>? ProgressCallback { get; }
    public Action<string>? LogCallback { get; }
    public CancellationToken CancellationToken { get; }

    public DumpPipeline(
        string gamePath,
        string? outputDir = null,
        int? forceVersion = null,
        Config? dumperConfig = null,
        Action<int, int, string>? progressCallback = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
    {
        GamePath = gamePath;
        OutputDir = outputDir;
        ForceVersion = forceVersion;
        DumperConfig = dumperConfig ?? new Config
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
        ProgressCallback = progressCallback;
        LogCallback = logCallback;
        CancellationToken = cancellationToken;
    }

    private void Log(string message) => LogCallback?.Invoke(message);
    private void ReportProgress(int step, int total, string status) => ProgressCallback?.Invoke(step, total, status);

    public DumpResult Run()
    {
        const int totalSteps = 5;
        CancellationToken.ThrowIfCancellationRequested();

        ReportProgress(1, totalSteps, "Discovering game files...");
        Log($"[*] Locating game from: {GamePath}");
        var game = GameDiscovery.DiscoverGame(GamePath);

        Log($"[*] Game Name: {game.GameName}");
        Log($"[*] Binary: {game.BinaryPath}");
        Log($"[*] Metadata: {game.MetadataPath}");
        Log($"[*] Unity Version: {game.UnityVersion ?? "Unknown"}");

        string targetOutputDir = !string.IsNullOrEmpty(OutputDir)
            ? Path.GetFullPath(OutputDir)
            : Path.GetFullPath(Path.Combine("output", game.GameName));

        CancellationToken.ThrowIfCancellationRequested();
        ReportProgress(2, totalSteps, "Checking metadata encryption status...");
        bool isEncrypted = MetadataValidator.IsEncryptedMetadata(game.MetadataPath);
        Log($"[*] Metadata encryption status: {(isEncrypted ? "NEP2 PROTECTED" : "UNENCRYPTED")}");

        List<int> candidateVersions = ForceVersion.HasValue
            ? [ForceVersion.Value]
            : MetadataValidator.GetCandidateVersions(game.UnityVersion);

        int initialVersion = candidateVersions[0];

        Directory.CreateDirectory(targetOutputDir);

        CancellationToken.ThrowIfCancellationRequested();
        ReportProgress(3, totalSteps, isEncrypted ? "Decrypting NEP2 metadata in memory..." : "Reading metadata into memory...");

        byte[] workingMetadata;
        double decryptDuration = 0;

        if (isEncrypted)
        {
            var sw = Stopwatch.StartNew();
            byte[] rawEncrypted = File.ReadAllBytes(game.MetadataPath);
            workingMetadata = Nep2Decryptor.DecryptBytes(rawEncrypted, (uint)initialVersion);
            sw.Stop();
            decryptDuration = sw.Elapsed.TotalSeconds;
            Log($"[*] In-memory decryption completed in {decryptDuration:F4}s ({workingMetadata.Length:N0} bytes)");
        }
        else
        {
            Log("[*] Loading unencrypted metadata...");
            workingMetadata = File.ReadAllBytes(game.MetadataPath);
        }

        CancellationToken.ThrowIfCancellationRequested();
        ReportProgress(4, totalSteps, "Validating metadata header and string heap...");

        int? validatedVersion = null;
        List<string> validationFailures = [];

        foreach (int verCandidate in candidateVersions)
        {
            CancellationToken.ThrowIfCancellationRequested();
            MetadataValidator.PatchMetadataVersion(workingMetadata, verCandidate);
            var (valid, reason) = MetadataValidator.ValidateMetadata(workingMetadata, verCandidate);

            if (valid)
            {
                validatedVersion = verCandidate;
                Log($"[*] Metadata successfully validated for version {validatedVersion} ({reason})");
                break;
            }
            else
            {
                validationFailures.Add($"v{verCandidate}: {reason}");
            }
        }

        if (!validatedVersion.HasValue)
        {
            string sampleErrors = string.Join("; ", validationFailures.Take(3));
            throw new InvalidMetadataException(
                $"Decrypted metadata failed header/string-heap validation for all candidates: [{sampleErrors}]. " +
                "The encryption scheme may have changed or the file is corrupted.");
        }

        int successVersion = validatedVersion.Value;

        CancellationToken.ThrowIfCancellationRequested();
        ReportProgress(5, totalSteps, "Initializing decompilation engine...");

        bool dumpSuccess = DumperEngine.Run(
            il2cppPath: game.BinaryPath,
            metadataBytes: workingMetadata,
            outputDir: targetOutputDir,
            config: DumperConfig,
            logCallback: Log,
            stageCallback: stage =>
            {
                CancellationToken.ThrowIfCancellationRequested();
                ReportProgress(5, totalSteps, stage);
            }
        );

        if (!dumpSuccess)
        {
            throw new PipelineException("In-process Il2CppDumper could not locate symbols or failed to complete.");
        }

        Log("[*] Verifying generated dump files...");
        foreach (var file in Directory.GetFiles(targetOutputDir))
        {
            var fi = new FileInfo(file);
            Log($"    - {fi.Name} ({fi.Length:N0} bytes)");
        }
        foreach (var dir in Directory.GetDirectories(targetOutputDir))
        {
            var di = new DirectoryInfo(dir);
            int count = di.GetFiles().Length;
            Log($"    - {di.Name}/ ({count} files)");
        }

        ReportProgress(5, totalSteps, "Dump completed successfully!");
        return new DumpResult(
            GameName: game.GameName,
            GameRoot: game.GameRoot,
            UnityVersion: game.UnityVersion,
            MetadataVersionUsed: successVersion,
            NeededDecryption: isEncrypted,
            DecryptDurationSeconds: decryptDuration,
            OutputDir: targetOutputDir,
            Timestamp: DateTime.UtcNow.ToString("o")
        );
    }
}
