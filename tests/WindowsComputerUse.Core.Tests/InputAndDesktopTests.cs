using System.Drawing;
using System.Drawing.Imaging;
using WindowsComputerUse.Core.Capture;
using WindowsComputerUse.Core.Dpi;
using WindowsComputerUse.Core.Uia;
using WindowsComputerUse.Core.Win32;

namespace WindowsComputerUse.Core.Tests;

public sealed class InputAndDesktopTests
{
    [Theory]
    [InlineData("Ctrl", 0xA2, false)]
    [InlineData("Shift", 0xA0, false)]
    [InlineData("Tab", 0x09, false)]
    [InlineData("Enter", 0x0D, false)]
    [InlineData("Win", 0x5B, true)]
    [InlineData("F5", 0x74, false)]
    [InlineData("S", (ushort)'S', false)]
    public void TryParseVirtualKey_ParsesExpectedKeys(string name, ushort expectedVk, bool expectedExt)
    {
        Assert.True(InputService.TryParseVirtualKey(name, out ushort vk, out bool ext));
        Assert.Equal(expectedVk, vk);
        Assert.Equal(expectedExt, ext);
    }

    [Fact]
    public void TryParseVirtualKey_RejectsUnknownKey()
    {
        Assert.False(InputService.TryParseVirtualKey("NOT_A_KEY", out _, out _));
    }

    [Fact]
    public void ProcessBitmap_DownscalesEncodesPngAndCachesTransform()
    {
        var capture = new ScreenCaptureService();
        var rect = new RectI(0, 0, 2560, 1440);
        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);

        var shot = capture.ProcessBitmap(bmp, rect, rect, [], maxWidth: 1280, format: CaptureFormat.Png);
        Assert.Equal(1280, shot.Transform.ImageWidth);
        Assert.Equal(720, shot.Transform.ImageHeight);
        Assert.Equal("image/png", shot.MimeType);
        Assert.True(shot.ImageBytes.Length > 8);
        Assert.Equal((byte)0x89, shot.ImageBytes[0]);
        Assert.Equal((byte)0x50, shot.ImageBytes[1]);
        Assert.Equal((byte)0x4E, shot.ImageBytes[2]);
        Assert.Equal((byte)0x47, shot.ImageBytes[3]);

        Assert.Same(shot.Transform, capture.GetTransform(shot.Transform.CaptureId));
        Assert.Same(shot.Transform, capture.GetTransform(null));

        var jpegShot = capture.ProcessBitmap(bmp, rect, rect, [], maxWidth: 1280, format: CaptureFormat.Jpeg);
        Assert.Equal("image/jpeg", jpegShot.MimeType);
        Assert.True(jpegShot.ImageBytes.Length > 4);
        Assert.Equal((byte)0xFF, jpegShot.ImageBytes[0]);
        Assert.Equal((byte)0xD8, jpegShot.ImageBytes[1]);
        Assert.Equal((byte)0xFF, jpegShot.ImageBytes[2]);
    }

    [Fact]
    public void LiveDesktop_DpiAndWindowList_DoNotThrow()
    {
        DpiAwareness.EnsurePerMonitorV2();
        var monitors = DpiAwareness.GetMonitors();
        Assert.NotEmpty(monitors);
        Assert.All(monitors, m => Assert.True(m.Bounds.Width > 0 && m.DpiX >= 96));

        var windows = WindowService.ListWindows(includeMinimized: true);
        Assert.NotNull(windows);
    }

    [Fact]
    public void UiaService_UnknownElementId_ThrowsStaleElementException()
    {
        var uia = new UiaService();
        Assert.Throws<StaleElementException>(() => uia.GetOwnerWindowHwnd("uia_missing"));
        Assert.Throws<StaleElementException>(() => uia.GetTree(0, rootElementId: "uia_missing"));
        Assert.Throws<StaleElementException>(() => uia.PerformAction("uia_missing", UiaActionKind.Invoke));
    }
}
