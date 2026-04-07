## ApngAsmWrapper.ImageSharp

这是 `ApngAsmWrapper` 的可选扩展包：提供 **ImageSharp** 实现，用于把 `jpg/bmp/gif/...` 自动转成 PNG。

This is the optional addon package for `ApngAsmWrapper` that uses **ImageSharp** to automatically transcode non-PNG inputs to PNG.

### Install

```bash
dotnet add package ApngAsmWrapper
dotnet add package ApngAsmWrapper.ImageSharp
```

### Usage

```csharp
using ApngAsmWrapper;
using ApngAsmWrapper.ImageSharp;

var req = new ApngGenerator.Builder(outputApngPath)
  .WithOptions(new ApngGenerator.Options { TranscodeNonPngInputs = true })
  .WithImageSharpTranscoding()
  .AddFrame(@"C:\frames\a.jpg")
  .AddFrame(@"C:\frames\b.png", 3, 1)
  .Build();

var result = await ApngGenerator.GenerateAsync(req);
```

