using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace GTerm.MCP
{
    /// <summary>Limits game-produced screenshot data before image processing.</summary>
    internal static class ScreenshotFrame
    {
        // Allows an 8K screenshot with headroom, while preventing unbounded reads or full pixel decodes.
        internal const int MaxEncodedBytes = 64 * 1024 * 1024;
        internal const int MaxDimension = 16384;
        internal const long MaxPixels = 64L * 1024 * 1024;
        private static readonly Configuration JpegConfiguration = new(new JpegConfigurationModule());
        private static readonly DecoderOptions Options = new()
        {
            Configuration = JpegConfiguration,
            SkipMetadata = true,
            MaxFrames = 1,
        };

        internal static void ValidateHeader(byte[] bytes, int expectedWidth, int expectedHeight)
        {
            if (bytes.Length == 0 || bytes.Length > MaxEncodedBytes)
                throw new InvalidDataException("Screenshot is empty or exceeds the encoded size limit.");
            if (expectedWidth <= 0 || expectedHeight <= 0 || expectedWidth > MaxDimension ||
                expectedHeight > MaxDimension || (long)expectedWidth * expectedHeight > MaxPixels)
                throw new InvalidDataException("Screenshot dimensions exceed the capture limit.");

            // Configuration has only a JPEG detector and decoder: a different format cannot
            // reach another decoder merely by being stored in the relay's .jpg file.
            ImageInfo info = Image.Identify(Options, bytes);
            if (info.Width != expectedWidth || info.Height != expectedHeight)
                throw new InvalidDataException("Screenshot dimensions do not match the game capture.");
        }

        internal static Image<Rgb24> Load(byte[] bytes, int width, int height)
        {
            ValidateHeader(bytes, width, height);
            return Image.Load<Rgb24>(Options, bytes);
        }

        internal static async Task<byte[]> ReadRelayAsync(string path, int width, int height, CancellationToken cancellationToken)
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            long length = stream.Length;
            if (length <= 0 || length > MaxEncodedBytes)
                throw new InvalidDataException("Screenshot is empty or exceeds the encoded size limit.");
            byte[] bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            ValidateHeader(bytes, width, height);
            return bytes;
        }
    }
}
