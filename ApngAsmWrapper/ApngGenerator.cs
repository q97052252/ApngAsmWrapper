using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace ApngAsmWrapper;

/// <summary>
/// High-level wrapper around the <c>apngasm</c> (APNG Assembler) CLI.
/// </summary>
/// <remarks>
/// This component:
/// - Materializes frame inputs (paths/streams/images) into a temp folder
/// - Writes per-frame delay overrides via <c>frameXX.txt</c> with <c>delay=NUM/DEN</c>
/// - Executes the apngasm process and returns a rich <see cref="Result"/>
///
/// The apngasm binary can be bundled into the NuGet package and copied to the consumer output directory.
/// You can also override the exe path via <see cref="Request.ApngasmExePath"/> or the <c>APNGASM_EXE</c> environment variable.
/// </remarks>
public static partial class ApngGenerator
{
    /// <summary>
    /// The default filename we bundle/copy for apngasm.
    /// </summary>
    public const string DefaultApngasmExeFileName = "apngasm64.exe";

    /// <summary>
    /// Attempts to resolve the <c>apngasm64.exe</c> path.
    /// </summary>
    /// <remarks>
    /// Resolution order:
    /// - If environment variable <c>APNGASM_EXE</c> is set and points to an existing file, use it
    /// - If <c>AppContext.BaseDirectory\apngasm64.exe</c> exists (recommended when packaged), use it
    /// - If <c>AppContext.BaseDirectory\tools\apngasm\apngasm64.exe</c> exists, use it (fallback)
    /// </remarks>
    public static bool TryResolveApngasmExePath(out string exePath)
    {
        exePath = string.Empty;

        string? env = Environment.GetEnvironmentVariable("APNGASM_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            exePath = env;
            return true;
        }

        string candidate = Path.Combine(AppContext.BaseDirectory, DefaultApngasmExeFileName);
        if (File.Exists(candidate))
        {
            exePath = candidate;
            return true;
        }

        candidate = Path.Combine(AppContext.BaseDirectory, "tools", "apngasm", DefaultApngasmExeFileName);
        if (File.Exists(candidate))
        {
            exePath = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the apngasm executable path, or throws if it cannot be found.
    /// </summary>
    public static string ResolveApngasmExePathOrThrow()
    {
        if (TryResolveApngasmExePath(out string path))
            return path;
        throw new FileNotFoundException($"Could not locate '{DefaultApngasmExeFileName}'. Set APNGASM_EXE or copy it to the output directory.", DefaultApngasmExeFileName);
    }

    /// <summary>apngasm compression mode (<c>-z0/-z1/-z2</c>).</summary>
    public enum Compression
    {
        /// <summary>zlib (<c>-z0</c>).</summary>
        Zlib,
        /// <summary>7zip (<c>-z1</c>, apngasm default).</summary>
        SevenZip,
        /// <summary>Zopfli (<c>-z2</c>).</summary>
        Zopfli,
    }

    /// <summary>Global apngasm options mapped from CLI flags.</summary>
    public sealed record Options
    {
        /// <summary>Loop count (<c>-l#</c>). 0 = forever. Default 0.</summary>
        public int LoopCount { get; init; } = 0;

        /// <summary>Skip the first frame when playing (<c>-f</c>). Default false.</summary>
        public bool SkipFirstFrame { get; init; } = false;

        /// <summary>Keep palette (<c>-kp</c>). Default false.</summary>
        public bool KeepPalette { get; init; } = false;

        /// <summary>Keep color type (<c>-kc</c>). Default false.</summary>
        public bool KeepColorType { get; init; } = false;

        /// <summary>Compression mode (<c>-z0/-z1/-z2</c>). Default SevenZip.</summary>
        public Compression CompressionMode { get; init; } = Compression.SevenZip;

        /// <summary>Iterations (<c>-i##</c>). Null = apngasm default.</summary>
        public int? Iterations { get; init; }

        /// <summary>Horizontal strip input (<c>-hs##</c>). Null = disabled.</summary>
        public int? HorizontalStripFrames { get; init; }

        /// <summary>Vertical strip input (<c>-vs##</c>). Null = disabled.</summary>
        public int? VerticalStripFrames { get; init; }

        /// <summary>
        /// Default delay for the first input frame, as numerator/denominator (<c>NUM DEN</c>).
        /// Per-frame delays should be set on <see cref="Frame"/> to write <c>frameXX.txt</c>.
        /// </summary>
        public (int Numerator, int Denominator)? DefaultDelay { get; init; }

        /// <summary>
        /// Optional temp directory root. If null/empty, uses <see cref="Path.GetTempPath"/>.
        /// </summary>
        public string? TempDirectoryRoot { get; init; }

        /// <summary>
        /// If true, when a file-path frame input is not a PNG (e.g. jpg/bmp), it will be transcoded to PNG automatically.
        /// If false, non-PNG file inputs will throw. Default true.
        /// </summary>
        public bool TranscodeNonPngInputs { get; init; } = true;

        /// <summary>
        /// If true, the temporary working directory (materialized frames + delay files) will NOT be deleted.
        /// Useful for troubleshooting apngasm failures. Default false.
        /// </summary>
        public bool KeepTempFiles { get; init; } = false;

        /// <summary>
        /// If true, materialize frames concurrently (bounded by <see cref="MaxDegreeOfParallelism"/>). Default false.
        /// </summary>
        public bool MaterializeFramesInParallel { get; init; } = false;

        /// <summary>
        /// Max concurrency when <see cref="MaterializeFramesInParallel"/> is enabled.
        /// Null = Environment.ProcessorCount. Minimum 1.
        /// </summary>
        public int? MaxDegreeOfParallelism { get; init; }

        /// <summary>
        /// Extra raw CLI args appended at the end (extension point for future flags).
        /// </summary>
        public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    }

    /// <summary>Frame definition (source + optional delay override).</summary>
    public sealed record Frame(IApngFrameSource Source)
    {
        /// <summary>Delay numerator. If set, requires <see cref="DelayDenominator"/>.</summary>
        public int? DelayNumerator { get; init; }
        /// <summary>Delay denominator. If set, requires <see cref="DelayNumerator"/>.</summary>
        public int? DelayDenominator { get; init; }
    }

    /// <summary>Request: apngasm path + output path + frames + options.</summary>
    public sealed record Request(
        string ApngasmExePath,
        string OutputApngPath,
        IReadOnlyList<Frame> Frames,
        Options Options);

    /// <summary>Execution result including diagnostics.</summary>
    public sealed record Result(
        bool Success,
        string? ErrorMessage,
        int? ExitCode,
        string? CommandLine,
        string? StandardOutput,
        string? StandardError,
        string? TempDirectory,
        IReadOnlyList<string>? MaterializedFramePaths)
    {
        public static Result Ok(string cmd, string stdout, string stderr, string? tempDir, IReadOnlyList<string>? materialized) =>
            new(true, null, 0, cmd, stdout, stderr, tempDir, materialized);

        public static Result Fail(string message, int? exit, string cmd, string stdout, string stderr, string? tempDir, IReadOnlyList<string>? materialized) =>
            new(false, message, exit, cmd, stdout, stderr, tempDir, materialized);
    }

    /// <summary>Optional logging interface (implement to integrate with your logger).</summary>
    public interface IApngLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    /// <summary>No-op logger.</summary>
    public sealed class NullLogger : IApngLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    /// <summary>Process runner abstraction (for testability / alternate execution).</summary>
    public interface IApngasmProcessRunner
    {
        Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            string exePath,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken);
    }

    /// <summary>Default runner based on <see cref="Process"/>.</summary>
    public sealed class DefaultProcessRunner : IApngasmProcessRunner
    {
        public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            string exePath,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            using Process p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            p.Start();
            Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = p.StandardError.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken);
            return (p.ExitCode, await stdoutTask, await stderrTask);
        }
    }

    /// <summary>Frame source abstraction.</summary>
    public interface IApngFrameSource
    {
        Task<string> MaterializePngAsync(string directory, string fileNameWithoutExt, CancellationToken ct);
    }

    /// <summary>Frame source backed by a file path.</summary>
    public sealed class FileFrameSource : IApngFrameSource
    {
        private readonly string _path;
        private readonly bool _transcodeNonPng;

        public FileFrameSource(string path, bool transcodeNonPng = true)
        {
            _path = path;
            _transcodeNonPng = transcodeNonPng;
        }

        public async Task<string> MaterializePngAsync(string directory, string fileNameWithoutExt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(_path))
                throw new ArgumentException("Input path is empty.", nameof(_path));
            if (!File.Exists(_path))
                throw new FileNotFoundException("Input file not found.", _path);

            string target = Path.Combine(directory, fileNameWithoutExt + ".png");
            string srcFull = Path.GetFullPath(_path);
            string dstFull = Path.GetFullPath(target);

            string ext = Path.GetExtension(_path);
            bool isPng = string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase);

            if (isPng)
            {
                // If the source is already the exact target path, avoid redundant copy.
                if (!string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
                    File.Copy(_path, target, overwrite: true);
                return target;
            }

            if (!_transcodeNonPng)
                throw new NotSupportedException($"Non-PNG input is not allowed: '{_path}'. Enable TranscodeNonPngInputs to auto-convert.");

            // Transcode common formats (jpg/bmp/gif/...) to PNG.
            await using FileStream input = File.OpenRead(_path);
            using Image image = await Image.LoadAsync(input, ct);
            var encoder = new PngEncoder();
            await image.SaveAsPngAsync(target, encoder, ct);
            return target;
        }
    }

    /// <summary>Frame source backed by a <see cref="Stream"/> (written to a .png file).</summary>
    public sealed class StreamFrameSource : IApngFrameSource
    {
        private readonly Stream _stream;
        public StreamFrameSource(Stream stream) => _stream = stream;

        public async Task<string> MaterializePngAsync(string directory, string fileNameWithoutExt, CancellationToken ct)
        {
            string target = Path.Combine(directory, fileNameWithoutExt + ".png");
            if (_stream.CanSeek)
                _stream.Position = 0;
            await using FileStream fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await _stream.CopyToAsync(fs, 81920, ct);
            return target;
        }
    }

    /// <summary>Fluent builder for <see cref="Request"/>.</summary>
    public sealed partial class Builder
    {
        private readonly List<Frame> _frames = new();
        private Options _options = new();
        private bool _transcodeNonPngInputs = true;
        private readonly string _apngasmExePath;
        private readonly string _outputPath;

        public Builder(string apngasmExePath, string outputApngPath)
        {
            _apngasmExePath = apngasmExePath;
            _outputPath = outputApngPath;
        }

        /// <summary>
        /// Creates a builder that uses a resolved bundled/output apngasm executable path.
        /// </summary>
        public Builder(string outputApngPath)
        {
            _apngasmExePath = ResolveApngasmExePathOrThrow();
            _outputPath = outputApngPath;
        }

        public Builder WithOptions(Options options)
        {
            _options = options;
            _transcodeNonPngInputs = options.TranscodeNonPngInputs;
            return this;
        }

        /// <summary>
        /// Controls whether file-path inputs that are not PNG should be auto-transcoded to PNG.
        /// This affects subsequent <see cref="AddFrame(string,int?,int?)"/> calls.
        /// </summary>
        public Builder WithFileInputTranscoding(bool enabled)
        {
            _transcodeNonPngInputs = enabled;
            return this;
        }

        public Builder AddFrame(string imagePath, int? delayNum = null, int? delayDen = null)
        {
            _frames.Add(new Frame(new FileFrameSource(imagePath, _transcodeNonPngInputs)) { DelayNumerator = delayNum, DelayDenominator = delayDen });
            return this;
        }

        /// <summary>
        /// Adds multiple frames from file paths.
        /// </summary>
        public Builder AddFrames(IEnumerable<string> imagePaths)
        {
            foreach (string p in imagePaths)
                AddFrame(p);
            return this;
        }

        /// <summary>
        /// Adds multiple frames with per-frame delays.
        /// </summary>
        public Builder AddFrames(IEnumerable<(string Path, int? DelayNum, int? DelayDen)> frames)
        {
            foreach (var f in frames)
                AddFrame(f.Path, f.DelayNum, f.DelayDen);
            return this;
        }

        public Builder AddFrame(Stream pngStream, int? delayNum = null, int? delayDen = null)
        {
            _frames.Add(new Frame(new StreamFrameSource(pngStream)) { DelayNumerator = delayNum, DelayDenominator = delayDen });
            return this;
        }

        /// <summary>
        /// Adds multiple frames from streams.
        /// </summary>
        public Builder AddFrames(IEnumerable<Stream> pngStreams)
        {
            foreach (Stream s in pngStreams)
                AddFrame(s);
            return this;
        }

        public Request Build() => new(_apngasmExePath, _outputPath, _frames.ToArray(), _options);
    }

    /// <summary>
    /// Generates an APNG with the provided request.
    /// </summary>
    public static Task<Result> GenerateAsync(
        Request req,
        CancellationToken cancellationToken = default,
        IApngasmProcessRunner? runner = null,
        IApngLogger? logger = null)
    {
        return GenerateInternalAsync(req, cancellationToken, runner ?? new DefaultProcessRunner(), logger ?? new NullLogger());
    }

    /// <summary>
    /// Convenience API: generates an APNG using the resolved bundled/output apngasm executable.
    /// </summary>
    public static Task<Result> GenerateAsync(
        string outputApngPath,
        IReadOnlyList<Frame> frames,
        Options? options = null,
        CancellationToken cancellationToken = default,
        IApngasmProcessRunner? runner = null,
        IApngLogger? logger = null)
    {
        string exe = ResolveApngasmExePathOrThrow();
        var req = new Request(exe, outputApngPath, frames, options ?? new Options());
        return GenerateInternalAsync(req, cancellationToken, runner ?? new DefaultProcessRunner(), logger ?? new NullLogger());
    }

    private static async Task<Result> GenerateInternalAsync(Request req, CancellationToken ct, IApngasmProcessRunner runner, IApngLogger logger)
    {
        if (string.IsNullOrWhiteSpace(req.ApngasmExePath) || !File.Exists(req.ApngasmExePath))
            return Result.Fail($"apngasm executable not found: '{req.ApngasmExePath}'.", null, "", "", "", null, null);
        if (string.IsNullOrWhiteSpace(req.OutputApngPath))
            return Result.Fail("Output path is empty.", null, "", "", "", null, null);
        if (req.Frames is null || req.Frames.Count == 0)
            return Result.Fail("At least one frame is required.", null, "", "", "", null, null);
        if (req.Options.LoopCount < 0)
            return Result.Fail("LoopCount must be >= 0.", null, "", "", "", null, null);
        if (req.Options.HorizontalStripFrames is not null && req.Options.VerticalStripFrames is not null)
            return Result.Fail("Cannot use -hs and -vs together.", null, "", "", "", null, null);

        string tempRoot = string.IsNullOrWhiteSpace(req.Options.TempDirectoryRoot) ? Path.GetTempPath() : req.Options.TempDirectoryRoot!;
        string tmpDir = Path.Combine(tempRoot, "ApngGenerator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            string[] materialized = new string[req.Frames.Count];

            if (!req.Options.MaterializeFramesInParallel || req.Frames.Count <= 1)
            {
                for (int i = 0; i < req.Frames.Count; i++)
                    materialized[i] = await MaterializeFrameAsync(req, tmpDir, i, ct);
            }
            else
            {
                int dop = req.Options.MaxDegreeOfParallelism.GetValueOrDefault(Environment.ProcessorCount);
                if (dop < 1) dop = 1;
                using SemaphoreSlim gate = new SemaphoreSlim(dop);

                var tasks = new Task[req.Frames.Count];
                for (int i = 0; i < req.Frames.Count; i++)
                {
                    int idx = i;
                    tasks[idx] = Task.Run(async () =>
                    {
                        await gate.WaitAsync(ct);
                        try { materialized[idx] = await MaterializeFrameAsync(req, tmpDir, idx, ct); }
                        finally { gate.Release(); }
                    }, ct);
                }
                await Task.WhenAll(tasks);
            }

            var fileInputs = (IReadOnlyList<string>)materialized;
            string args = BuildArgs(req.OutputApngPath, fileInputs, req.Options);
            string cmd = $"\"{req.ApngasmExePath}\" {args}";
            logger.Info(cmd);

            (int exit, string stdout, string stderr) = await runner.RunAsync(req.ApngasmExePath, args, tmpDir, ct);
            return exit == 0
                ? Result.Ok(cmd, stdout, stderr, req.Options.KeepTempFiles ? tmpDir : null, materialized)
                : Result.Fail($"apngasm failed (exit={exit}).", exit, cmd, stdout, stderr, req.Options.KeepTempFiles ? tmpDir : null, materialized);
        }
        catch (OperationCanceledException)
        {
            return Result.Fail("Cancelled.", null, "", "", "", req.Options.KeepTempFiles ? tmpDir : null, null);
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            return Result.Fail(ex.ToString(), null, "", "", "", req.Options.KeepTempFiles ? tmpDir : null, null);
        }
        finally
        {
            if (!req.Options.KeepTempFiles)
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { }
            }
        }
    }

    private static async Task<string> MaterializeFrameAsync(Request req, string tmpDir, int index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string fileName = $"frame{index:00}";
        string path = await req.Frames[index].Source.MaterializePngAsync(tmpDir, fileName, ct);

        int? num = req.Frames[index].DelayNumerator;
        int? den = req.Frames[index].DelayDenominator;
        if (num is not null || den is not null)
        {
            if (num is null || den is null || num <= 0 || den <= 0)
                throw new ArgumentException("Per-frame delay requires positive numerator and denominator.");
            File.WriteAllText(Path.Combine(tmpDir, $"{fileName}.txt"), $"delay={num}/{den}", Encoding.ASCII);
        }

        return path;
    }

    private static string BuildArgs(string outputPath, IReadOnlyList<string> inputPaths, Options opt)
    {
        var parts = new List<string>(32) { Quote(outputPath) };

        for (int i = 0; i < inputPaths.Count; i++)
            parts.Add(Quote(inputPaths[i]));

        if (opt.DefaultDelay is { } d)
        {
            parts.Add(d.Numerator.ToString());
            parts.Add(d.Denominator.ToString());
        }

        parts.Add(opt.CompressionMode switch
        {
            Compression.Zlib => "-z0",
            Compression.SevenZip => "-z1",
            Compression.Zopfli => "-z2",
            _ => "-z1"
        });

        if (opt.Iterations is not null)
            parts.Add("-i" + opt.Iterations.Value);
        if (opt.KeepPalette)
            parts.Add("-kp");
        if (opt.KeepColorType)
            parts.Add("-kc");
        if (opt.SkipFirstFrame)
            parts.Add("-f");

        parts.Add("-l" + opt.LoopCount);

        if (opt.HorizontalStripFrames is not null)
            parts.Add("-hs" + opt.HorizontalStripFrames.Value);
        if (opt.VerticalStripFrames is not null)
            parts.Add("-vs" + opt.VerticalStripFrames.Value);

        foreach (string extra in opt.ExtraArgs)
            parts.Add(extra);

        return string.Join(" ", parts);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}

