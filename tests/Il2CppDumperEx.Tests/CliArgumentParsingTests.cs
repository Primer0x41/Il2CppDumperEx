using System;
using System.IO;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class CliArgumentParsingTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("-?")]
    public void RunCli_HelpFlag_ReturnsZero(string flag)
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            int exitCode = Program.RunCli([flag]);
            Assert.Equal(0, exitCode);

            string output = sw.ToString();
            Assert.Contains("Usage:", output);
            Assert.Contains("--no-cs", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }


    [Fact]
    public void RunCli_NoArguments_ReturnsNonZero()
    {
        var originalErr = Console.Error;
        var originalOut = Console.Out;
        try
        {
            using var swErr = new StringWriter();
            using var swOut = new StringWriter();
            Console.SetError(swErr);
            Console.SetOut(swOut);

            int exitCode = Program.RunCli(Array.Empty<string>());
            Assert.Equal(1, exitCode);

            string err = swErr.ToString();
            Assert.Contains("No game path or metadata path specified", err);
        }
        finally
        {
            Console.SetError(originalErr);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void RunCli_InvalidForceVersion_ReturnsNonZero()
    {
        var originalErr = Console.Error;
        try
        {
            using var swErr = new StringWriter();
            Console.SetError(swErr);

            int exitCode = Program.RunCli(["--force-version", "invalid_version_not_an_int"]);
            Assert.Equal(1, exitCode);

            string err = swErr.ToString();
            Assert.Contains("must be an integer", err);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void RunCli_NonExistentPath_ReturnsNonZero()
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            using var swOut = new StringWriter();
            using var swErr = new StringWriter();
            Console.SetOut(swOut);
            Console.SetError(swErr);

            string fakePath = Path.Combine(Path.GetTempPath(), "definitely_non_existent_" + Guid.NewGuid().ToString("N"));
            int exitCode = Program.RunCli([fakePath]);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
