## ApngAsmWrapper

Reusable C# wrapper around the `apngasm` (APNG Assembler) command line tool.

### What this library does

- Builds correct `apngasm` command line arguments (loops, skip-first, compression, etc.)
- Materializes frames (from file paths, `Stream`, or `Image` on Windows) to a temp folder
- Writes per-frame delay override files (`frameXX.txt` with `delay=NUM/DEN`)
- Executes `apngasm64.exe` and returns a rich result (exit code, stdout, stderr, command line)

### Install

```bash
dotnet add package ApngAsmWrapper
```

### Bundled `apngasm64.exe` (optional)

This package can bundle `apngasm64.exe` and automatically copy it to the consumer project's output directory (via `buildTransitive`).

If you want to override the path, set environment variable `APNGASM_EXE` to a full exe path.

### Basic usage (any number of frames)

```csharp
using ApngAsmWrapper;

var options = new ApngGenerator.Options
{
    SkipFirstFrame = true,
    LoopCount = 1,
    CompressionMode = ApngGenerator.Compression.SevenZip,
};

var req = new ApngGenerator.Builder(outputApngPath) // uses resolved apngasm64.exe
    .WithOptions(options)
    .AddFrame(@"C:\frames\frame00.png")       // static fallback (skipped when -f)
    .AddFrame(@"C:\frames\frame01.png", 3, 1) // 3 seconds
    .AddFrame(@"C:\frames\frame02.png", 1, 1) // 1 second
    .Build();

ApngGenerator.Result result = await ApngGenerator.GenerateAsync(req);
if (!result.Success)
    throw new Exception(result.ErrorMessage + "\n" + result.StandardError);
```

### CI/CD (GitHub Actions → NuGet)

This repo includes:
- `CI` workflow: build + test on push / PR
- `Publish NuGet` workflow: packs and pushes to NuGet on tag `v*.*.*` or manual run

To enable publishing:
1. In GitHub repo settings, add Actions secret **`NUGET_API_KEY`**
2. Create a tag like `v0.1.1` and push it

