using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace ApngAsmWrapper.ImageSharp;

/// <summary>
/// ImageSharp-based PNG transcoder for <see cref="ApngAsmWrapper.ApngGenerator.FileFrameSource"/>.
/// </summary>
public sealed class ImageSharpPngTranscoder : ApngAsmWrapper.ApngGenerator.IPngTranscoder
{
    public async Task TranscodeToPngAsync(string inputPath, string outputPngPath, CancellationToken ct)
    {
        await using FileStream input = File.OpenRead(inputPath);
        using Image image = await Image.LoadAsync(input, ct);
        var encoder = new PngEncoder();
        await image.SaveAsPngAsync(outputPngPath, encoder, ct);
    }
}

