using System.Buffers.Binary;
using Il2CppDumperEx.Core;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class PipelineTests
{
    private static string CreateMockGameFolder()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "md_pipe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        File.WriteAllBytes(Path.Combine(tmpDir, "Game.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(tmpDir, "GameAssembly.dll"), [0x4D, 0x5A]);

        string dataDir = Path.Combine(tmpDir, "Game_Data");
        string metaDir = Path.Combine(dataDir, "il2cpp_data", "Metadata");
        Directory.CreateDirectory(metaDir);

        byte[] meta = new byte[256];
        Nep2Decryptor.MagicBytes.CopyTo(meta.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(meta.AsSpan(4, 4), 29);
        File.WriteAllBytes(Path.Combine(metaDir, "global-metadata.dat"), meta);
        File.WriteAllText(Path.Combine(dataDir, "app.info"), "2021.3.16f1");

        return tmpDir;
    }


    [Fact]
    public void Pipeline_Cancellation_ThrowsOperationCanceledException()
    {
        string gameFolder = CreateMockGameFolder();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            var pipeline = new DumpPipeline(
                gamePath: gameFolder,
                cancellationToken: cts.Token
            );

            Assert.Throws<OperationCanceledException>(() => pipeline.Run());
        }
        finally
        {
            try { Directory.Delete(gameFolder, true); } catch { }
        }
    }

    [Fact]
    public void DecryptMetadataFile_ValidatesAndWritesDecryptedMetadata()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "md_dec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            string inputFile = Path.Combine(tmpDir, "global-metadata.dat");
            string outputFile = Path.Combine(tmpDir, "global-metadata-out.dat");

            byte[] mockMetadata = DiscoveryAndValidationTests.BuildMockMetadataFile(29, 4096);
            File.WriteAllBytes(inputFile, mockMetadata);

            var result = DumpPipeline.DecryptMetadataFile(inputFile, outputFile);

            Assert.True(File.Exists(outputFile));
            Assert.Equal(29, result.ValidatedVersion);
            Assert.False(result.NeededDecryption);
            Assert.True(result.SaneStringCount > 0);
            Assert.Equal(mockMetadata.Length, new FileInfo(outputFile).Length);

            // Test destination resolution when output path is an existing directory
            string outSubDir = Path.Combine(tmpDir, "target_folder");
            Directory.CreateDirectory(outSubDir);
            string destination = Directory.Exists(outSubDir) ? Path.Combine(outSubDir, "global-metadata.dat") : outSubDir;
            var dirResult = DumpPipeline.DecryptMetadataFile(inputFile, destination);
            Assert.True(File.Exists(Path.Combine(outSubDir, "global-metadata.dat")));
            Assert.Equal(29, dirResult.ValidatedVersion);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("com.example.game.apk", "com.example.game")]
    [InlineData("GameBundle.aab", "GameBundle")]
    [InlineData("archive_game.zip", "archive_game")]
    [InlineData("Payload.ipa", "Payload")]
    [InlineData("MyGame.exe", "MyGame")]
    public void DetermineOutputDirectory_WithArchiveOrExeInput_WhenOutputDirNull_ExtractsToOutputSubfolder(string inputFileName, string expectedSubfolder)
    {
        string mockRoot = Path.GetFullPath("MockGames");
        string inputPath = Path.Combine(mockRoot, inputFileName);
        string output = DumpPipeline.DetermineOutputDirectory(null, null, inputPath);

        Assert.Equal(Path.GetFullPath(Path.Combine("output", expectedSubfolder)), output);
    }

    [Fact]
    public void DetermineOutputDirectory_WhenOutputDirNull_UsesDetectedGameName()
    {
        string mockRoot = Path.GetFullPath("MockGames");
        string output = DumpPipeline.DetermineOutputDirectory(null, "GenshinImpact", Path.Combine(mockRoot, "Genshin.exe"));
        Assert.Equal(Path.GetFullPath(Path.Combine("output", "GenshinImpact")), output);
    }

    [Fact]
    public void DetermineOutputDirectory_WhenOutputDirNull_FallsBackToFolder_WhenGameNameEmptyOrInvalid()
    {
        string mockRoot = Path.GetFullPath("MockGames");
        string inputFolder = Path.Combine(mockRoot, "MyCustomGame");
        string output = DumpPipeline.DetermineOutputDirectory(null, "   ", inputFolder);

        Assert.Equal(Path.GetFullPath(Path.Combine("output", "MyCustomGame")), output);
    }

    [Fact]
    public void DetermineOutputDirectory_DataDirStripsDataSuffix()
    {
        string mockRoot = Path.GetFullPath("MockGames");
        string dataFolder = Path.Combine(mockRoot, "MyAdventure_Data");
        string output = DumpPipeline.DetermineOutputDirectory(null, null, dataFolder);

        Assert.Equal(Path.GetFullPath(Path.Combine("output", "MyAdventure")), output);
    }

    [Fact]
    public void DetermineOutputDirectory_SanitizesInvalidChars()
    {
        string mockRoot = Path.GetFullPath("MockGames");
        string output = DumpPipeline.DetermineOutputDirectory(null, "Super:Mario*Galaxy?Deluxe", Path.Combine(mockRoot, "Dummy"));
        Assert.Equal(Path.GetFullPath(Path.Combine("output", "Super_Mario_Galaxy_Deluxe")), output);
    }

    [Fact]
    public void DetermineOutputDirectory_WhenOutputDirSpecified_ExtractsDirectlyWithoutExtraSubfolder()
    {
        string customDir = Path.GetFullPath(Path.Combine("CustomDumps", "SpecificFolder"));
        string mockRoot = Path.GetFullPath("MockGames");
        string output = DumpPipeline.DetermineOutputDirectory(customDir, "GenshinImpact", Path.Combine(mockRoot, "Genshin.exe"));

        Assert.Equal(customDir, output);
    }

    [Fact]
    public void DetermineOutputDirectory_WhenBaseOutputDirIsGenericOutput_AppendsDetectedGameSubfolder()
    {
        string baseDir = Path.GetFullPath("output");
        string mockRoot = Path.GetFullPath("MockGames");
        string output = DumpPipeline.DetermineOutputDirectory(baseDir, "HonkaiStarRail", Path.Combine(mockRoot, "StarRail.exe"));

        Assert.Equal(Path.Combine(baseDir, "HonkaiStarRail"), output);
    }

    [Fact]
    public void DetermineOutputDirectory_WhenBaseOutputDirIsGenericOutputAndGameNameNull_FallsBackToInputName()
    {
        string baseDir = Path.GetFullPath("output");
        string mockRoot = Path.GetFullPath("MockGames");
        string output = DumpPipeline.DetermineOutputDirectory(baseDir, null, Path.Combine(mockRoot, "MyInputGame.exe"));

        Assert.Equal(Path.Combine(baseDir, "MyInputGame"), output);
    }
}
