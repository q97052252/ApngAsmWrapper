## ApngAsmWrapper

基于 `apngasm`（APNG Assembler）命令行工具的 **C# 高复用封装库**。

Reusable C# wrapper around the `apngasm` (APNG Assembler) command line tool.

---

## 功能概览 / What it does

- **多帧 APNG 合成**：支持任意帧数输入
- **逐帧延迟**：通过写入 `frameXX.txt`（`delay=NUM/DEN`）覆盖每一帧延迟
- **循环次数**：`LoopCount` → `-l#`
- **跳过首帧**：`SkipFirstFrame` → `-f`（常用于首帧静态兜底）
- **压缩方式**：`CompressionMode` → `-z0/-z1/-z2`
- **可诊断**：返回 `Result`（命令行、stdout、stderr、exit code）
- **可扩展**：`Options.ExtraArgs` 可透传任何 apngasm 原始参数

---

## 安装 / Install

```bash
dotnet add package ApngAsmWrapper
```

### 可选扩展：自动转码（ImageSharp addon）/ Optional addon: auto-transcode via ImageSharp

如果你需要把 `jpg/bmp/gif/...` 自动转成 PNG（让 `.AddFrame(\"x.jpg\")` 也能直接用），请额外安装：

If you want to automatically convert non-PNG inputs to PNG, install:

```bash
dotnet add package ApngAsmWrapper.ImageSharp
```

然后在构建请求时启用：

```csharp
using ApngAsmWrapper;
using ApngAsmWrapper.ImageSharp;

var req = new ApngGenerator.Builder(outputApngPath)
  .WithOptions(new ApngGenerator.Options { TranscodeNonPngInputs = true })
  .WithImageSharpTranscoding()
  .AddFrame(@"C:\frames\a.jpg")
  .AddFrame(@"C:\frames\b.png", 3, 1)
  .Build();
```

---

## apngasm64.exe（自动携带 & 可覆盖）/ Bundled apngasm64.exe (auto + override)

### 默认行为 / Default behavior

本包会将 `apngasm64.exe` 随 NuGet 一起打包，并通过 `buildTransitive` 在引用方编译时自动复制到输出目录：

`bin\...\apngasm64.exe`

The package bundles `apngasm64.exe` and automatically copies it into the consumer project's output folder via `buildTransitive`.

### 覆盖路径 / Override exe path

如果你需要使用自定义路径（例如你自己部署的 apngasm），设置环境变量：

If you want to use your own apngasm build, set:

- `APNGASM_EXE` = full path to `apngasm64.exe`

---

## 快速开始：3 帧示例 / Quickstart: 3-frame example

下面示例会生成 `out.png`，动画帧为：`frame1 -> frame2`，并且循环一次（`LoopCount=1`）。
注意：Windows 自带图片查看器可能不播放 APNG，建议用 Chrome/Edge 验证。

This sample generates `out.png` with animation frames `frame1 -> frame2` and loops once. Use Chrome/Edge to view.

```csharp
using ApngAsmWrapper;

string outputApngPath = @"C:\out\out.png";

var options = new ApngGenerator.Options
{
    SkipFirstFrame = true,                  // -f
    LoopCount = 1,                          // -l1
    CompressionMode = ApngGenerator.Compression.SevenZip, // -z1
};

var req = new ApngGenerator.Builder(outputApngPath) // auto-resolve apngasm64.exe
    .WithOptions(options)
    .AddFrame(@"C:\frames\frame0.png")          // 静态兜底帧 / static fallback frame
    .AddFrame(@"C:\frames\frame1.png", 3, 1)    // 3 秒 / 3 seconds
    .AddFrame(@"C:\frames\frame2.png", 1, 1)    // 1 秒 / 1 second
    .Build();

ApngGenerator.Result result = await ApngGenerator.GenerateAsync(req);
if (!result.Success)
    throw new Exception(result.ErrorMessage + "\n" + result.StandardError + "\n" + result.CommandLine);
```

---

## 用 Stream 输入帧 / Frames from Stream

```csharp
using ApngAsmWrapper;

await using var s0 = File.OpenRead(@"C:\frames\frame0.png");
await using var s1 = File.OpenRead(@"C:\frames\frame1.png");
await using var s2 = File.OpenRead(@"C:\frames\frame2.png");

var frames = new[]
{
    new ApngGenerator.Frame(new ApngGenerator.StreamFrameSource(s0)),
    new ApngGenerator.Frame(new ApngGenerator.StreamFrameSource(s1)) { DelayNumerator = 3, DelayDenominator = 1 },
    new ApngGenerator.Frame(new ApngGenerator.StreamFrameSource(s2)) { DelayNumerator = 1, DelayDenominator = 1 },
};

var result = await ApngGenerator.GenerateAsync(@"C:\out\out.png", frames,
    options: new ApngGenerator.Options { SkipFirstFrame = true, LoopCount = 1 });
```

---

## WinForms/WPF：直接用 Image（仅 Windows）/ Using System.Drawing Image (Windows-only)

在 `net10.0-windows` 目标框架下，你可以直接 `.AddFrame(Image)`：

```csharp
using ApngAsmWrapper;
using System.Drawing;

using Image img0 = Image.FromFile(@"C:\frames\frame0.png");
using Image img1 = Image.FromFile(@"C:\frames\frame1.png");

var req = new ApngGenerator.Builder(@"C:\out\out.png")
    .WithOptions(new ApngGenerator.Options { SkipFirstFrame = true, LoopCount = 1 })
    .AddFrame(img0)
    .AddFrame(img1, 3, 1)
    .Build();

var result = await ApngGenerator.GenerateAsync(req);
```

你也可以用 `TimeSpan` 传延迟（内部会自动转换为 NUM/DEN 秒）：

You can also provide delays via `TimeSpan`:

```csharp
var req = new ApngGenerator.Builder(outputApngPath)
  .WithOptions(options)
  .AddFrame(@"C:\frames\frame0.png")
  .AddFrame(@"C:\frames\frame1.png", TimeSpan.FromSeconds(3))
  .AddFrame(@"C:\frames\frame2.png", TimeSpan.FromSeconds(1))
  .Build();
```

---

## 错误诊断 / Troubleshooting

当 `Success=false` 时，优先看：

When `Success=false`, check:

- `Result.ErrorMessage`
- `Result.StandardError`
- `Result.StandardOutput`
- `Result.CommandLine`

常见原因 / Common causes:

- **找不到 apngasm64.exe**：确认输出目录存在 `apngasm64.exe`，或设置 `APNGASM_EXE`
- **输入不是 PNG**：如果你安装了 `ApngAsmWrapper.ImageSharp` 并启用 `.WithImageSharpTranscoding()`，则可自动转码；否则会提示你提供 `IPngTranscoder`

---

## CI/CD（GitHub Actions → NuGet）

本仓库包含：
- `CI`：push/PR 自动 build + test
- `Publish NuGet`：打 tag `v*.*.*`（或手动触发）→ pack → push 到 NuGet

启用发布：
1. 仓库 Settings → Secrets and variables → Actions → 添加 `NUGET_API_KEY`
2. 推送 tag（例如 `v0.1.2`）

