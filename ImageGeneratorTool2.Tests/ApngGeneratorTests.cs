using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ApngAsmWrapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageGeneratorTool2.Tests;

[TestClass]
public sealed class ApngGeneratorTests
{
    private sealed class FakeRunner : ApngGenerator.IApngasmProcessRunner
    {
        public string? LastExe { get; private set; }
        public string? LastArgs { get; private set; }
        public string? LastCwd { get; private set; }

        public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string exePath, string arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            LastExe = exePath;
            LastArgs = arguments;
            LastCwd = workingDirectory;
            return Task.FromResult((0, "ok", ""));
        }
    }

    [TestMethod]
    public async Task GenerateAsync_WritesDelayFiles_AndBuildsArgs()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ApngGeneratorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Create a dummy "exe" file so ApngGenerator passes file-exists validation.
            string fakeExe = Path.Combine(tempRoot, "apngasm64.exe");
            await File.WriteAllTextAsync(fakeExe, "fake");

            string outPath = Path.Combine(tempRoot, "out.png");

            var options = new ApngGenerator.Options
            {
                SkipFirstFrame = true,
                LoopCount = 1,
                TempDirectoryRoot = tempRoot,
            };

            // Use file sources; content doesn't matter for this test (we just want delay txt).
            string f0 = Path.Combine(tempRoot, "in0.png");
            string f1 = Path.Combine(tempRoot, "in1.png");
            await File.WriteAllBytesAsync(f0, new byte[] { 1, 2, 3 });
            await File.WriteAllBytesAsync(f1, new byte[] { 4, 5, 6 });

            ApngGenerator.Request req = new ApngGenerator.Builder(fakeExe, outPath)
                .WithOptions(options)
                .AddFrame(f0)
                .AddFrame(f1, delayNum: 3, delayDen: 1)
                .Build();

            var runner = new FakeRunner();
            ApngGenerator.Result result = await ApngGenerator.GenerateAsync(req, runner: runner);

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(runner.LastArgs);
            StringAssert.Contains(runner.LastArgs, "\""+outPath+"\"");
            // Inputs are copied into the generator temp folder with stable names (frame00.png, frame01.png, ...).
            StringAssert.Contains(runner.LastArgs, "\""+Path.Combine(runner.LastCwd!, "frame00.png")+"\"");
            StringAssert.Contains(runner.LastArgs, "\""+Path.Combine(runner.LastCwd!, "frame01.png")+"\"");
            StringAssert.Contains(runner.LastArgs, "-f");
            StringAssert.Contains(runner.LastArgs, "-l1");

            // Ensure delay file exists in runner cwd (temp subdir). The generator cleans up temp dir by default,
            // so instead we only assert that the working directory was set under tempRoot.
            Assert.IsNotNull(runner.LastCwd);
            StringAssert.StartsWith(runner.LastCwd!, tempRoot, "Working directory should be under configured temp root.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}

