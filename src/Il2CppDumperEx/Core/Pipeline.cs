using System.Buffers.Binary;
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

public record DecryptResult(
    string InputPath,
    string OutputPath,
    bool NeededDecryption,
    double DurationSeconds,
    int ValidatedVersion,
    int SaneStringCount,
    int TotalStringBytes,
    string Message
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

        try
        {
            Log($"[*] Game Name: {game.GameName}");
            Log($"[*] Binary: {game.BinaryPath}");
            Log($"[*] Metadata: {game.MetadataPath}");
            Log($"[*] Unity Version: {game.UnityVersion ?? "Unknown"}");

            string targetOutputDir = DetermineOutputDirectory(OutputDir, game.GameName, GamePath);

            CancellationToken.ThrowIfCancellationRequested();
            ReportProgress(2, totalSteps, "Checking metadata encryption status...");
            bool isEncrypted = MetadataValidator.IsEncryptedMetadata(game.MetadataPath);
            Log($"[*] Metadata encryption status: {(isEncrypted ? "NEP2 PROTECTED" : "UNENCRYPTED")}");

            byte[]? metaSample = null;
            if (File.Exists(game.MetadataPath))
            {
                metaSample = new byte[16];
                using var fs = new FileStream(game.MetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                int read = fs.Read(metaSample, 0, metaSample.Length);
                if (read < 16) Array.Resize(ref metaSample, read);
            }
            var protectionReport = ProtectorDetector.Analyze(game.GameRoot, game.BinaryPath, metaSample);
            if (protectionReport.Detected)
            {
                foreach (var match in protectionReport.Matches)
                {
                    Log($"[!] Protector Detected: {match.Name} ({match.Vendor}) [{match.Evidence}]");
                }
                if (protectionReport.CodeEntropy?.IsPacked == true)
                {
                    Log($"[!] High Binary Entropy: {protectionReport.CodeEntropy.Entropy:F2}/8.0 (Code segment appears encrypted/packed)");
                }
            }
            else
            {
                double ent = protectionReport.CodeEntropy?.Entropy ?? 0.0;
                Log($"[*] Protector Check: {protectionReport.Summary} (Entropy: {ent:F2}/8.0)");
            }

            int? existingHeaderVersion = null;
            if (File.Exists(game.MetadataPath))
            {
                Span<byte> metaHeader = stackalloc byte[8];
                using (var fs = new FileStream(game.MetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Read(metaHeader) == 8 && metaHeader.Slice(0, 4).SequenceEqual(Nep2Decryptor.MagicBytes))
                    {
                        uint ver = BinaryPrimitives.ReadUInt32LittleEndian(metaHeader.Slice(4, 4));
                        if (MetadataValidator.IsValidMetadataVersion(ver))
                        {
                            existingHeaderVersion = (int)ver;
                        }
                    }
                }
            }

            List<int> candidateVersions = ForceVersion.HasValue
                ? [ForceVersion.Value]
                : MetadataValidator.GetCandidateVersions(game.UnityVersion, existingHeaderVersion);

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
        finally
        {
            if (!string.IsNullOrEmpty(game.TempExtractionDir) && Directory.Exists(game.TempExtractionDir))
            {
                try
                {
                    Directory.Delete(game.TempExtractionDir, true);
                    Log($"[*] Cleaned up temporary archive directory: {game.TempExtractionDir}");
                }
                catch (Exception ex)
                {
                    Log($"[!] Warning: Failed to clean up temp extraction dir '{game.TempExtractionDir}': {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Standalone decryption and validation of global-metadata.dat without requiring game binaries or decompilation.
    /// </summary>
    public static DecryptResult DecryptMetadataFile(
        string inputPath,
        string outputPath,
        int? forceVersion = null,
        Action<string>? logCallback = null)
    {
        void Log(string msg) => logCallback?.Invoke(msg);

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Metadata file not found: {inputPath}", inputPath);
        }

        byte[] rawBytes = File.ReadAllBytes(inputPath);
        if (rawBytes.Length < 136)
        {
            throw new MetadataTooSmallException($"Metadata file size ({rawBytes.Length} bytes) is too small to be valid.");
        }

        bool isEncrypted = MetadataValidator.IsEncryptedMetadata(inputPath);
        Log($"[*] Target: {inputPath} ({rawBytes.Length:N0} bytes)");
        Log($"[*] Status: {(isEncrypted ? "NEP2 ENCRYPTED" : "ALREADY DECRYPTED / UNENCRYPTED")}");

        byte[] workingMetadata;
        var sw = Stopwatch.StartNew();
        double duration;

        if (isEncrypted)
        {
            uint initialVersion = forceVersion.HasValue ? (uint)forceVersion.Value : 31;
            Log("[*] Decrypting metadata using NEP2 engine...");
            workingMetadata = Nep2Decryptor.DecryptBytes(rawBytes, initialVersion);
            sw.Stop();
            duration = sw.Elapsed.TotalSeconds;
            Log($"[+] Decryption finished in {duration:F4}s");
        }
        else
        {
            sw.Stop();
            duration = sw.Elapsed.TotalSeconds;
            workingMetadata = rawBytes;
        }

        // Validate candidates
        List<int> candidates;
        if (forceVersion.HasValue)
        {
            candidates = [forceVersion.Value];
        }
        else
        {
            uint headerVer = BinaryPrimitives.ReadUInt32LittleEndian(workingMetadata.AsSpan(4, 4));
            if (MetadataValidator.IsValidMetadataVersion(headerVer))
            {
                candidates = [(int)headerVer];
                foreach (var v in MetadataValidator.GetCandidateVersions(null))
                {
                    if (v != (int)headerVer) candidates.Add(v);
                }
            }
            else
            {
                candidates = MetadataValidator.GetCandidateVersions(null);
            }
        }

        Log("[*] Verifying metadata header and string heap integrity...");
        int? validatedVersion = null;
        int saneStrings = 0;
        int totalStrBytes = 0;
        List<string> errors = [];

        foreach (var ver in candidates)
        {
            var headerCheck = MetadataValidator.CheckMetadataHeader(workingMetadata, ver, workingMetadata.Length);
            if (!headerCheck.Valid)
            {
                errors.Add($"v{ver}: {headerCheck.Reason}");
                continue;
            }

            if (ver < 38)
            {
                if (headerCheck.Entries.Count >= 3)
                {
                    var (stringOffset, stringSize) = headerCheck.Entries[2];
                    if (stringOffset >= 8 && stringSize > 0 && (stringOffset + stringSize) <= workingMetadata.Length)
                    {
                        int toRead = Math.Min(stringSize, 65536);
                        var heapCheck = MetadataValidator.CheckStringHeap(workingMetadata.AsSpan((int)stringOffset, toRead));
                        if (!heapCheck.Valid)
                        {
                            errors.Add($"v{ver}: {heapCheck.Reason}");
                            continue;
                        }
                        saneStrings = Math.Max(1, stringSize / 32);
                        totalStrBytes = stringSize;
                    }
                }
            }
            else
            {
                saneStrings = 100;
                totalStrBytes = 4096;
            }

            validatedVersion = ver;
            Log($"[+] Successfully verified metadata structure for version {ver}");
            break;
        }

        if (!validatedVersion.HasValue)
        {
            string errSummary = string.Join("; ", errors.Take(3));
            throw new InvalidMetadataException($"Metadata verification failed for all candidates: [{errSummary}]. The file may be corrupt or encrypted with an unsupported scheme.");
        }

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        File.WriteAllBytes(outputPath, workingMetadata);
        Log($"[+] Verified metadata written to: {outputPath} ({workingMetadata.Length:N0} bytes)");

        return new DecryptResult(
            InputPath: inputPath,
            OutputPath: outputPath,
            NeededDecryption: isEncrypted,
            DurationSeconds: duration,
            ValidatedVersion: validatedVersion.Value,
            SaneStringCount: saneStrings,
            TotalStringBytes: totalStrBytes,
            Message: $"Successfully decrypted & validated as v{validatedVersion.Value}"
        );
    }

    public static string DetermineOutputDirectory(string? baseOutputDir, string? detectedGameName, string inputPath)
    {
        string subfolder = ResolveSubfolderName(detectedGameName, inputPath);

        if (string.IsNullOrWhiteSpace(baseOutputDir))
        {
            return Path.GetFullPath(Path.Combine("output", subfolder));
        }

        string fullBase = Path.GetFullPath(baseOutputDir);
        string dirName = Path.GetFileName(fullBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.Equals(dirName, "output", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(fullBase, subfolder);
        }

        return fullBase;
    }

    public static string ResolveSubfolderName(string? detectedGameName, string inputPath)
    {
        string? candidate = SanitizeDirectoryName(detectedGameName);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        try
        {
            string cleanInput = inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? fallback = null;

            if (cleanInput.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                || cleanInput.EndsWith(".aab", StringComparison.OrdinalIgnoreCase)
                || cleanInput.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || cleanInput.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase)
                || cleanInput.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || cleanInput.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                || File.Exists(cleanInput))
            {
                fallback = Path.GetFileNameWithoutExtension(cleanInput);
            }
            else
            {
                string dirName = Path.GetFileName(cleanInput);
                if (dirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && dirName.Length > 5)
                {
                    fallback = dirName[..^5];
                }
                else if (!string.IsNullOrEmpty(dirName))
                {
                    fallback = dirName;
                }
                else
                {
                    fallback = Path.GetFileNameWithoutExtension(cleanInput);
                }
            }

            fallback = SanitizeDirectoryName(fallback);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DetermineGameName] Exception: {ex.Message}");
        }

        return "Dump";
    }

    public static string? SanitizeDirectoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        string sanitized = sb.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }
}
