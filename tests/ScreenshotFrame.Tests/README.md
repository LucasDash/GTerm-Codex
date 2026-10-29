# Screenshot frame checks

Run `dotnet run --project tests/ScreenshotFrame.Tests` from the repository root.
The harness creates local JPEG, PNG and TIFF images, exercises the screenshot
guard and cached cropping, and checks relay size and dimensions. It is not a
GMod capture test or a security proof. Header identification does not prove a
JPEG's entropy data is well-formed; the passthrough path is not fully decoded.
Passthrough JPEG bytes and their metadata are not stripped. ImageSharp 3.1.12
security advisories remain unresolved.
