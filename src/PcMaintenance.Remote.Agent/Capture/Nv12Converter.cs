namespace PcMaintenance.Remote.Agent.Capture;

/// <summary>
/// 把 BGRA（Desktop Duplication 的輸出）轉成 H.264 編碼器要的 NV12。
/// 用 BT.601 full-range 係數的整數近似，和 P0 驗證程式一致（PSNR 40～50 dB）。
/// 這裡是 CPU 版、純函式、可測；GPU（D3D11 VideoProcessor）版是 P1 之後的最佳化。
/// </summary>
public static class Nv12Converter
{
    /// <summary>NV12 緩衝區需要的位元組數：Y 平面 w*h + 交錯 UV 平面 w*h/2。</summary>
    public static int RequiredSize(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// 轉換一張畫面。寬高須為偶數。bgra 的每列位元組數由 srcStride 指定（Desktop Duplication
    /// 的列距常大於 width*4）；dst 必須至少 <see cref="RequiredSize"/> 大。
    /// </summary>
    public static void Convert(ReadOnlySpan<byte> bgra, int srcStride, int width, int height, Span<byte> dst)
    {
        if ((width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("寬與高必須是偶數");
        if (srcStride < width * 4)
            throw new ArgumentException("srcStride 小於一列的位元組數", nameof(srcStride));
        if (dst.Length < RequiredSize(width, height))
            throw new ArgumentException("dst 太小", nameof(dst));

        int ySize = width * height;
        Span<byte> y = dst.Slice(0, ySize);
        Span<byte> uv = dst.Slice(ySize, ySize / 2);

        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> src = bgra.Slice(row * srcStride, width * 4);
            Span<byte> yRow = y.Slice(row * width, width);
            for (int x = 0; x < width; x++)
            {
                int b = src[x * 4], g = src[x * 4 + 1], r = src[x * 4 + 2];
                yRow[x] = (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
            }
        }

        // 色度在每個 2x2 區塊取左上角像素取樣。
        for (int row = 0; row < height; row += 2)
        {
            ReadOnlySpan<byte> src = bgra.Slice(row * srcStride, width * 4);
            Span<byte> uvRow = uv.Slice(row / 2 * width, width);
            for (int x = 0; x < width; x += 2)
            {
                int b = src[x * 4], g = src[x * 4 + 1], r = src[x * 4 + 2];
                uvRow[x] = (byte)(((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128);     // U/Cb
                uvRow[x + 1] = (byte)(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128); // V/Cr
            }
        }
    }
}
