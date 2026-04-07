namespace ApngAsmWrapper.ImageSharp;

/// <summary>
/// Convenience extensions to enable ImageSharp transcoding.
/// </summary>
public static class ApngGeneratorImageSharpExtensions
{
    /// <summary>
    /// Configures the builder to automatically transcode non-PNG file inputs to PNG using ImageSharp.
    /// </summary>
    public static ApngAsmWrapper.ApngGenerator.Builder WithImageSharpTranscoding(this ApngAsmWrapper.ApngGenerator.Builder builder)
    {
        return builder.WithPngTranscoder(new ImageSharpPngTranscoder());
    }
}

