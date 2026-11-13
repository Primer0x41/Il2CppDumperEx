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
}
