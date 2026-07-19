using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class BackdropBlurTests
{
    [Fact]
    public void BlurBgra32_ZeroRadiusReturnsIndependentExactCopy()
    {
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8];

        var result = BackdropBlur.BlurBgra32(
            source,
            width: 2,
            height: 1,
            stride: 8,
            radius: 0);

        Assert.Equal(source, result);
        Assert.NotSame(source, result);
    }

    [Fact]
    public void BlurBgra32_BlursHorizontalPixelsWithClippedEdges()
    {
        var source = CreateGrayPixels(0, 30, 90);

        var result = BackdropBlur.BlurBgra32(
            source,
            width: 3,
            height: 1,
            stride: 12,
            radius: 1);

        Assert.Equal(new byte[] { 15, 40, 60 }, ReadBlueChannel(result, 3, 1, 12));
        Assert.All(ReadAlphaChannel(result, 3, 1, 12), value => Assert.Equal(255, value));
    }

    [Fact]
    public void BlurBgra32_BlursVerticalPixelsWithClippedEdges()
    {
        var source = CreateGrayPixels(0, 30, 90);

        var result = BackdropBlur.BlurBgra32(
            source,
            width: 1,
            height: 3,
            stride: 4,
            radius: 1);

        Assert.Equal(new byte[] { 15, 40, 60 }, ReadBlueChannel(result, 1, 3, 4));
    }

    [Fact]
    public void BlurBgra32_BlursEveryBgraChannelIndependently()
    {
        byte[] source =
        [
            0, 100, 200, 50,
            100, 0, 100, 150,
            200, 100, 0, 250
        ];

        var result = BackdropBlur.BlurBgra32(
            source,
            width: 3,
            height: 1,
            stride: 12,
            radius: 1);

        Assert.Equal(
            new byte[]
            {
                50, 50, 150, 100,
                100, 67, 100, 150,
                150, 50, 50, 200
            },
            result);
    }

    [Fact]
    public void BlurBgra32_MultiplePassesMatchRepeatedSinglePasses()
    {
        var source = CreateGrayPixels(0, 20, 80, 160, 240);

        var twoPassResult = BackdropBlur.BlurBgra32(
            source,
            width: 5,
            height: 1,
            stride: 20,
            radius: 1,
            passes: 2);
        var firstPass = BackdropBlur.BlurBgra32(
            source,
            width: 5,
            height: 1,
            stride: 20,
            radius: 1);
        var repeatedResult = BackdropBlur.BlurBgra32(
            firstPass,
            width: 5,
            height: 1,
            stride: 20,
            radius: 1);

        Assert.Equal(repeatedResult, twoPassResult);
        Assert.NotEqual(firstPass, twoPassResult);
    }

    [Fact]
    public void BlurBgra32_PreservesPaddingAndDoesNotModifySource()
    {
        byte[] source =
        [
            0, 0, 0, 255,
            100, 100, 100, 255,
            201, 202, 203, 204,
            50, 50, 50, 255,
            150, 150, 150, 255,
            211, 212, 213, 214
        ];
        var original = source.ToArray();

        var result = BackdropBlur.BlurBgra32(
            source,
            width: 2,
            height: 2,
            stride: 12,
            radius: 1,
            passes: 3);

        Assert.Equal(original, source);
        Assert.Equal(original[8..12], result[8..12]);
        Assert.Equal(original[20..24], result[20..24]);
    }

    [Fact]
    public void BlurBgra32_ConstantSinglePixelSurvivesVeryLargeRadiusAndPassCount()
    {
        byte[] source = [10, 20, 30, 40];

        var result = BackdropBlur.BlurBgra32(
            source,
            width: 1,
            height: 1,
            stride: 4,
            radius: int.MaxValue,
            passes: 4);

        Assert.Equal(source, result);
    }

    [Theory]
    [InlineData(0, 1, 4, 0, 1)]
    [InlineData(1, 0, 4, 0, 1)]
    [InlineData(2, 1, 4, 0, 1)]
    [InlineData(1, 1, 4, -1, 1)]
    [InlineData(1, 1, 4, 0, 0)]
    public void BlurBgra32_RejectsInvalidGeometryOrOptions(
        int width,
        int height,
        int stride,
        int radius,
        int passes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackdropBlur.BlurBgra32(
                new byte[16],
                width,
                height,
                stride,
                radius,
                passes));
    }

    [Fact]
    public void BlurBgra32_RejectsShortSourceBuffer()
    {
        Assert.Throws<ArgumentException>(
            () => BackdropBlur.BlurBgra32(
                new byte[7],
                width: 2,
                height: 1,
                stride: 8,
                radius: 1));
    }

    private static byte[] CreateGrayPixels(params byte[] values)
    {
        var result = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
        {
            var offset = index * 4;
            result[offset] = values[index];
            result[offset + 1] = values[index];
            result[offset + 2] = values[index];
            result[offset + 3] = 255;
        }

        return result;
    }

    private static byte[] ReadBlueChannel(
        byte[] pixels,
        int width,
        int height,
        int stride) =>
        ReadChannel(pixels, width, height, stride, channel: 0);

    private static byte[] ReadAlphaChannel(
        byte[] pixels,
        int width,
        int height,
        int stride) =>
        ReadChannel(pixels, width, height, stride, channel: 3);

    private static byte[] ReadChannel(
        byte[] pixels,
        int width,
        int height,
        int stride,
        int channel)
    {
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result[(y * width) + x] =
                    pixels[(y * stride) + (x * 4) + channel];
            }
        }

        return result;
    }
}
