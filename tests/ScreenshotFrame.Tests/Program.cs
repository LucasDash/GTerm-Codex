using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

Assembly gterm = Assembly.Load("GTerm");
Type guard = gterm.GetType("GTerm.MCP.ScreenshotFrame", true)!;
Type capturer = gterm.GetType("GTerm.MCP.ScreenshotCapturer", true)!;
const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
MethodInfo validate = guard.GetMethod("ValidateHeader", flags)!;
MethodInfo read = guard.GetMethod("ReadRelayAsync", flags)!;
MethodInfo crop = capturer.GetMethod("CropStoredFrame", flags)!;
MethodInfo enlarge = capturer.GetMethod("Enlarge", flags)!;
int count = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
bool Reject(Action action) { try { action(); return false; } catch { return true; } }
void Validate(byte[] bytes, int w, int h) => validate.Invoke(null, [bytes, w, h]);
async Task<byte[]> Read(string path, int w, int h) => await (Task<byte[]>)read.Invoke(null, [path, w, h, CancellationToken.None])!;

using Image<Rgb24> image = new(32, 16);
for (int y = 0; y < 16; y++)
    for (int x = 0; x < 32; x++)
        image[x, y] = x < 16 ? new Rgb24(240, 20, 20) : new Rgb24(20, 20, 240);
using MemoryStream jpegStream = new();
image.SaveAsJpeg(jpegStream);
byte[] jpeg = jpegStream.ToArray();
using MemoryStream pngStream = new();
image.SaveAsPng(pngStream);
using MemoryStream tiffStream = new();
image.SaveAsTiff(tiffStream);

Validate(jpeg, 32, 16); Check(true, "valid JPEG");
Check(Reject(() => Validate(pngStream.ToArray(), 32, 16)), "PNG rejected");
Check(Reject(() => Validate(tiffStream.ToArray(), 32, 16)), "TIFF rejected");
Check(Reject(() => Validate(jpeg[..8], 32, 16)), "truncated header rejected");
Check(Reject(() => Validate([], 32, 16)), "empty rejected");
Check(Reject(() => Validate(jpeg, 16, 16)), "mismatched dimensions rejected");
Check(Reject(() => Validate(jpeg, 16385, 1)), "dimension cap enforced");
Check(Reject(() => Validate(jpeg, 8193, 8193)), "pixel cap enforced");
Check(Reject(() => Validate(jpeg, 0, 16)), "zero dimension rejected");

object cropResult = crop.Invoke(null, [jpeg, 32, 16, 8, 4, 8, 4, true, 0.0])!;
Check((bool)cropResult.GetType().GetProperty("Success")!.GetValue(cropResult)!, "valid cached crop");
var cropped = (byte[])cropResult.GetType().GetProperty("Jpeg")!.GetValue(cropResult)!;
using Image<Rgb24> decodedCrop = Image.Load<Rgb24>(cropped);
Check(decodedCrop.Width > 0 && decodedCrop.Height > 0, "crop output decodes");
Rgb24 cropCenter = decodedCrop[decodedCrop.Width / 2, decodedCrop.Height / 2];
Check(cropCenter.R > cropCenter.B + 100, "crop uses requested part of image");
object badCrop = crop.Invoke(null, [tiffStream.ToArray(), 32, 16, 0, 0, 8, 8, false, 0.0])!;
Check(!(bool)badCrop.GetType().GetProperty("Success")!.GetValue(badCrop)!, "cached TIFF rejected");
object enlarged = enlarge.Invoke(null, [jpeg, 32, 16])!;
var output = ((byte[] Bytes, double Scale, int Width, int Height))enlarged;
using Image<Rgb24> decodedEnlarged = Image.Load<Rgb24>(output.Bytes);
Check(decodedEnlarged.Width == output.Width && decodedEnlarged.Height == output.Height && output.Scale > 1, "region enlargement dimensions");

string file = Path.Combine(Path.GetTempPath(), "gterm-shot-test-" + Guid.NewGuid().ToString("N"));
try
{
    await File.WriteAllBytesAsync(file, jpeg);
    Check((await Read(file, 32, 16)).SequenceEqual(jpeg), "valid relay read");
    await File.WriteAllBytesAsync(file, pngStream.ToArray());
    bool rejected = false;
    try { await Read(file, 32, 16); } catch { rejected = true; }
    Check(rejected, "non-JPEG relay rejected");
    using (FileStream oversized = new(file, FileMode.Create, FileAccess.Write)) oversized.SetLength(64L * 1024 * 1024 + 1);
    rejected = false;
    try { await Read(file, 32, 16); } catch { rejected = true; }
    Check(rejected, "oversize relay rejected before allocation");
}
finally { File.Delete(file); }
Console.WriteLine($"Screenshot frame: {count} checks passed.");
