namespace PcMaintenance.Remote.Viewer.Decoding;

/// <summary>
/// NV12 → BGRA（32bpp，WPF Bgra32 可直接用）。輸入可能帶列距（stride ≥ 寬）且編碼側會把高度補到 16 的倍數，
/// 所以只取左上 displayW×displayH（H.264 影像原點在左上、補邊在右下，裁左上就是原內容）。
/// 用 BT.601 有限範圍（Y 16..235）的反轉換，對上編碼側的 Nv12Converter。
/// </summary>
public static class Nv12ToBgra
{
    public static byte[] Convert(ReadOnlySpan<byte> nv12, int codedWidth, int codedHeight, int strideY,
        int displayWidth, int displayHeight)
    {
        int w = Math.Min(displayWidth, codedWidth);
        int h = Math.Min(displayHeight, codedHeight);
        byte[] bgra = new byte[w * h * 4];

        int yPlane = 0;
        int uvPlane = strideY * codedHeight; // UV 交錯平面緊接在 Y 平面後，列距同 strideY

        for (int y = 0; y < h; y++)
        {
            int yRow = yPlane + y * strideY;
            int uvRow = uvPlane + (y >> 1) * strideY;
            int dst = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int yy = nv12[yRow + x] - 16;
                int uvCol = (x >> 1) * 2;
                int u = nv12[uvRow + uvCol] - 128;
                int v = nv12[uvRow + uvCol + 1] - 128;

                int c = 298 * yy;
                int r = (c + 409 * v + 128) >> 8;
                int g = (c - 100 * u - 208 * v + 128) >> 8;
                int b = (c + 516 * u + 128) >> 8;

                bgra[dst + 0] = Clamp(b);
                bgra[dst + 1] = Clamp(g);
                bgra[dst + 2] = Clamp(r);
                bgra[dst + 3] = 255;
                dst += 4;
            }
        }
        return bgra;
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
