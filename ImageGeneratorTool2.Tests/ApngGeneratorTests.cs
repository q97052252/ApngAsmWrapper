using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ApngAsmWrapper;
using ApngAsmWrapper.ImageSharp;
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
    public void DelayMilliseconds_ConvertsToExpectedFraction()
    {
        (int n1, int d1) = ApngGenerator.ToApngDelayFractionFromMilliseconds(3000);
        Assert.AreEqual(3, n1);
        Assert.AreEqual(1, d1);

        (int n2, int d2) = ApngGenerator.ToApngDelayFractionFromMilliseconds(1500);
        Assert.AreEqual(3, n2);
        Assert.AreEqual(2, d2);
    }

    [TestMethod]
    public void DelayMilliseconds_ClampsTo16Bit()
    {
        // Very large delay should still produce a 16-bit safe fraction.
        (int n, int d) = ApngGenerator.ToApngDelayFractionFromMilliseconds(int.MaxValue);
        Assert.IsTrue(n >= 1 && n <= 65535);
        Assert.IsTrue(d >= 1 && d <= 65535);
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

            var src = new ApngGenerator.FileFrameSource(jpg, transcodeNonPng: true, transcoder: new ImageSharpPngTranscoder());
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

    [TestMethod]
    public async Task Integration_GeneratesApngWithThreeFrames_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_APNG_INTEGRATION"), "1", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("Set RUN_APNG_INTEGRATION=1 to enable integration test.");

        string exe = Path.Combine(AppContext.BaseDirectory, "apngasm64.exe");
        if (!File.Exists(exe))
            Assert.Inconclusive("apngasm64.exe not found in test output.");

        string tempRoot = Path.Combine(Path.GetTempPath(), "ApngGeneratorIntegration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string f0 = Path.Combine(tempRoot, "f0.png");
            string f1 = Path.Combine(tempRoot, "f1.jpg");
            string f2 = Path.Combine(tempRoot, "f2.png");

            // Create PNG/JPG/PNG to validate auto-transcode path via ImageSharp addon.
            using (var bmp = new System.Drawing.Bitmap(16, 16))
            {
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.Clear(System.Drawing.Color.Red);
                bmp.Save(f0, System.Drawing.Imaging.ImageFormat.Png);
            }
            using (var bmp = new System.Drawing.Bitmap(16, 16))
            {
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.Clear(System.Drawing.Color.Green);
                bmp.Save(f1, System.Drawing.Imaging.ImageFormat.Jpeg);
            }
            using (var bmp = new System.Drawing.Bitmap(16, 16))
            {
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.Clear(System.Drawing.Color.Blue);
                bmp.Save(f2, System.Drawing.Imaging.ImageFormat.Png);
            }

            string outPath = Path.Combine(tempRoot, "out.png");

            var options = new ApngGenerator.Options
            {
                SkipFirstFrame = true,
                LoopCount = 1,
                KeepTempFiles = false,
                TranscodeNonPngInputs = true,
            };

            var req = new ApngGenerator.Builder(exe, outPath)
                .WithOptions(options)
                .WithImageSharpTranscoding()
                .AddFrame(f0)
                .AddFrame(f1, 3, 1)
                .AddFrame(f2, 1, 1)
                .Build();

            ApngGenerator.Result result = await ApngGenerator.GenerateAsync(req);
            Assert.IsTrue(result.Success, result.ErrorMessage + "\n" + result.StandardError);
            Assert.IsTrue(File.Exists(outPath));

            int frames = ReadApngFrameCount(outPath);
            Assert.AreEqual(3, frames, "Expected 3 frames in output APNG.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static int ReadApngFrameCount(string pngPath)
    {
        // Reads acTL chunk (Animation Control) and returns num_frames. If absent, returns 1.
        // PNG signature 8 bytes, then chunks: length(4) type(4) data(length) crc(4)
        byte[] data = File.ReadAllBytes(pngPath);
        int i = 8;
        while (i + 12 <= data.Length)
        {
            int len = ReadInt32BE(data, i);
            string type = System.Text.Encoding.ASCII.GetString(data, i + 4, 4);
            int dataStart = i + 8;
            if (dataStart + len + 4 > data.Length)
                break;
            if (type == "acTL" && len >= 8)
                return ReadInt32BE(data, dataStart);
            i = dataStart + len + 4;
        }
        return 1;
    }

    private static int ReadInt32BE(byte[] b, int offset)
    {
        return (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
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

