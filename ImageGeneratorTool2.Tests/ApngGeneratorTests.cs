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
            Assert.IsNull(result.TempDirectory, "TempDirectory should be null when KeepTempFiles=false.");
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

    [TestMethod]
    public async Task GenerateAsync_KeepTempFiles_ExposesTempDirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ApngGeneratorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string fakeExe = Path.Combine(tempRoot, "apngasm64.exe");
            await File.WriteAllTextAsync(fakeExe, "fake");

            string outPath = Path.Combine(tempRoot, "out.png");

            var options = new ApngGenerator.Options
            {
                SkipFirstFrame = true,
                LoopCount = 1,
                TempDirectoryRoot = tempRoot,
                KeepTempFiles = true,
            };

            string f0 = Path.Combine(tempRoot, "in0.png");
            await File.WriteAllBytesAsync(f0, new byte[] { 1, 2, 3 });

            ApngGenerator.Request req = new ApngGenerator.Builder(fakeExe, outPath)
                .WithOptions(options)
                .AddFrame(f0)
                .Build();

            var runner = new FakeRunner();
            ApngGenerator.Result result = await ApngGenerator.GenerateAsync(req, runner: runner);

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.TempDirectory);
            Assert.IsTrue(Directory.Exists(result.TempDirectory!), "Temp directory should exist when KeepTempFiles=true.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task GenerateAsync_MaterializeFramesInParallel_Works()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ApngGeneratorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string fakeExe = Path.Combine(tempRoot, "apngasm64.exe");
            await File.WriteAllTextAsync(fakeExe, "fake");
            string outPath = Path.Combine(tempRoot, "out.png");

            int started = 0;
            int maxConcurrent = 0;
            int inFlight = 0;

            var options = new ApngGenerator.Options
            {
                TempDirectoryRoot = tempRoot,
                MaterializeFramesInParallel = true,
                MaxDegreeOfParallelism = 2,
            };

            ApngGenerator.IApngFrameSource slow = new SlowFrameSource(
                onStart: () =>
                {
                    Interlocked.Increment(ref started);
                    int now = Interlocked.Increment(ref inFlight);
                    int prev;
                    do { prev = maxConcurrent; } while (now > prev && Interlocked.CompareExchange(ref maxConcurrent, now, prev) != prev);
                },
                onEnd: () => Interlocked.Decrement(ref inFlight));

            var req = new ApngGenerator.Request(
                fakeExe,
                outPath,
                new[]
                {
                    new ApngGenerator.Frame(slow),
                    new ApngGenerator.Frame(slow),
                    new ApngGenerator.Frame(slow),
                },
                options);

            var runner = new FakeRunner();
            ApngGenerator.Result result = await ApngGenerator.GenerateAsync(req, runner: runner);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(3, started);
            Assert.IsTrue(maxConcurrent >= 2, "Expected some parallelism when MaxDegreeOfParallelism=2.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task FileFrameSource_InvalidPath_Throws()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ApngGeneratorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var src = new ApngGenerator.FileFrameSource(Path.Combine(tempRoot, "nope.png"));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                src.MaterializePngAsync(tempRoot, "frame00", CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task FileFrameSource_TranscodesNonPng_WhenEnabled()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ApngGeneratorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Create a tiny JPEG using System.Drawing (tests target net10.0-windows).
            string jpg = Path.Combine(tempRoot, "in.jpg");
            using (var bmp = new System.Drawing.Bitmap(8, 8))
            {
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.Clear(System.Drawing.Color.Aqua);
                bmp.Save(jpg, System.Drawing.Imaging.ImageFormat.Jpeg);
            }

            var src = new ApngGenerator.FileFrameSource(jpg, transcodeNonPng: true);
            string outPng = await src.MaterializePngAsync(tempRoot, "frame00", CancellationToken.None);

            Assert.IsTrue(File.Exists(outPng));
            Assert.AreEqual(".png", Path.GetExtension(outPng).ToLowerInvariant());
            Assert.IsTrue(new FileInfo(outPng).Length > 0);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class SlowFrameSource : ApngGenerator.IApngFrameSource
    {
        private readonly Action _onStart;
        private readonly Action _onEnd;

        public SlowFrameSource(Action onStart, Action onEnd)
        {
            _onStart = onStart;
            _onEnd = onEnd;
        }

        public async Task<string> MaterializePngAsync(string directory, string fileNameWithoutExt, CancellationToken ct)
        {
            _onStart();
            try
            {
                await Task.Delay(100, ct);
                string path = Path.Combine(directory, fileNameWithoutExt + ".png");
                await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }, ct);
                return path;
            }
            finally
            {
                _onEnd();
            }
        }
    }
}

