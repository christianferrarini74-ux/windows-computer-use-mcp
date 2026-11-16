using WindowsComputerUse.Core.Dpi;

namespace WindowsComputerUse.Core.Tests;

public sealed class CoordinateTransformTests
{
    [Fact]
    public void ImageToScreen_Downscaled125PercentDisplay_MapsAccurately()
    {
        var virtualScreen = new RectI(0, 0, 2560, 1440);
        var transform = new CoordinateTransform(
            CaptureId: "cap_test",
            OriginX: 0,
            OriginY: 0,
            CaptureWidth: 2560,
            CaptureHeight: 1440,
            ImageWidth: 1280,
            ImageHeight: 720,
            VirtualScreenBounds: virtualScreen);

        Assert.Equal(2.0, transform.ScaleX, precision: 6);
        Assert.Equal(2.0, transform.ScaleY, precision: 6);

        var (sx, sy) = transform.ImageToScreen(640, 360);
        Assert.Equal((1280, 720), (sx, sy));

        var (ix, iy) = transform.ScreenToImage(1280, 720);
        Assert.Equal(640.0, ix, precision: 6);
        Assert.Equal(360.0, iy, precision: 6);
    }

    [Fact]
    public void ImageToScreen_WindowOriginOffset_AddsOriginAndClamps()
    {
        var virtualScreen = new RectI(0, 0, 2560, 1440);
        var transform = new CoordinateTransform(
            CaptureId: "cap_win",
            OriginX: 300,
            OriginY: 200,
            CaptureWidth: 1000,
            CaptureHeight: 800,
            ImageWidth: 500,
            ImageHeight: 400,
            VirtualScreenBounds: virtualScreen);

        Assert.Equal((320, 240), transform.ImageToScreen(10, 20));
        // Clamped to window bounds [300..1299, 200..999]
        Assert.Equal((1299, 999), transform.ImageToScreen(9999, 9999));
    }

    [Fact]
    public void ScreenToSendInputNormalized_MultiMonitorNegativeOrigin_MapsFullRange()
    {
        // Secondary 1920x1080 on the left (-1920, 0), Primary 2560x1440 on the right (0, 0)
        var virtualScreen = new RectI(-1920, 0, 4480, 1440);

        Assert.Equal((0, 0), CoordinateTransform.ScreenToSendInputNormalized(-1920, 0, virtualScreen));
        Assert.Equal((65535, 65535), CoordinateTransform.ScreenToSendInputNormalized(2559, 1439, virtualScreen));

        // Clamping out-of-range coordinates
        Assert.Equal((0, 0), CoordinateTransform.ScreenToSendInputNormalized(-5000, -100, virtualScreen));
        Assert.Equal((65535, 65535), CoordinateTransform.ScreenToSendInputNormalized(9999, 9999, virtualScreen));
    }
}
