using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppDumper;

namespace Il2CppDumperEx.Core;

public class GameDiscoveryException : Exception
{
    public GameDiscoveryException(string message) : base(message) { }
}

public class GameNotFoundException : GameDiscoveryException
{
    public GameNotFoundException(string message) : base(message) { }
}

public class NotIl2CppGameException : GameDiscoveryException
{
    public NotIl2CppGameException(string message) : base(message) { }
}

public class BinaryNotFoundException : GameDiscoveryException
{
    public BinaryNotFoundException(string message) : base(message) { }
}

public class MetadataNotFoundException : GameDiscoveryException
{
    public MetadataNotFoundException(string message) : base(message) { }
}

public record DiscoveredGame(
    string GameName,
    string GameRoot,
    string? DataDir,
    string MetadataPath,
    string BinaryPath,
    string? UnityVersion,
    bool IsIl2Cpp,
    string? TempExtractionDir = null
);

public static class GameDiscovery
{
    public static readonly string[] Il2CppBinaries =
    [
        "GameAssembly.dll",
        "libil2cpp.so",
        "UnityFramework",
        "UnityFramework.framework/UnityFramework",
        "Il2CppUserAssemblies.prx",
        "main",
        "main.nso",
        "eboot.bin",
        "maps.txt"
    ];

    public static readonly string[] Il2CppBinaryExtensions =
    [
        ".dll", ".so", ".dylib", ".prx", ".sprx", ".nso"
    ];

    public static readonly string[] MetadataExtensions =
    [
        "global-metadata.dat", "global-metadata.dec"
    ];

    private static readonly Regex UnityVersionRegex = new(@"\b(20\d{2}\.\d+\.\d+[a-zA-Z0-9]*|6000\.\d+\.\d+[a-zA-Z0-9]*|[5]\.\d+\.\d+[a-zA-Z0-9]*)\b", RegexOptions.Compiled);

    public static DiscoveredGame DiscoverGame(string targetPath)
    {
        string fullPath = Path.GetFullPath(targetPath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new GameNotFoundException($"Target path does not exist: {fullPath}");
        }

        string gameRoot;
        string? dataDir = null;
        string gameName;

        if (File.Exists(fullPath))
        {
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (ext is ".apk" or ".aab" or ".zip" or ".ipa")
            {
                return ExtractAndDiscoverArchive(fullPath);
            }

            gameRoot = Path.GetDirectoryName(fullPath)!;
            string fileName = Path.GetFileName(fullPath);
            if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                gameName = Path.GetFileNameWithoutExtension(fileName);
                string candidateData = Path.Combine(gameRoot, $"{gameName}_Data");
                dataDir = Directory.Exists(candidateData) ? candidateData : FindDataDir(gameRoot);
            }
            else
            {
                gameName = Path.GetFileName(gameRoot);
                dataDir = FindDataDir(gameRoot);
            }
        }
        else
        {
            string dirName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (dirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                dataDir = fullPath;
                gameRoot = Path.GetDirectoryName(fullPath)!;
                gameName = dirName.Substring(0, dirName.Length - 5);
            }
            else
            {
                gameRoot = fullPath;
                dataDir = FindDataDir(gameRoot);
                if (dataDir != null && Path.GetFileName(dataDir).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                {
                    string dName = Path.GetFileName(dataDir);
                    gameName = dName.Substring(0, dName.Length - 5);
                }
                else
                {
                    gameName = dirName;
                }
            }
        }

        string? psTitle = ExtractPlayStationTitleName(gameRoot);
        if (!string.IsNullOrEmpty(psTitle))
        {
            gameName = psTitle;
        }

        string? switchTitle = ExtractSwitchTitleName(gameRoot);
        if (!string.IsNullOrEmpty(switchTitle))
        {
            gameName = switchTitle;
        }

        string? unityVersion = ExtractUnityVersion(gameRoot, dataDir);

        string? monoAssembly = dataDir != null ? Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll") : null;
        string? binaryPath = FindBinary(gameRoot, dataDir);

        if (monoAssembly != null && File.Exists(monoAssembly) && binaryPath == null)
        {
            throw new NotIl2CppGameException(
                $"Game at '{gameRoot}' is a Mono/Managed Unity game ('{monoAssembly}' found, no IL2CPP binary). " +
                "Il2CppDumper is only for IL2CPP games. Use dnSpy, ILSpy, or de4dot instead.");
        }

        if (binaryPath == null)
        {
            throw new BinaryNotFoundException($"Could not find IL2CPP binary ({string.Join(", ", Il2CppBinaries)}) in '{gameRoot}'.");
        }

        string? metadataPath = FindMetadata(gameRoot, dataDir);
        if (metadataPath == null)
        {
            throw new MetadataNotFoundException($"Could not find 'global-metadata.dat' in '{gameRoot}'.");
        }

        return new DiscoveredGame(
            GameName: gameName,
            GameRoot: gameRoot,
            DataDir: dataDir,
            MetadataPath: metadataPath,
            BinaryPath: binaryPath,
            UnityVersion: unityVersion,
            IsIl2Cpp: true
        );
    }

    private static string? FindDataDir(string gameRoot)
    {
        if (!Directory.Exists(gameRoot)) return null;

        // On Switch dumps, assets are located under romfs/Data or romfs
        string switchRomfsData = Path.Combine(gameRoot, "romfs", "Data");
        if (Directory.Exists(switchRomfsData)) return switchRomfsData;
        string switchRomfs = Path.Combine(gameRoot, "romfs");
        if (Directory.Exists(switchRomfs)) return switchRomfs;

        foreach (var dir in Directory.EnumerateDirectories(gameRoot))
        {
            string name = Path.GetFileName(dir);
            if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Media", StringComparison.OrdinalIgnoreCase))
            {
                return dir;
            }
        }
        return null;
    }

    public static string? ExtractSwitchTitleName(string gameRoot)
    {
        try
        {
            // Check control/control.nacp
            string nacpPath = Path.Combine(gameRoot, "control", "control.nacp");
            if (!File.Exists(nacpPath))
            {
                nacpPath = Path.Combine(gameRoot, "control.nacp");
            }

            if (File.Exists(nacpPath))
            {
                byte[] bytes = File.ReadAllBytes(nacpPath);
                // NACP has 16 Title entries of 0x300 (768) bytes each (0x200 name, 0x100 publisher)
                // Entry 0 = AmericanEnglish, Entry 1 = BritishEnglish, etc.
                for (int i = 0; i < 16; i++)
                {
                    int offset = i * 0x300;
                    if (offset + 0x200 > bytes.Length) break;
                    int nullIdx = Array.IndexOf(bytes, (byte)0, offset, 0x200);
                    int len = (nullIdx >= offset) ? (nullIdx - offset) : 0x200;
                    if (len > 0)
                    {
                        string title = Encoding.UTF8.GetString(bytes, offset, len).Trim();
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            return title;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExtractNsoTitleName] Exception: {ex.Message}");
        }

        return null;
    }

    public static string? ExtractPlayStationTitleName(string gameRoot)
    {
        try
        {
            string paramJson = Path.Combine(gameRoot, "sce_sys", "param.json");
            if (File.Exists(paramJson))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(paramJson));
                var root = doc.RootElement;
                if (root.TryGetProperty("localizedParameters", out var locParams))
                {
                    string defLang = locParams.TryGetProperty("defaultLanguage", out var dl) ? dl.GetString() ?? "en-US" : "en-US";
                    if (locParams.TryGetProperty(defLang, out var langObj) && langObj.TryGetProperty("titleName", out var titleProp))
                    {
                        string? title = titleProp.GetString();
                        if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
                    }

                    foreach (var prop in locParams.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            prop.Value.TryGetProperty("titleName", out var anyTitle))
                        {
                            string? title = anyTitle.GetString();
                            if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
                        }
                    }
                }
            }

            string paramSfo = Path.Combine(gameRoot, "sce_sys", "param.sfo");
            if (File.Exists(paramSfo))
            {
                byte[] bytes = File.ReadAllBytes(paramSfo);
                if (bytes.Length >= 20 && BitConverter.ToUInt32(bytes, 0) == 0x46535000) // "\0PSF"
                {
                    uint keyTableOffset = BitConverter.ToUInt32(bytes, 0x08);
                    uint dataTableOffset = BitConverter.ToUInt32(bytes, 0x0C);
                    uint numEntries = BitConverter.ToUInt32(bytes, 0x10);

                    for (int i = 0; i < numEntries; i++)
                    {
                        int entryOffset = 0x14 + i * 16;
                        if (entryOffset + 16 > bytes.Length) break;

                        ushort keyOffset = BitConverter.ToUInt16(bytes, entryOffset);
                        uint dataLen = BitConverter.ToUInt32(bytes, entryOffset + 4);
                        uint dataOffset = BitConverter.ToUInt32(bytes, entryOffset + 12);

                        int fullKeyOffset = (int)(keyTableOffset + keyOffset);
                        if (fullKeyOffset < bytes.Length)
                        {
                            int keyEnd = Array.IndexOf(bytes, (byte)0, fullKeyOffset);
                            if (keyEnd >= fullKeyOffset)
                            {
                                string key = Encoding.UTF8.GetString(bytes, fullKeyOffset, keyEnd - fullKeyOffset);
                                if (key.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                                {
                                    int fullDataOffset = (int)(dataTableOffset + dataOffset);
                                    if (fullDataOffset + dataLen <= bytes.Length)
                                    {
                                        string title = Encoding.UTF8.GetString(bytes, fullDataOffset, (int)dataLen).TrimEnd('\0', ' ', '\r', '\n');
                                        if (!string.IsNullOrWhiteSpace(title)) return title;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExtractPlayStationTitleName] Exception: {ex.Message}");
        }

        return null;
    }

    public static string? ExtractUnityVersion(string gameRoot, string? dataDir)
    {
        List<string> checkPaths = [];
        if (dataDir != null && Directory.Exists(dataDir))
        {
            checkPaths.Add(Path.Combine(dataDir, "globalgamemanagers"));
            checkPaths.Add(Path.Combine(dataDir, "globalgamemanagers.assets"));
            checkPaths.Add(Path.Combine(dataDir, "data.unity3d"));
            checkPaths.Add(Path.Combine(dataDir, "app.info"));
        }

        if (Directory.Exists(gameRoot))
        {
            foreach (var exe in Directory.EnumerateFiles(gameRoot, "*.exe"))
            {
                checkPaths.Add(exe);
            }
        }

        foreach (var path in checkPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] buffer = new byte[Math.Min(8192, fs.Length)];
                int read = fs.Read(buffer, 0, buffer.Length);
                string text = Encoding.ASCII.GetString(buffer, 0, read);
                var match = UnityVersionRegex.Match(text);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            catch
            {
                // Continue scanning fallback files
            }
        }

        // Fallback: search GameAssembly.dll, libil2cpp.so, or UnityFramework for embedded version string
        if (Directory.Exists(gameRoot))
        {
            foreach (var binName in Il2CppBinaries)
            {
                if (binName.Contains('/')) continue;
                string binPath = Path.Combine(gameRoot, binName);
                if (File.Exists(binPath))
                {
                    string? ver = ScanBinaryForUnityVersion(binPath);
                    if (ver != null) return ver;
                }
            }
        }

        return null;
    }

    private static string? ScanBinaryForUnityVersion(string binaryPath)
    {
        try
        {
            using var fs = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int scanLength = (int)Math.Min(1024 * 1024 * 8, fs.Length); // First 8MB
            byte[] chunk = new byte[scanLength];
            int read = fs.Read(chunk, 0, chunk.Length);
            string content = Encoding.ASCII.GetString(chunk, 0, read);
            var match = UnityVersionRegex.Match(content);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScanBinaryForUnityVersion] Exception: {ex.Message}");
        }
        return null;
    }

    private static string? FindMetadata(string gameRoot, string? dataDir)
    {
        if (dataDir != null)
        {
            foreach (var metaName in MetadataExtensions)
            {
                string standard = Path.Combine(dataDir, "il2cpp_data", "Metadata", metaName);
                if (File.Exists(standard)) return standard;
            }

            string streaming = Path.Combine(dataDir, "StreamingAssets");
            if (Directory.Exists(streaming))
            {
                foreach (var metaName in MetadataExtensions)
                {
                    foreach (var file in Directory.EnumerateFiles(streaming, metaName, SearchOption.AllDirectories))
                    {
                        if (File.Exists(file)) return file;
                    }
                }
            }
        }

        if (Directory.Exists(gameRoot))
        {
            foreach (var metaName in MetadataExtensions)
            {
                foreach (var file in Directory.EnumerateFiles(gameRoot, metaName, SearchOption.AllDirectories))
                {
                    if (File.Exists(file)) return file;
                }
            }

            Span<byte> magic = stackalloc byte[4];
            foreach (var ext in new[] { "*.dat", "*.dec" })
            {
                foreach (var file in Directory.EnumerateFiles(gameRoot, ext, SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.Length >= 136)
                        {
                            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                            if (fs.Read(magic) == 4 && magic.SequenceEqual(Nep2Decryptor.MagicBytes))
                            {
                                return file;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[FindMetadata] Error checking {file}: {ex.Message}");
                    }
                }
            }
        }

        return null;
    }

    private static string? FindBinary(string gameRoot, string? dataDir)
    {
        string? CheckPath(string path)
        {
            if (File.Exists(path)) return path;
            return null;
        }

        // Check if Il2CppUserAssemblies.prx exists anywhere in the game tree first (common for PS4/PS5 IL2CPP games)
        if (Directory.Exists(gameRoot))
        {
            foreach (var file in Directory.EnumerateFiles(gameRoot, "Il2CppUserAssemblies.prx", SearchOption.AllDirectories))
            {
                if (File.Exists(file)) return file;
            }
        }

        if (Directory.Exists(gameRoot))
        {
            foreach (var name in Il2CppBinaries)
            {
                string cand = Path.Combine(gameRoot, name);
                if (CheckPath(cand) != null) return cand;
            }
        }

        if (dataDir != null && Directory.Exists(dataDir))
        {
            foreach (var name in Il2CppBinaries)
            {
                string cand = Path.Combine(dataDir, name);
                if (CheckPath(cand) != null) return cand;
            }
        }

        if (Directory.Exists(gameRoot))
        {
            string frameworkCand = Path.Combine(gameRoot, "Frameworks", "UnityFramework.framework", "UnityFramework");
            if (File.Exists(frameworkCand)) return frameworkCand;

            foreach (var name in Il2CppBinaries)
            {
                if (name.Contains('/')) continue;
                foreach (var file in Directory.EnumerateFiles(gameRoot, name, SearchOption.AllDirectories))
                {
                    if (File.Exists(file)) return file;
                }
            }

            foreach (var file in Directory.EnumerateFiles(gameRoot, "*maps*.txt", SearchOption.AllDirectories))
            {
                if (ProcessMapReader.IsProcessMapFile(file)) return file;
            }

            foreach (var file in Directory.EnumerateFiles(gameRoot, "*.*", SearchOption.AllDirectories))
            {
                string fn = Path.GetFileName(file);
                string ext = Path.GetExtension(file);

                if (Il2CppBinaryExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    if (fn.Contains("il2cpp", StringComparison.OrdinalIgnoreCase) ||
                        fn.Contains("GameAssembly", StringComparison.OrdinalIgnoreCase) ||
                        fn.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) ||
                        fn.EndsWith(".nso", StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
            }
        }

        return null;
    }

    private static string? ExtractUnityVersionFromArchive(ZipArchive archive)
    {
        try
        {
            var ggmEntry = archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith("globalgamemanagers.assets", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith("data.unity3d", StringComparison.OrdinalIgnoreCase));

            if (ggmEntry != null)
            {
                using var stream = ggmEntry.Open();
                byte[] buffer = new byte[Math.Min(4096, ggmEntry.Length)];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    string text = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                    var match = UnityVersionRegex.Match(text);
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
            }

            var libUnityEntry = archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("libunity.so", StringComparison.OrdinalIgnoreCase));

            if (libUnityEntry != null)
            {
                using var stream = libUnityEntry.Open();
                int scanSize = (int)Math.Min(1024 * 1024, libUnityEntry.Length);
                byte[] buffer = new byte[scanSize];
                int totalRead = 0;
                while (totalRead < scanSize)
                {
                    int r = stream.Read(buffer, totalRead, scanSize - totalRead);
                    if (r <= 0) break;
                    totalRead += r;
                }

                if (totalRead > 0)
                {
                    string text = System.Text.Encoding.ASCII.GetString(buffer, 0, totalRead);
                    var match = UnityVersionRegex.Match(text);
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static DiscoveredGame ExtractAndDiscoverArchive(string archivePath)
    {
        string archiveName = Path.GetFileNameWithoutExtension(archivePath);
        string uniqueFolderName = $"{archiveName}_{Guid.NewGuid():N}";
        string tempExtractDir = Path.Combine(Path.GetTempPath(), "Il2CppDumperEx_Extracted", uniqueFolderName);

        if (!Directory.Exists(tempExtractDir))
        {
            Directory.CreateDirectory(tempExtractDir);
        }

        string? extractedBinary = null;
        string? extractedMetadata = null;
        string? detectedUnityVersion = null;

        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                detectedUnityVersion = ExtractUnityVersionFromArchive(archive);

                var metaEntry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("global-metadata.dat", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith("global-metadata.dec", StringComparison.OrdinalIgnoreCase));

                if (metaEntry != null)
                {
                    string metaDest = Path.Combine(tempExtractDir, Path.GetFileName(metaEntry.FullName));
                    metaEntry.ExtractToFile(metaDest, overwrite: true);
                    extractedMetadata = metaDest;
                }

                var binaryEntries = archive.Entries.Where(e =>
                {
                    string fn = e.Name;
                    if (string.IsNullOrEmpty(fn)) return false;

                    // Known IL2CPP binary names
                    foreach (var binName in Il2CppBinaries)
                    {
                        if (fn.Equals(Path.GetFileName(binName), StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    // Console & Switch binaries inside archives
                    if (fn.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) ||
                        fn.Equals("Il2CppUserAssemblies.prx", StringComparison.OrdinalIgnoreCase) ||
                        fn.EndsWith(".nso", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    // iOS .ipa binary inside Payload/<App>.app/<App>
                    if (e.FullName.StartsWith("Payload/", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.Contains(".app/", StringComparison.OrdinalIgnoreCase) &&
                        !fn.Contains('.'))
                    {
                        return true;
                    }

                    return false;
                }).ToList();

                var preferredBinary = binaryEntries.FirstOrDefault(e => e.FullName.Contains("arm64-v8a", StringComparison.OrdinalIgnoreCase))
                    ?? binaryEntries.FirstOrDefault(e => e.FullName.Contains("armeabi-v7a", StringComparison.OrdinalIgnoreCase))
                    ?? binaryEntries.FirstOrDefault(e => e.FullName.EndsWith("UnityFramework", StringComparison.OrdinalIgnoreCase))
                    ?? binaryEntries.FirstOrDefault(e => e.FullName.EndsWith("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                    ?? binaryEntries.FirstOrDefault(e => e.FullName.EndsWith("libil2cpp.so", StringComparison.OrdinalIgnoreCase))
                    ?? binaryEntries.FirstOrDefault();

                if (preferredBinary != null)
                {
                    string binDest = Path.Combine(tempExtractDir, Path.GetFileName(preferredBinary.FullName));
                    preferredBinary.ExtractToFile(binDest, overwrite: true);
                    extractedBinary = binDest;
                }
            }

            if (extractedBinary == null)
            {
                throw new BinaryNotFoundException($"Could not find IL2CPP binary (libil2cpp.so / GameAssembly.dll) inside archive '{archivePath}'.");
            }

            if (extractedMetadata == null)
            {
                throw new MetadataNotFoundException($"Could not find 'global-metadata.dat' inside archive '{archivePath}'.");
            }

            return new DiscoveredGame(
                GameName: archiveName,
                GameRoot: tempExtractDir,
                DataDir: tempExtractDir,
                MetadataPath: extractedMetadata,
                BinaryPath: extractedBinary,
                UnityVersion: detectedUnityVersion,
                IsIl2Cpp: true,
                TempExtractionDir: tempExtractDir
            );
        }
        catch
        {
            try
            {
                if (Directory.Exists(tempExtractDir))
                {
                    Directory.Delete(tempExtractDir, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExtractAndDiscoverArchive] Error cleaning temp extraction dir: {ex.Message}");
            }
            throw;
        }
    }
}
