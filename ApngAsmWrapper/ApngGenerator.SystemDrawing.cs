using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ApngAsmWrapper;

public static partial class ApngGenerator
{
    /// <summary>
    /// Frame source backed by an <see cref="Image"/> (Windows / System.Drawing).
    /// </summary>
    public sealed class ImageFrameSource : IApngFrameSource
    {
        private readonly Image _image;
        public ImageFrameSource(Image image) => _image = image;

        public Task<string> MaterializePngAsync(string directory, string fileNameWithoutExt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.Combine(directory, fileNameWithoutExt + ".png");
            _image.Save(target, ImageFormat.Png);
            return Task.FromResult(target);
        }
    }

    public sealed partial class Builder
    {
        /// <summary>
        /// Adds a frame from a <see cref="Image"/> (Windows-only overload).
        /// </summary>
        public Builder AddFrame(Image image, int? delayNum = null, int? delayDen = null)
        {
            _frames.Add(new Frame(new ImageFrameSource(image)) { DelayNumerator = delayNum, DelayDenominator = delayDen });
            return this;
        }
    }
}
