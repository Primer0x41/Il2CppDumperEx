using System.IO;
using System.Text;
using System.Text.RegularExpressions;

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
    bool IsIl2Cpp
);

public static class GameDiscovery
{
    public static readonly string[] Il2CppBinaries = ["GameAssembly.dll", "libil2cpp.so", "UnityFramework"];
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

        // Check for Mono/Managed games
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

        string? unityVersion = ExtractUnityVersion(gameRoot, dataDir);

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
        foreach (var dir in Directory.EnumerateDirectories(gameRoot))
        {
            if (Path.GetFileName(dir).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                return dir;
            }
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

        return null;
    }

    private static string? FindMetadata(string gameRoot, string? dataDir)
    {
        // 1. Standard path: <Game>_Data/il2cpp_data/Metadata/global-metadata.dat
        if (dataDir != null)
        {
            string standard = Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat");
            if (File.Exists(standard)) return standard;

            // 2. Relocated in StreamingAssets
            string streaming = Path.Combine(dataDir, "StreamingAssets");
            if (Directory.Exists(streaming))
            {
                foreach (var file in Directory.EnumerateFiles(streaming, "global-metadata.dat", SearchOption.AllDirectories))
                {
                    if (File.Exists(file)) return file;
                }
            }
        }

        // 3. Search anywhere in gameRoot
        if (Directory.Exists(gameRoot))
        {
            foreach (var file in Directory.EnumerateFiles(gameRoot, "global-metadata.dat", SearchOption.AllDirectories))
            {
                if (File.Exists(file)) return file;
            }

            // 4. Magic-byte search for *.dat
            Span<byte> magic = stackalloc byte[4];
            foreach (var file in Directory.EnumerateFiles(gameRoot, "*.dat", SearchOption.AllDirectories))
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
                catch { }
            }
        }

        return null;
    }

    private static string? FindBinary(string gameRoot, string? dataDir)
    {
        if (Directory.Exists(gameRoot))
        {
            foreach (var name in Il2CppBinaries)
            {
                string cand = Path.Combine(gameRoot, name);
                if (File.Exists(cand)) return cand;
            }
        }

        if (dataDir != null && Directory.Exists(dataDir))
        {
            foreach (var name in Il2CppBinaries)
            {
                string cand = Path.Combine(dataDir, name);
                if (File.Exists(cand)) return cand;
            }
        }

        if (Directory.Exists(gameRoot))
        {
            foreach (var name in Il2CppBinaries)
            {
                foreach (var file in Directory.EnumerateFiles(gameRoot, name, SearchOption.AllDirectories))
                {
                    if (File.Exists(file)) return file;
                }
            }
        }

        return null;
    }
}
