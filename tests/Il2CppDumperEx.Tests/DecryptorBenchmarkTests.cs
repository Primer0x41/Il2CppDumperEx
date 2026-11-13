using System.Diagnostics;
using Il2CppDumperEx.Core;
using Xunit;
using Xunit.Abstractions;

namespace Il2CppDumperEx.Tests;

public class DecryptorBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public DecryptorBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Benchmark_Throughput_50MB()
    {
        int size = 50 * 1024 * 1024 + 136;
        byte[] input = new byte[size];
        input[0] = 0xAF; input[1] = 0x1B; input[2] = 0xB1; input[3] = 0xFA;
        input[4] = 0x00; input[5] = 0x00; input[6] = 0x01; input[7] = 0x00;

        _ = Nep2Decryptor.DecryptBytes(input.AsSpan(0, 1024 * 1024));

        var sw = Stopwatch.StartNew();
        byte[] decrypted = Nep2Decryptor.DecryptBytes(input);
        sw.Stop();

        double elapsedSeconds = sw.Elapsed.TotalSeconds;
        double mb = size / (1024.0 * 1024.0);
        double mbPerSec = mb / elapsedSeconds;

        _output.WriteLine($"Decrypted {mb:F2} MB in {elapsedSeconds * 1000:F2} ms ({mbPerSec:F2} MB/s)");
        Assert.Equal(size, decrypted.Length);
    }
}
