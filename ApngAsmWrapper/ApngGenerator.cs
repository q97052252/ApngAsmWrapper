using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ApngAsmWrapper;

/// <summary>
/// 面向 .NET 的 <c>apngasm</c>（APNG Assembler）命令行工具高层封装，提供“生成 APNG”的一站式 API。
/// High-level .NET wrapper around the <c>apngasm</c> (APNG Assembler) CLI, providing a one-stop API to generate APNG files.
/// </summary>
/// <remarks>
/// 该组件会自动完成这些工作（调用方只需提供帧和输出路径）：
/// - 将帧输入（文件路径/流/图像等）落盘到临时目录，统一为 PNG 文件
/// - 如帧设置了延迟，则为对应帧写入 <c>frameXX.txt</c>，格式为 <c>delay=NUM/DEN</c>（单位：秒）
/// - 组装并执行 apngasm 进程，收集退出码/stdout/stderr/命令行等诊断信息，并返回结构化 <see cref="Result"/>
///
/// This component automates the full workflow (you only provide frames and output path):
/// - Materializes frame inputs (file paths / streams / images) into a temp folder as PNG files
/// - Writes per-frame delay overrides via <c>frameXX.txt</c> using <c>delay=NUM/DEN</c> (seconds)
/// - Executes apngasm, captures exit code/stdout/stderr/command line, and returns a rich <see cref="Result"/> for diagnostics
///
/// apngasm 可执行文件通常会随 NuGet 一起打包并复制到使用者输出目录（推荐做法）。
/// 你也可以通过 <see cref="Request.ApngasmExePath"/> 或环境变量 <c>APNGASM_EXE</c> 显式指定 apngasm 的路径。
///
/// The apngasm binary can be bundled with the NuGet and copied to the consumer output directory (recommended).
/// Alternatively, override the exe path via <see cref="Request.ApngasmExePath"/> or the <c>APNGASM_EXE</c> environment variable.
/// </remarks>
public static partial class ApngGenerator
{
    /// <summary>
    /// 默认随库打包/复制到输出目录的 apngasm 文件名（Windows x64 常见为 <c>apngasm64.exe</c>）。
    /// The default apngasm filename that is bundled/copied to the output directory (commonly <c>apngasm64.exe</c> on Windows x64).
    /// </summary>
    public const string DefaultApngasmExeFileName = "apngasm64.exe";

    /// <summary>
    /// 尝试解析 <c>apngasm</c> 可执行文件路径（不抛异常；失败返回 false）。
    /// Attempts to resolve the <c>apngasm</c> executable path (no exceptions; returns false on failure).
    /// </summary>
    /// <remarks>
    /// 解析顺序（先匹配到的优先）：
    /// - 如果环境变量 <c>APNGASM_EXE</c> 已设置且指向存在的文件，则使用该路径
    /// - 如果 <c>AppContext.BaseDirectory\apngasm64.exe</c> 存在（建议的打包/部署方式），则使用该路径
    /// - 如果 <c>AppContext.BaseDirectory\tools\apngasm\apngasm64.exe</c> 存在，则使用该路径（兼容性回退）
    ///
    /// Resolution order (first match wins):
    /// - If environment variable <c>APNGASM_EXE</c> is set and points to an existing file, use it
    /// - If <c>AppContext.BaseDirectory\apngasm64.exe</c> exists (recommended packaging/deployment), use it
    /// - If <c>AppContext.BaseDirectory\tools\apngasm\apngasm64.exe</c> exists, use it (compatibility fallback)
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
    /// 解析 apngasm 可执行文件路径；如果找不到则抛出异常（用于“必须可用”的场景）。
    /// Resolves the apngasm executable path; throws if it cannot be found (for “must-have” scenarios).
    /// </summary>
    public static string ResolveApngasmExePathOrThrow()
    {
        if (TryResolveApngasmExePath(out string path))
            return path;
        throw new FileNotFoundException(
            $"无法定位 apngasm 可执行文件 '{DefaultApngasmExeFileName}'。请设置环境变量 APNGASM_EXE 指向 apngasm64.exe，或将其复制到应用输出目录（例如：{AppContext.BaseDirectory}）。" +
            $" / Could not locate apngasm executable '{DefaultApngasmExeFileName}'. Set environment variable APNGASM_EXE to apngasm64.exe, or copy it next to your app output (e.g. {AppContext.BaseDirectory}).",
            DefaultApngasmExeFileName);
    }

    /// <summary>
    /// apngasm 压缩模式（对应 CLI：<c>-z0/-z1/-z2</c>），影响输出文件体积与生成耗时。
    /// apngasm compression mode (<c>-z0/-z1/-z2</c>), affecting output size and encoding time.
    /// </summary>
    public enum Compression
    {
        /// <summary>
        /// 使用 zlib 压缩（<c>-z0</c>）：通常速度更快、压缩率一般。
        /// zlib (<c>-z0</c>): typically faster with moderate compression.
        /// </summary>
        Zlib,
        /// <summary>
        /// 使用 7zip 压缩（<c>-z1</c>，apngasm 默认）：通常压缩率更好但更慢。
        /// 7zip (<c>-z1</c>, apngasm default): often better compression but slower.
        /// </summary>
        SevenZip,
        /// <summary>
        /// 使用 Zopfli 压缩（<c>-z2</c>）：一般压缩率最高但最慢。
        /// Zopfli (<c>-z2</c>): typically best compression but slowest.
        /// </summary>
        Zopfli,
    }

    /// <summary>
    /// 全局选项：将常用 apngasm CLI 参数映射为 .NET 属性，便于以类型安全的方式配置。
    /// Global options mapped from apngasm CLI flags for type-safe configuration.
    /// </summary>
    public sealed record Options
    {
        /// <summary>
        /// 循环次数（<c>-l#</c>）。0 表示无限循环。默认 0。
        /// Loop count (<c>-l#</c>). 0 means forever. Default 0.
        /// </summary>
        public int LoopCount { get; init; } = 0;

        /// <summary>
        /// 播放时跳过第一帧（<c>-f</c>）。默认 false。
        /// Skip the first frame when playing (<c>-f</c>). Default false.
        /// </summary>
        public bool SkipFirstFrame { get; init; } = false;

        /// <summary>
        /// 保留调色板（<c>-kp</c>）。默认 false。
        /// Keep palette (<c>-kp</c>). Default false.
        /// </summary>
        public bool KeepPalette { get; init; } = false;

        /// <summary>
        /// 保留颜色类型（<c>-kc</c>）。默认 false。
        /// Keep color type (<c>-kc</c>). Default false.
        /// </summary>
        public bool KeepColorType { get; init; } = false;

        /// <summary>
        /// 压缩模式（<c>-z0/-z1/-z2</c>）。默认 <see cref="Compression.SevenZip"/>。
        /// Compression mode (<c>-z0/-z1/-z2</c>). Default <see cref="Compression.SevenZip"/>.
        /// </summary>
        public Compression CompressionMode { get; init; } = Compression.SevenZip;

        /// <summary>
        /// 迭代次数（<c>-i##</c>）。null 表示使用 apngasm 默认值。
        /// Iterations (<c>-i##</c>). Null uses apngasm default.
        /// </summary>
        public int? Iterations { get; init; }

        /// <summary>
        /// 水平条带输入（<c>-hs##</c>）。null 表示禁用。
        /// Horizontal strip input (<c>-hs##</c>). Null disables.
        /// </summary>
        public int? HorizontalStripFrames { get; init; }

        /// <summary>
        /// 垂直条带输入（<c>-vs##</c>）。null 表示禁用。
        /// Vertical strip input (<c>-vs##</c>). Null disables.
        /// </summary>
        public int? VerticalStripFrames { get; init; }

        /// <summary>
        /// 第一帧的默认延迟（NUM/DEN，单位：秒；对应 apngasm 参数中的 <c>NUM DEN</c>）。
        /// 若需要逐帧不同延迟，请在 <see cref="Frame"/> 上设置延迟；生成器会写入对应的 <c>frameXX.txt</c>。
        ///
        /// Default delay for the first input frame as numerator/denominator (<c>NUM DEN</c> in seconds).
        /// For per-frame delays, set delay on <see cref="Frame"/> so the generator writes <c>frameXX.txt</c>.
        /// </summary>
        public (int Numerator, int Denominator)? DefaultDelay { get; init; }

        /// <summary>
        /// 临时目录根路径（可选）。用于存放“落盘后的帧 PNG + delay 文件”等中间产物。
        /// 若为 null/空字符串，则使用 <see cref="Path.GetTempPath"/>。
        ///
        /// Optional temp directory root used to store materialized PNG frames and delay files.
        /// If null/empty, uses <see cref="Path.GetTempPath"/>.
        /// </summary>
        public string? TempDirectoryRoot { get; init; }

        /// <summary>
        /// 是否允许对“文件路径帧输入”的非 PNG 文件（如 jpg/bmp）自动转码为 PNG。
        /// - true：遇到非 PNG 时会尝试转码为 PNG（需要同时提供 <see cref="IPngTranscoder"/>，例如使用可选包 ApngAsmWrapper.ImageSharp）
        /// - false：遇到非 PNG 文件路径输入将直接抛出异常
        /// 默认 true。
        ///
        /// Whether to auto-transcode non-PNG file-path inputs (e.g. jpg/bmp) into PNG.
        /// - true: attempts to transcode (requires a <see cref="IPngTranscoder"/>, e.g. from optional ApngAsmWrapper.ImageSharp)
        /// - false: non-PNG file-path inputs will throw
        /// Default true.
        /// </summary>
        public bool TranscodeNonPngInputs { get; init; } = true;

        /// <summary>
        /// 是否保留临时工作目录（包含落盘帧 PNG 与 delay 文件等）。
        /// - true：不会删除临时目录，便于排查 apngasm 失败原因（可查看中间产物是否正确）
        /// - false：执行结束后自动清理临时目录（推荐默认）
        /// 默认 false。
        ///
        /// Whether to keep the temp working directory (materialized PNG frames + delay files, etc.).
        /// - true: do NOT delete temp dir, useful for debugging apngasm failures
        /// - false: automatically cleans up after execution (recommended default)
        /// Default false.
        /// </summary>
        public bool KeepTempFiles { get; init; } = false;

        /// <summary>
        /// 是否并发落盘帧文件（并发度由 <see cref="MaxDegreeOfParallelism"/> 控制）。
        /// 在帧较多、I/O 或转码较重时可提升速度，但也会增加瞬时资源占用。
        /// 默认 false。
        ///
        /// Whether to materialize frames concurrently (bounded by <see cref="MaxDegreeOfParallelism"/>).
        /// Can improve throughput for many frames / heavy I/O or transcoding, at the cost of higher peak resource usage.
        /// Default false.
        /// </summary>
        public bool MaterializeFramesInParallel { get; init; } = false;

        /// <summary>
        /// 当启用 <see cref="MaterializeFramesInParallel"/> 时的最大并发度。
        /// - null：使用 <see cref="Environment.ProcessorCount"/>
        /// - 最小值：1（低于 1 会按 1 处理）
        ///
        /// Max concurrency when <see cref="MaterializeFramesInParallel"/> is enabled.
        /// - Null uses <see cref="Environment.ProcessorCount"/>
        /// - Minimum 1 (values below 1 are treated as 1)
        /// </summary>
        public int? MaxDegreeOfParallelism { get; init; }

        /// <summary>
        /// 额外的原始 CLI 参数（会被追加到命令行末尾），用于扩展/兼容未来新旗标。
        /// Extra raw CLI args appended at the end (extension point for future flags).
        /// </summary>
        public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 帧定义：包含帧来源（Source）以及可选的延迟覆盖（Delay）。
    /// Frame definition: source plus an optional per-frame delay override.
    /// </summary>
    public sealed record Frame(IApngFrameSource Source)
    {
        /// <summary>
        /// 延迟分子（NUM）。如果设置了该值，则必须同时设置 <see cref="DelayDenominator"/>（且两者都必须为正数）。
        /// Delay numerator (NUM). If set, requires <see cref="DelayDenominator"/> as well (both must be positive).
        /// </summary>
        public int? DelayNumerator { get; init; }
        /// <summary>
        /// 延迟分母（DEN）。如果设置了该值，则必须同时设置 <see cref="DelayNumerator"/>（且两者都必须为正数）。
        /// Delay denominator (DEN). If set, requires <see cref="DelayNumerator"/> as well (both must be positive).
        /// </summary>
        public int? DelayDenominator { get; init; }
    }

    /// <summary>
    /// 生成请求：包含 apngasm 路径、输出 APNG 路径、帧列表以及全局选项。
    /// Request: apngasm path + output APNG path + frames + options.
    /// </summary>
    /// <summary>
    /// 生成请求：用于驱动一次 APNG 生成任务的完整输入。
    /// 该类型是“不可变数据”（record），适合在业务层构造后传入 <see cref="GenerateAsync(Request, CancellationToken, IApngasmProcessRunner?, IApngLogger?)"/>。
    ///
    /// Request object that fully describes one APNG generation run.
    /// This is immutable data (record) and is typically constructed in your app layer then passed to
    /// <see cref="GenerateAsync(Request, CancellationToken, IApngasmProcessRunner?, IApngLogger?)"/>.
    /// </summary>
    /// <param name="ApngasmExePath">
    /// apngasm 可执行文件路径（例如 <c>apngasm64.exe</c> 的绝对路径或相对路径）。
    /// 需要是存在的文件，否则生成会失败并返回 <see cref="Result.Success"/> = false。
    ///
    /// Path to the apngasm executable (e.g. absolute/relative path to <c>apngasm64.exe</c>).
    /// Must point to an existing file; otherwise generation fails with <see cref="Result.Success"/> = false.
    /// </param>
    /// <param name="OutputApngPath">
    /// 输出 APNG 的目标路径（通常以 <c>.png</c> 结尾，因为 APNG 也是 PNG 容器格式）。
    /// 若目录不存在会尝试创建；若为空则直接失败。
    ///
    /// Output APNG path (typically ends with <c>.png</c> since APNG is a PNG container).
    /// The output directory will be created if needed; empty paths fail fast.
    /// </param>
    /// <param name="Frames">
    /// 帧列表（至少 1 帧）。每个帧提供来源（文件/流/图像等）以及可选的逐帧延迟覆盖。
    ///
    /// List of frames (must contain at least 1). Each frame provides a source (file/stream/image, etc.)
    /// and an optional per-frame delay override.
    /// </param>
    /// <param name="Options">
    /// 全局选项（压缩、循环次数、默认延迟、临时目录、并行落盘、保留临时文件等）。
    ///
    /// Global options (compression, loop count, default delay, temp dir, parallel materialization, keep temp files, etc.).
    /// </param>
    public sealed record Request(
        string ApngasmExePath,
        string OutputApngPath,
        IReadOnlyList<Frame> Frames,
        Options Options);

    /// <summary>
    /// 执行结果：包含成功/失败信息以及详细诊断（命令行、stdout、stderr、退出码、临时目录等）。
    /// Execution result including rich diagnostics (command line, stdout, stderr, exit code, temp dir, etc.).
    /// </summary>
    /// <summary>
    /// 生成结果：无论成功或失败都尽量返回可诊断的信息（退出码、命令行、stdout/stderr、临时目录、落盘帧路径等）。
    /// 建议：当 <see cref="Success"/> 为 false 时优先查看 <see cref="ErrorMessage"/>、<see cref="StandardError"/> 与 <see cref="CommandLine"/>。
    ///
    /// Generation result: provides rich diagnostics on both success and failure (exit code, command line, stdout/stderr,
    /// temp directory, materialized frame paths, etc.).
    /// Tip: when <see cref="Success"/> is false, start with <see cref="ErrorMessage"/>, <see cref="StandardError"/>, and <see cref="CommandLine"/>.
    /// </summary>
    /// <param name="Success">
    /// 是否成功生成 APNG。/ Whether the generation succeeded.
    /// </param>
    /// <param name="ErrorMessage">
    /// 失败原因（可空）。/ Failure reason (nullable).
    /// </param>
    /// <param name="ExitCode">
    /// apngasm 进程退出码（可空；例如前置校验失败时可能为空）。/ apngasm exit code (nullable; may be null for pre-validation failures).
    /// </param>
    /// <param name="CommandLine">
    /// 运行时的完整命令行（可空）。/ Full command line used (nullable).
    /// </param>
    /// <param name="StandardOutput">
    /// 标准输出（可空）。/ Standard output (nullable).
    /// </param>
    /// <param name="StandardError">
    /// 标准错误（可空）。/ Standard error (nullable).
    /// </param>
    /// <param name="TempDirectory">
    /// 临时工作目录路径（当 <see cref="Options.KeepTempFiles"/> 为 true 时通常会保留并返回）。/ Temp working directory path (typically returned when <see cref="Options.KeepTempFiles"/> is true).
    /// </param>
    /// <param name="MaterializedFramePaths">
    /// 落盘后的帧 PNG 路径列表（可空；失败早期可能为空）。/ Paths of materialized PNG frames (nullable; may be null on early failure).
    /// </param>
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

    /// <summary>
    /// 可选日志接口：实现它以接入你自己的日志框架（Serilog/NLog/ILogger 等）。
    /// Optional logging interface: implement to integrate with your logging framework (Serilog/NLog/ILogger, etc.).
    /// </summary>
    public interface IApngLogger
    {
        /// <summary>
        /// 信息级日志。/ Informational log.
        /// </summary>
        void Info(string message);
        /// <summary>
        /// 警告级日志。/ Warning log.
        /// </summary>
        void Warn(string message);
        /// <summary>
        /// 错误级日志。/ Error log.
        /// </summary>
        void Error(string message);
    }

    /// <summary>
    /// 空实现日志器：默认用于不输出任何日志的场景。
    /// No-op logger used by default when you don't want logging.
    /// </summary>
    public sealed class NullLogger : IApngLogger
    {
        /// <summary>
        /// 写入信息级日志（本实现为空操作，不会输出）。/ Writes an informational log (no-op in this implementation).
        /// </summary>
        public void Info(string message) { }
        /// <summary>
        /// 写入警告级日志（本实现为空操作，不会输出）。/ Writes a warning log (no-op in this implementation).
        /// </summary>
        public void Warn(string message) { }
        /// <summary>
        /// 写入错误级日志（本实现为空操作，不会输出）。/ Writes an error log (no-op in this implementation).
        /// </summary>
        public void Error(string message) { }
    }

    /// <summary>
    /// 进程运行抽象：便于测试（mock）或替换执行方式（例如自定义进程沙箱/权限控制）。
    /// Process runner abstraction for testability (mocking) or alternate execution strategies.
    /// </summary>
    public interface IApngasmProcessRunner
    {
        /// <summary>
        /// 运行 apngasm 进程并捕获 stdout/stderr。
        /// 该抽象用于：单元测试替身、或在特定环境下自定义进程启动策略（例如受限权限/沙箱/超时控制）。
        ///
        /// Runs the apngasm process and captures stdout/stderr.
        /// This abstraction enables test doubles and custom process launching strategies (restricted env/sandbox/timeout control).
        /// </summary>
        /// <param name="exePath">
        /// apngasm 可执行文件路径。/ Path to the apngasm executable.
        /// </param>
        /// <param name="arguments">
        /// 传给 apngasm 的参数字符串（不包含 exe 本身）。/ Argument string passed to apngasm (excluding the exe path).
        /// </param>
        /// <param name="workingDirectory">
        /// 工作目录（本库通常传入临时目录，里面包含帧与 delay 文件）。/ Working directory (typically the temp directory that contains frames and delay files).
        /// </param>
        /// <param name="cancellationToken">
        /// 取消令牌。实现应在取消时尽快中止等待并抛出 <see cref="OperationCanceledException"/> 或返回相应结果。
        /// / Cancellation token. Implementations should abort promptly and throw <see cref="OperationCanceledException"/> (or equivalent) when cancelled.
        /// </param>
        /// <returns>
        /// 三元组：退出码、stdout、stderr。/ A tuple of exit code, stdout, stderr.
        /// </returns>
        Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            string exePath,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// 默认进程运行器：基于 <see cref="Process"/> 启动 apngasm，并捕获 stdout/stderr。
    /// Default process runner based on <see cref="Process"/>, capturing stdout/stderr.
    /// </summary>
    public sealed class DefaultProcessRunner : IApngasmProcessRunner
    {
        /// <summary>
        /// 启动 apngasm 进程并异步读取 stdout/stderr，直到进程退出或取消。
        /// 该实现会：
        /// - 关闭 Shell 执行（<c>UseShellExecute=false</c>）
        /// - 重定向 stdout/stderr 以便返回诊断信息
        /// - 使用 <paramref name="workingDirectory"/> 作为进程工作目录（通常是临时目录）
        ///
        /// Starts the apngasm process and reads stdout/stderr asynchronously until the process exits or cancellation is requested.
        /// This implementation:
        /// - Disables shell execution (<c>UseShellExecute=false</c>)
        /// - Redirects stdout/stderr for diagnostics
        /// - Uses <paramref name="workingDirectory"/> as the process working directory (typically the temp directory)
        /// </summary>
        /// <param name="exePath">apngasm 可执行文件路径。/ apngasm executable path.</param>
        /// <param name="arguments">apngasm 参数（不含 exe）。/ apngasm arguments (excluding exe).</param>
        /// <param name="workingDirectory">工作目录。/ Working directory.</param>
        /// <param name="cancellationToken">取消令牌。/ Cancellation token.</param>
        /// <returns>退出码 + stdout + stderr。/ Exit code + stdout + stderr.</returns>
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

    /// <summary>
    /// 帧来源抽象：负责把“某种形式的帧输入”落盘成 PNG 文件，并返回落盘后的路径。
    /// Frame source abstraction responsible for materializing an input into a PNG file and returning its path.
    /// </summary>
    public interface IApngFrameSource
    {
        /// <summary>
        /// 将帧来源落盘成 PNG 文件并返回目标路径。
        /// 约定：返回的路径应位于 <paramref name="directory"/> 下，文件名使用 <paramref name="fileNameWithoutExt"/> + <c>.png</c>。
        ///
        /// Materializes this frame source into a PNG file and returns the resulting path.
        /// Convention: the returned file should live under <paramref name="directory"/> and be named
        /// <paramref name="fileNameWithoutExt"/> + <c>.png</c>.
        /// </summary>
        /// <param name="directory">
        /// 目标目录（一般是临时目录）。/ Target directory (usually the temp directory).
        /// </param>
        /// <param name="fileNameWithoutExt">
        /// 目标文件名（不含扩展名）。/ Target file name without extension.
        /// </param>
        /// <param name="ct">
        /// 取消令牌。/ Cancellation token.
        /// </param>
        /// <returns>
        /// 落盘后的 PNG 路径。/ Path to the materialized PNG file.
        /// </returns>
        Task<string> MaterializePngAsync(string directory, string fileNameWithoutExt, CancellationToken ct);
    }

    /// <summary>
    /// 可选转码器接口：用于将任意图片格式（jpg/bmp/webp 等）转为 PNG，以便交给 apngasm 处理。
    /// Optional transcoder interface to convert arbitrary image formats (jpg/bmp/webp, etc.) into PNG for apngasm.
    /// </summary>
    public interface IPngTranscoder
    {
        /// <summary>
        /// 将任意图片文件转码为 PNG。
        /// 要求：输出 PNG 必须可被 apngasm 正常读取。
        ///
        /// Transcodes an image file of any supported format into PNG.
        /// Requirement: output PNG must be readable by apngasm.
        /// </summary>
        /// <param name="inputPath">
        /// 输入文件路径（源格式可为 jpg/bmp/webp 等）。/ Input file path (source may be jpg/bmp/webp, etc.).
        /// </param>
        /// <param name="outputPngPath">
        /// 输出 PNG 文件路径。/ Output PNG file path.
        /// </param>
        /// <param name="ct">
        /// 取消令牌。/ Cancellation token.
        /// </param>
        Task TranscodeToPngAsync(string inputPath, string outputPngPath, CancellationToken ct);
    }

    /// <summary>
    /// 基于文件路径的帧来源：支持 PNG 直接复制；也支持（可选）将非 PNG 文件转码为 PNG。
    /// Frame source backed by a file path: supports direct PNG copy and optional transcoding for non-PNG inputs.
    /// </summary>
    public sealed class FileFrameSource : IApngFrameSource
    {
        /// <summary>
        /// 原始输入路径（可能是 PNG，也可能是其他格式）。/ Original input path (may be PNG or other formats).
        /// </summary>
        private readonly string _path;
        /// <summary>
        /// 是否允许对非 PNG 的文件路径输入进行转码。/ Whether transcoding non-PNG file inputs is allowed.
        /// </summary>
        private readonly bool _transcodeNonPng;
        /// <summary>
        /// 非 PNG 转 PNG 的转码器（可空）。/ Transcoder used for non-PNG inputs (nullable).
        /// </summary>
        private readonly IPngTranscoder? _transcoder;

        /// <summary>
        /// 从文件路径创建帧来源。
        /// - 当输入是 PNG：会复制/复用为临时帧文件
        /// - 当输入非 PNG：如果允许转码且提供 <paramref name="transcoder"/>，则转码为 PNG；否则抛出 <see cref="NotSupportedException"/>
        ///
        /// Creates a frame source from a file path.
        /// - For PNG inputs: copies/reuses as the temp frame file
        /// - For non-PNG inputs: transcodes to PNG if allowed and <paramref name="transcoder"/> is provided; otherwise throws <see cref="NotSupportedException"/>
        /// </summary>
        /// <param name="path">输入图片路径。/ Input image path.</param>
        /// <param name="transcodeNonPng">是否允许非 PNG 自动转码。/ Allow auto-transcoding for non-PNG inputs.</param>
        /// <param name="transcoder">转码器（可空）。/ Transcoder (nullable).</param>
        public FileFrameSource(string path, bool transcodeNonPng = true, IPngTranscoder? transcoder = null)
        {
            _path = path;
            _transcodeNonPng = transcodeNonPng;
            _transcoder = transcoder;
        }

        /// <summary>
        /// 将输入文件落盘成 PNG 并返回落盘路径。
        /// - PNG：直接复制（若目标与源相同则跳过复制）
        /// - 非 PNG：按配置进行转码
        ///
        /// Materializes the input file into a PNG on disk and returns the resulting path.
        /// - PNG: copied directly (skips copy if source equals target)
        /// - Non-PNG: transcoded based on configuration
        /// </summary>
        /// <param name="directory">输出目录（通常为临时目录）。/ Output directory (usually temp).</param>
        /// <param name="fileNameWithoutExt">目标文件名（不含扩展名）。/ Target filename without extension.</param>
        /// <param name="ct">取消令牌。/ Cancellation token.</param>
        /// <returns>落盘后的 PNG 路径。/ Path to the materialized PNG.</returns>
        public async Task<string> MaterializePngAsync(string directory, string fileNameWithoutExt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(_path))
                throw new ArgumentException(
                    "输入文件路径为空或仅包含空白字符，无法作为帧输入。/ Input file path is empty or whitespace and cannot be used as a frame input.",
                    nameof(_path));
            if (!File.Exists(_path))
                throw new FileNotFoundException(
                    $"找不到输入文件：'{_path}'。请确认路径正确且文件存在。/ Input file not found: '{_path}'. Ensure the path is correct and the file exists.",
                    _path);

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
                throw new NotSupportedException(
                    $"不允许使用非 PNG 文件作为帧输入：'{_path}'。如需自动转码，请启用 Options.TranscodeNonPngInputs=true 并提供 IPngTranscoder。" +
                    $" / Non-PNG input is not allowed: '{_path}'. To auto-convert, enable Options.TranscodeNonPngInputs=true and provide an IPngTranscoder.");

            if (_transcoder is null)
                throw new NotSupportedException(
                    $"非 PNG 输入需要 PNG 转码器，但当前未提供 IPngTranscoder。你可以安装可选包 ApngAsmWrapper.ImageSharp，或实现并注入自定义 IPngTranscoder。输入：'{_path}'。" +
                    $" / Non-PNG input requires a PNG transcoder, but none was provided. Install ApngAsmWrapper.ImageSharp or provide a custom IPngTranscoder. Input: '{_path}'.");

            await _transcoder.TranscodeToPngAsync(_path, target, ct);
            return target;
        }
    }

    /// <summary>
    /// 基于 <see cref="Stream"/> 的帧来源：会将流内容原样写入到 <c>.png</c> 文件。
    /// 注意：该类型不会验证流内容是否真的是 PNG（调用方应确保提供的是 PNG 数据）。
    /// Frame source backed by a <see cref="Stream"/>: writes the stream as-is to a <c>.png</c> file.
    /// Note: this does not validate that the stream is actually PNG; callers must ensure PNG data.
    /// </summary>
    public sealed class StreamFrameSource : IApngFrameSource
    {
        /// <summary>
        /// 输入数据流（应为 PNG 字节）。/ Input stream (should contain PNG bytes).
        /// </summary>
        private readonly Stream _stream;
        /// <summary>
        /// 使用一个包含 PNG 数据的 <see cref="Stream"/> 构造帧来源。
        /// 注意：该类型不验证流内容是否真为 PNG；并且不会拥有/释放该流（调用方管理生命周期）。
        ///
        /// Constructs a frame source from a <see cref="Stream"/> containing PNG bytes.
        /// Note: this type does not validate that the stream is PNG and does not own/dispose the stream (caller manages lifetime).
        /// </summary>
        /// <param name="stream">
        /// 包含 PNG 数据的流。/ Stream containing PNG data.
        /// </param>
        public StreamFrameSource(Stream stream) => _stream = stream;

        /// <summary>
        /// 将流内容写入到目标 <c>.png</c> 文件并返回路径。
        /// 注意：不会校验流内容是否为 PNG；若流可 Seek，会在写入前重置到 0。
        ///
        /// Writes the stream to the target <c>.png</c> file and returns the path.
        /// Note: does not validate PNG format; if the stream is seekable, resets position to 0 before copying.
        /// </summary>
        /// <param name="directory">输出目录（通常为临时目录）。/ Output directory (usually temp).</param>
        /// <param name="fileNameWithoutExt">目标文件名（不含扩展名）。/ Target filename without extension.</param>
        /// <param name="ct">取消令牌。/ Cancellation token.</param>
        /// <returns>落盘后的 PNG 路径。/ Path to the materialized PNG.</returns>
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

    /// <summary>
    /// <see cref="Request"/> 的 Fluent Builder：用于以链式调用的方式配置选项并添加帧，最后 <see cref="Build"/> 生成请求。
    /// Fluent builder for <see cref="Request"/>: configure options and add frames via chaining, then call <see cref="Build"/>.
    /// </summary>
    public sealed partial class Builder
    {
        /// <summary>
        /// 已添加的帧列表（按添加顺序生成）。/ Frames added so far (generated in the same order).
        /// </summary>
        private readonly List<Frame> _frames = new();
        /// <summary>
        /// 当前 Builder 的全局选项快照（可被 <see cref="WithOptions"/> 替换）。/ Current global options snapshot (replaced by <see cref="WithOptions"/>).
        /// </summary>
        private Options _options = new();
        /// <summary>
        /// 是否对非 PNG 的文件路径输入启用转码（builder 内部状态，通常与 <see cref="Options.TranscodeNonPngInputs"/> 同步）。
        /// / Whether transcoding is enabled for non-PNG file-path inputs (builder internal state, usually synced with <see cref="Options.TranscodeNonPngInputs"/>).
        /// </summary>
        private bool _transcodeNonPngInputs = true;
        /// <summary>
        /// PNG 转码器（可选）。/ PNG transcoder (optional).
        /// </summary>
        private IPngTranscoder? _pngTranscoder;
        /// <summary>
        /// apngasm 可执行文件路径。/ apngasm executable path.
        /// </summary>
        private readonly string _apngasmExePath;
        /// <summary>
        /// 输出 APNG 文件路径。/ Output APNG file path.
        /// </summary>
        private readonly string _outputPath;

        /// <summary>
        /// 使用显式的 apngasm 路径与输出路径创建 Builder。
        /// 适用于：你希望完全控制 apngasm 可执行文件位置（例如自定义部署、非默认目录、不同版本等）。
        ///
        /// Creates a builder using an explicit apngasm path and output path.
        /// Use this when you want full control over where apngasm lives (custom deployment/non-default directory/different versions).
        /// </summary>
        /// <param name="apngasmExePath">
        /// apngasm 可执行文件路径。/ Path to apngasm executable.
        /// </param>
        /// <param name="outputApngPath">
        /// 输出 APNG 路径。/ Output APNG path.
        /// </param>
        public Builder(string apngasmExePath, string outputApngPath)
        {
            _apngasmExePath = apngasmExePath;
            _outputPath = outputApngPath;
        }

        /// <summary>
        /// 使用已解析到的 apngasm 可执行文件路径创建 Builder（解析规则见 <see cref="TryResolveApngasmExePath"/>）。
        /// 常用于：你希望使用“随库打包/输出目录中的 apngasm64.exe”，而不手动指定路径。
        ///
        /// Creates a builder using a resolved apngasm executable path (see <see cref="TryResolveApngasmExePath"/> for resolution rules).
        /// Typical use: you want to use the bundled/output <c>apngasm64.exe</c> without manually specifying its path.
        /// </summary>
        public Builder(string outputApngPath)
        {
            _apngasmExePath = ResolveApngasmExePathOrThrow();
            _outputPath = outputApngPath;
        }

        /// <summary>
        /// 设置全局选项（压缩/循环/默认延迟/临时目录/并行落盘等），并同步 Builder 内部的“是否转码非 PNG 文件路径输入”设置。
        /// 提示：该方法会用传入的 options 覆盖之前的 options（builder 是可变的）。
        ///
        /// Sets global options (compression/loop/default delay/temp dir/parallel materialization, etc.)
        /// and syncs the builder’s internal “transcode non-PNG file inputs” flag.
        /// Note: this replaces the previously set options (builder is mutable).
        /// </summary>
        /// <param name="options">
        /// 要应用的全局选项。/ Options to apply.
        /// </param>
        /// <returns>
        /// Builder 本身，便于链式调用。/ The same builder for chaining.
        /// </returns>
        public Builder WithOptions(Options options)
        {
            _options = options;
            _transcodeNonPngInputs = options.TranscodeNonPngInputs;
            return this;
        }

        /// <summary>
        /// 设置 PNG 转码器：当通过文件路径添加帧且输入不是 PNG（jpg/bmp 等）时，用它转码为 PNG。
        /// 注意：它只影响“文件路径”形式的帧（<see cref="AddFrame(string,int?,int?)"/>），不影响 stream/image 等来源。
        ///
        /// Sets the PNG transcoder used when file-path frame inputs are not PNG (jpg/bmp, etc.).
        /// Note: only affects file-path frames (<see cref="AddFrame(string,int?,int?)"/>), not streams/images.
        /// </summary>
        public Builder WithPngTranscoder(IPngTranscoder transcoder)
        {
            _pngTranscoder = transcoder;
            return this;
        }

        /// <summary>
        /// 控制是否允许将非 PNG 的“文件路径帧输入”自动转码为 PNG。
        /// - true：允许（仍需要提供 <see cref="IPngTranscoder"/> 才能真正转码）
        /// - false：遇到非 PNG 文件路径输入会抛异常（更严格、更早失败）
        /// 该设置会影响后续所有 <see cref="AddFrame(string,int?,int?)"/> 调用。
        ///
        /// Controls whether non-PNG file-path inputs should be auto-transcoded to PNG.
        /// - true: allowed (still requires an <see cref="IPngTranscoder"/> to actually transcode)
        /// - false: non-PNG file-path inputs will throw (stricter, fail-fast)
        /// Affects subsequent <see cref="AddFrame(string,int?,int?)"/> calls.
        /// </summary>
        public Builder WithFileInputTranscoding(bool enabled)
        {
            _transcodeNonPngInputs = enabled;
            return this;
        }

        /// <summary>
        /// 添加一帧（文件路径输入），并可选设置逐帧延迟（NUM/DEN，单位：秒）。
        /// - 如果文件是 PNG：将被复制/复用为临时帧输入
        /// - 如果文件不是 PNG：在允许转码且提供转码器时会先转为 PNG；否则抛异常
        ///
        /// Adds a frame from a file path, with an optional per-frame delay override (NUM/DEN seconds).
        /// - PNG inputs are copied/reused as-is
        /// - Non-PNG inputs require transcoding if enabled and a transcoder is provided; otherwise it throws
        /// </summary>
        /// <param name="imagePath">
        /// 图片文件路径。/ Image file path.
        /// </param>
        /// <param name="delayNum">
        /// 延迟分子 NUM（可空）。/ Delay numerator NUM (nullable).
        /// </param>
        /// <param name="delayDen">
        /// 延迟分母 DEN（可空）。/ Delay denominator DEN (nullable).
        /// </param>
        /// <returns>
        /// Builder 本身，便于链式调用。/ The same builder for chaining.
        /// </returns>
        public Builder AddFrame(string imagePath, int? delayNum = null, int? delayDen = null)
        {
            _frames.Add(new Frame(new FileFrameSource(imagePath, _transcodeNonPngInputs, _pngTranscoder)) { DelayNumerator = delayNum, DelayDenominator = delayDen });
            return this;
        }

        /// <summary>
        /// 添加一帧，并以“毫秒”指定延迟；内部会转换为 apngasm 所需的 NUM/DEN（秒）。
        /// Adds a frame and specifies delay in milliseconds; internally converted to apngasm NUM/DEN (seconds).
        /// </summary>
        public Builder AddFrame(string imagePath, int delayMs)
        {
            (int num, int den) = ToApngDelayFractionFromMilliseconds(delayMs);
            return AddFrame(imagePath, num, den);
        }

        /// <summary>
        /// 添加一帧，并以 <see cref="TimeSpan"/> 指定延迟；内部会转换为 apngasm 所需的 NUM/DEN（秒）。
        /// Adds a frame with a <see cref="TimeSpan"/> delay (converted to apngasm NUM/DEN seconds).
        /// </summary>
        public Builder AddFrame(string imagePath, TimeSpan delay)
        {
            (int num, int den) = ToFractionSeconds(delay);
            return AddFrame(imagePath, num, den);
        }

        /// <summary>
        /// 从多个文件路径批量添加帧（不设置逐帧延迟，使用默认延迟规则）。
        /// Adds multiple frames from file paths (no per-frame delay; uses default delay rules).
        /// </summary>
        public Builder AddFrames(IEnumerable<string> imagePaths)
        {
            foreach (string p in imagePaths)
                AddFrame(p);
            return this;
        }

        /// <summary>
        /// 批量添加帧，并为每一帧可选设置延迟（NUM/DEN，秒）。
        /// Adds multiple frames with optional per-frame delays (NUM/DEN seconds).
        /// </summary>
        public Builder AddFrames(IEnumerable<(string Path, int? DelayNum, int? DelayDen)> frames)
        {
            foreach (var f in frames)
                AddFrame(f.Path, f.DelayNum, f.DelayDen);
            return this;
        }

        /// <summary>
        /// 批量添加帧，并为每一帧以“毫秒”设置延迟（内部转换为 NUM/DEN，秒）。
        /// Adds multiple frames with per-frame delays in milliseconds (internally converted to NUM/DEN seconds).
        /// </summary>
        public Builder AddFrames(IEnumerable<(string Path, int DelayMs)> frames)
        {
            foreach (var f in frames)
                AddFrame(f.Path, f.DelayMs);
            return this;
        }

        /// <summary>
        /// 添加一帧（Stream 输入），并可选设置逐帧延迟（NUM/DEN，单位：秒）。
        /// 注意：该 stream 应提供 PNG 数据；本库不会校验其格式。
        ///
        /// Adds a frame from a stream with an optional per-frame delay override (NUM/DEN seconds).
        /// Note: the stream should contain PNG bytes; this library does not validate the format.
        /// </summary>
        /// <param name="pngStream">
        /// PNG 数据流。/ Stream containing PNG bytes.
        /// </param>
        /// <param name="delayNum">
        /// 延迟分子 NUM（可空）。/ Delay numerator NUM (nullable).
        /// </param>
        /// <param name="delayDen">
        /// 延迟分母 DEN（可空）。/ Delay denominator DEN (nullable).
        /// </param>
        /// <returns>
        /// Builder 本身，便于链式调用。/ The same builder for chaining.
        /// </returns>
        public Builder AddFrame(Stream pngStream, int? delayNum = null, int? delayDen = null)
        {
            _frames.Add(new Frame(new StreamFrameSource(pngStream)) { DelayNumerator = delayNum, DelayDenominator = delayDen });
            return this;
        }

        /// <summary>
        /// 添加一帧（Stream 输入），并以“毫秒”指定延迟；内部会转换为 NUM/DEN（秒）。
        /// Adds a frame (stream input) with delay in milliseconds; internally converted to NUM/DEN seconds.
        /// </summary>
        public Builder AddFrame(Stream pngStream, int delayMs)
        {
            (int num, int den) = ToApngDelayFractionFromMilliseconds(delayMs);
            return AddFrame(pngStream, num, den);
        }

        /// <summary>
        /// 添加一帧（Stream 输入），并以 <see cref="TimeSpan"/> 指定延迟（转换为 NUM/DEN，秒）。
        /// Adds a frame (stream input) with a <see cref="TimeSpan"/> delay (converted to NUM/DEN seconds).
        /// </summary>
        public Builder AddFrame(Stream pngStream, TimeSpan delay)
        {
            (int num, int den) = ToFractionSeconds(delay);
            return AddFrame(pngStream, num, den);
        }

        /// <summary>
        /// 从多个 PNG Stream 批量添加帧（注意：不验证内容是否真为 PNG）。
        /// Adds multiple frames from PNG streams (note: stream contents are not validated as PNG).
        /// </summary>
        public Builder AddFrames(IEnumerable<Stream> pngStreams)
        {
            foreach (Stream s in pngStreams)
                AddFrame(s);
            return this;
        }

        /// <summary>
        /// 构建 <see cref="Request"/>（不可变）对象，用于后续生成。
        /// 注意：该方法不执行 I/O，不会验证路径/帧是否可用；验证与落盘在生成阶段进行。
        ///
        /// Builds an immutable <see cref="Request"/> for later generation.
        /// Note: this performs no I/O and does not validate paths/frames; validation/materialization happens during generation.
        /// </summary>
        /// <returns>
        /// 构建好的请求对象。/ The constructed request.
        /// </returns>
        public Request Build() => new(_apngasmExePath, _outputPath, _frames.ToArray(), _options);
    }

    internal static (int Numerator, int Denominator) ToFractionSeconds(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay),
                "延迟必须为正数（> 0）。/ Delay must be positive (> 0).");

        long ms = (long)Math.Round(delay.TotalMilliseconds);
        if (ms <= 0) ms = 1;
        return ToApngDelayFractionFromMillisecondsCore(ms);
    }

    public static (int Numerator, int Denominator) ToApngDelayFractionFromMilliseconds(int delayMs)
    {
        // APNG delay is stored as two unsigned 16-bit integers (NUM/DEN seconds).
        // Keep values within 1..65535 to avoid encoder/decoder issues.
        if (delayMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(delayMs),
                "延迟毫秒数必须为正数（> 0）。/ Delay must be positive milliseconds (> 0).");

        return ToApngDelayFractionFromMillisecondsCore(delayMs);
    }

    private static (int Numerator, int Denominator) ToApngDelayFractionFromMillisecondsCore(long delayMs)
    {
        if (delayMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(delayMs),
                "延迟毫秒数必须为正数（> 0）。/ Delay must be positive milliseconds (> 0).");

        long num = delayMs;
        long den = 1000;
        long g = Gcd(num, den);
        num /= g;
        den /= g;

        const int Max = 65535;
        if (num <= Max && den <= Max)
            return ((int)num, (int)den);

        // Scale down both sides to fit, preserving ratio as closely as possible.
        long scaleA = (num + Max - 1) / Max;
        long scaleB = (den + Max - 1) / Max;
        long scale = Math.Max(scaleA, scaleB);
        if (scale < 1) scale = 1;

        num = (num + scale - 1) / scale;
        den = (den + scale - 1) / scale;

        if (num < 1) num = 1;
        if (den < 1) den = 1;
        if (num > Max) num = Max;
        if (den > Max) den = Max;

        g = Gcd(num, den);
        num /= g;
        den /= g;

        return ((int)num, (int)den);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            long t = a % b;
            a = b;
            b = t;
        }
        return Math.Abs(a);
    }

    /// <summary>
    /// 使用指定的 <see cref="Request"/> 生成 APNG（最完整/最可控的入口）。
    /// 你可以在请求中显式指定 apngasm 路径、输出路径、帧列表与选项；返回值 <see cref="Result"/> 包含详细诊断信息。
    ///
    /// Generates an APNG using the provided <see cref="Request"/> (the most explicit/controllable entry point).
    /// You can specify apngasm path, output path, frames and options; the returned <see cref="Result"/> contains rich diagnostics.
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
    /// 便捷 API：自动解析 apngasm 可执行文件路径（随库打包/输出目录/环境变量），然后生成 APNG。
    /// 适用于大多数“默认部署方式”的场景；如需完全控制 apngasm 路径请使用 <see cref="GenerateAsync(Request, CancellationToken, IApngasmProcessRunner?, IApngLogger?)"/>。
    ///
    /// Convenience API: resolves the apngasm executable path (bundled/output/env-var) and generates an APNG.
    /// Suitable for most default deployments; use <see cref="GenerateAsync(Request, CancellationToken, IApngasmProcessRunner?, IApngLogger?)"/> for full control.
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
            return Result.Fail(
                $"找不到 apngasm 可执行文件：'{req.ApngasmExePath}'。请确认文件存在，或通过 APNGASM_EXE / Request.ApngasmExePath 指定正确路径。" +
                $" / apngasm executable not found: '{req.ApngasmExePath}'. Ensure the file exists, or specify it via APNGASM_EXE / Request.ApngasmExePath.",
                null, "", "", "", null, null);
        if (string.IsNullOrWhiteSpace(req.OutputApngPath))
            return Result.Fail(
                "输出路径为空，无法生成 APNG。/ Output path is empty and APNG cannot be generated.",
                null, "", "", "", null, null);
        if (req.Frames is null || req.Frames.Count == 0)
            return Result.Fail(
                "至少需要 1 帧才能生成 APNG。/ At least one frame is required to generate an APNG.",
                null, "", "", "", null, null);
        if (req.Options.LoopCount < 0)
            return Result.Fail(
                "LoopCount 必须 >= 0（0 表示无限循环）。/ LoopCount must be >= 0 (0 means infinite loop).",
                null, "", "", "", null, null);
        if (req.Options.HorizontalStripFrames is not null && req.Options.VerticalStripFrames is not null)
            return Result.Fail(
                "不能同时使用 -hs 与 -vs（水平/垂直条带输入互斥）。/ Cannot use -hs and -vs together (horizontal/vertical strip inputs are mutually exclusive).",
                null, "", "", "", null, null);

        string tempRoot = string.IsNullOrWhiteSpace(req.Options.TempDirectoryRoot) ? Path.GetTempPath() : req.Options.TempDirectoryRoot!;
        string tmpDir = Path.Combine(tempRoot, "ApngGenerator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Ensure output directory exists.
            string? outDir = Path.GetDirectoryName(req.OutputApngPath);
            if (!string.IsNullOrWhiteSpace(outDir))
                Directory.CreateDirectory(outDir);

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
            if (exit == 0)
                return Result.Ok(cmd, stdout, stderr, req.Options.KeepTempFiles ? tmpDir : null, materialized);

            string hint =
                "如果持续失败：建议设置 Options.KeepTempFiles=true 以保留临时目录，检查落盘帧 PNG 与 frameXX.txt(delay) 是否正确，并查看 Result.StandardError / Result.CommandLine 以定位原因。" +
                " / If it keeps failing: set Options.KeepTempFiles=true to keep the temp directory, inspect materialized PNG frames and frameXX.txt (delay) files, and review Result.StandardError / Result.CommandLine for troubleshooting.";
            return Result.Fail($"apngasm 执行失败（exit={exit}）。{hint} / apngasm failed (exit={exit}). {hint}", exit, cmd, stdout, stderr, req.Options.KeepTempFiles ? tmpDir : null, materialized);
        }
        catch (OperationCanceledException)
        {
            return Result.Fail(
                "操作已取消（CancellationToken 触发）。/ Operation was cancelled (CancellationToken triggered).",
                null, "", "", "", req.Options.KeepTempFiles ? tmpDir : null, null);
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
                throw new ArgumentException(
                    $"第 {index} 帧的延迟设置无效：必须同时提供正数的分子与分母（NUM/DEN，秒）。" +
                    $" / Invalid delay for Frame[{index}]: requires both positive numerator and denominator (NUM/DEN seconds).");
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

