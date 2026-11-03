using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ACadSharp.Formats.Image;

/// <summary>
/// 纯托管（零外部依赖）的光栅图辅助工具：
/// <list type="bullet">
///   <item>JPEG 属性探测（尺寸/通道数，用于 PDF /DCTDecode 直嵌，无需解码）</item>
///   <item>PNG 解码（8-bit RGB/RGBA/Gray/GrayA/Palette → RGB(A) 字节，用于 PDF /FlateDecode+Predictor15）</item>
/// </list>
/// 所有实现均基于 PNG / JPEG 开放规范独立编写，不依赖 PDFsharp / SkiaSharp / System.Drawing，
/// 符合 Formats 零系统级依赖约定。
/// 外部 PDF 底图引用（PdfUnderlay）的矢量嵌入涉及完整 PDF 对象图克隆，按项目约定不引入第三方
/// PDF 库，渲染端降级为「引用框 + 信息」（见 EmitPdfUnderlayPdf），故此处无需 PDF 解析能力。
/// </summary>
internal static class RasterHelpers
{
    #region ===== JPEG 探测（不解码，直嵌 DCTDecode）=====
    public sealed class JpegInfo
    {
        public int Width;
        public int Height;
        public int Components; // 1=Gray 3=RGB 4=CMYK
    }

    /// <summary>探测 JPEG 文件尺寸与通道数。失败返回 null。</summary>
    public static JpegInfo ProbeJpeg(byte[] data)
    {
        if (data == null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF) { i++; continue; }
            byte m = data[i + 1];
            // SOF0..SOF15 中排除 DHT(0xC4)/SOF8(0xC8)/SOF12(0xCC)
            if (m is >= 0xC0 and <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
            {
                int h = (data[i + 5] << 8) | data[i + 6];
                int w = (data[i + 7] << 8) | data[i + 8];
                int comp = data[i + 9];
                return new JpegInfo { Width = w, Height = h, Components = comp };
            }
            int len = (data[i + 2] << 8) | data[i + 3];
            i += 2 + len;
        }
        return null;
    }
    #endregion

    #region ===== BMP 解码（OLE2FRAME 内嵌 DIB 预览）=====

    /// <summary>
    /// 解码 24/32 位 BI_RGB BMP（BITMAPFILEHEADER + 40 字节 BITMAPINFOHEADER）为**自上而下**的 RGBA 字节。
    /// 用于 OLE2FRAME（组码310 内嵌 OLE CFB 容器中的 DIB 预览图）：BMP 行序为底向上（biHeight&gt;0）
    /// 或顶向下（biHeight&lt;0），统一翻转/直通为顶向下，与 PNG/JPEG 解码输出及 DrawImageParallelogram
    /// （图像 (0,0)=左上）的约定一致。不支持压缩/调色板/16 位（OLE DIB 均为 BI_RGB 24/32）。
    /// 失败抛 InvalidDataException（调用方 catch 后降级为边框）。
    /// </summary>
    public static (int width, int height, byte[] rgba) DecodeBmpToRgba(byte[] bmp)
    {
        if (bmp == null || bmp.Length < 54) throw new InvalidDataException("BMP 过短（<54 字节）");
        if (bmp[0] != 0x42 || bmp[1] != 0x4D) throw new InvalidDataException("非 BMP（BM 魔数缺失）");
        int dataOff = BitConverter.ToInt32(bmp, 10);
        int biSize = BitConverter.ToInt32(bmp, 14);
        if (biSize < 40) throw new InvalidDataException("不支持的 BITMAPINFOHEADER（biSize=" + biSize + "）");
        int w = BitConverter.ToInt32(bmp, 18);
        int h = BitConverter.ToInt32(bmp, 22);
        short planes = BitConverter.ToInt16(bmp, 26);
        short bpp = BitConverter.ToInt16(bmp, 28);
        int compression = BitConverter.ToInt32(bmp, 30);
        if (planes != 1) throw new InvalidDataException("不支持的 BMP（biPlanes=" + planes + "）");
        if (bpp != 24 && bpp != 32) throw new InvalidDataException("不支持的 BMP 位深（" + bpp + "）");
        if (compression != 0) throw new InvalidDataException("不支持的 BMP 压缩（" + compression + "）");
        if (w <= 0 || w > 16384 || h == 0 || Math.Abs(h) > 16384) throw new InvalidDataException("BMP 尺寸非法（" + w + "x" + h + "）");
        bool topDown = h < 0;
        int H = Math.Abs(h);
        int stride = (w * (bpp / 8) + 3) & ~3; // 行 4 字节对齐
        if (dataOff < 54 || (long)dataOff + (long)stride * H > bmp.Length) throw new InvalidDataException("BMP 像素数据截断");
        var rgba = new byte[w * H * 4];
        for (int y = 0; y < H; y++)
        {
            int srcRow = topDown ? y : (H - 1 - y);
            int srow = dataOff + srcRow * stride;
            int drow = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int si = srow + x * (bpp / 8);
                int di = drow + x * 4;
                rgba[di] = bmp[si + 2];      // R
                rgba[di + 1] = bmp[si + 1];  // G
                rgba[di + 2] = bmp[si];      // B
                rgba[di + 3] = 255;          // A（DIB 无透明度）
            }
        }
        return (w, H, rgba);
    }

    #endregion

    #region ===== PNG 解码（8-bit）=====
    /// <summary>解码 PNG 为像素字节。返回 channels: 3(RGB)/4(RGBA)。失败抛异常。</summary>
    public static (int width, int height, int channels, byte[] pixels) DecodePngToRgb(byte[] png)
    {
        if (png == null || png.Length < 8 || !(png[0] == 0x89 && png[1] == 0x50)) throw new Exception("非 PNG");
        int w = 0, h = 0, bitDepth = 0, colorType = 0;
        byte[] palette = null; byte[] trans = null;
        var idat = new List<byte>();
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            string type = Encoding.ASCII.GetString(png, p + 4, 4);
            byte[] chunk = new byte[len];
            Array.Copy(png, p + 8, chunk, 0, len);
            if (type == "IHDR")
            {
                w = (chunk[0] << 24) | (chunk[1] << 16) | (chunk[2] << 8) | chunk[3];
                h = (chunk[4] << 24) | (chunk[5] << 16) | (chunk[6] << 8) | chunk[7];
                bitDepth = chunk[8]; colorType = chunk[9];
            }
            else if (type == "PLTE") palette = chunk;
            else if (type == "tRNS") trans = chunk;
            else if (type == "IDAT") idat.AddRange(chunk);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        if (w == 0 || h == 0) throw new Exception("PNG 无 IHDR");
        if (bitDepth != 8) throw new Exception("仅支持 8-bit PNG");

        // 解压 IDAT（zlib）
        var raw = InflateZlib(idat);
        int channels = colorType switch
        {
            0 => 1,  // Gray
            2 => 3,  // RGB
            3 => 3,  // Palette → 转 RGB
            4 => 2,  // Gray+Alpha
            6 => 4,  // RGBA
            _ => throw new Exception("不支持的颜色类型 " + colorType)
        };
        int stride = w * channels;
        byte[] outp = new byte[h * stride];
        // 逐行还原 filter
        int pos = 0;
        byte[] prev = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            byte ft = raw[pos++];
            byte[] cur = new byte[stride];
            for (int x = 0; x < stride; x++)
            {
                byte rawb = raw[pos++];
                byte a = x >= channels ? cur[x - channels] : (byte)0;
                byte b = prev[x];
                byte c = x >= channels ? prev[x - channels] : (byte)0;
                byte v = ft switch
                {
                    0 => rawb,
                    1 => (byte)(rawb + a),
                    2 => (byte)(rawb + b),
                    3 => (byte)(rawb + ((a + b) / 2)),
                    4 => (byte)(rawb + Paeth(a, b, c)),
                    _ => rawb
                };
                cur[x] = v;
            }
            Array.Copy(cur, 0, outp, y * stride, stride);
            prev = cur;
        }

        // 转 RGB(A)
        if (colorType == 2) return (w, h, 3, outp);
        if (colorType == 6) return (w, h, 4, outp);
        if (colorType == 4) // Gray+Alpha → RGBA
        {
            byte[] rgba = new byte[w * h * 4];
            for (int k = 0, j = 0; k < outp.Length; k += 2, j += 4)
            { rgba[j] = rgba[j + 1] = rgba[j + 2] = outp[k]; rgba[j + 3] = outp[k + 1]; }
            return (w, h, 4, rgba);
        }
        if (colorType == 0) // Gray → RGBA (alpha=255)
        {
            byte[] rgba = new byte[w * h * 4];
            for (int k = 0, j = 0; k < outp.Length; k++, j += 4)
            { rgba[j] = rgba[j + 1] = rgba[j + 2] = outp[k]; rgba[j + 3] = 255; }
            return (w, h, 4, rgba);
        }
        if (colorType == 3) // Palette → RGB(A)
        {
            byte[] rgba = new byte[w * h * 4];
            bool hasTrans = trans != null && trans.Length > 0;
            for (int k = 0, j = 0; k < outp.Length; k++, j += 4)
            {
                int idx = outp[k];
                rgba[j] = palette[idx * 3];
                rgba[j + 1] = palette[idx * 3 + 1];
                rgba[j + 2] = palette[idx * 3 + 2];
                rgba[j + 3] = (hasTrans && idx < trans.Length) ? trans[idx] : (byte)255;
            }
            return (w, h, 4, rgba);
        }
        throw new Exception("unreachable");
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int pp = a + b - c;
        int pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static byte[] InflateZlib(List<byte> idatChunks)
    {
        if (idatChunks == null || idatChunks.Count < 2) return Array.Empty<byte>();
        var all = idatChunks.ToArray();
        // 跳过 zlib 2 字节头
        using var ms = new MemoryStream(all, 2, all.Length - 2);
        using var def = new DeflateStream(ms, CompressionMode.Decompress);
        using var outms = new MemoryStream();
        def.CopyTo(outms);
        return outms.ToArray();
    }
    #endregion

    #region ===== JPEG 解码（baseline / 扩展序列 Huffman → RGBA，零外部依赖）=====

    /// <summary>统一解码入口：自动识别 JPEG / PNG，统一输出 RGBA（长度 w*h*4，行优先，原点左上）。
    /// 供 PNG 引擎把光栅图像（RasterImage）绘制到软件光栅化缓冲。失败抛异常。</summary>
    public static (int width, int height, byte[] rgba) DecodeToRgba(byte[] img)
    {
        if (img != null && img.Length > 3 && img[0] == 0xFF && img[1] == 0xD8)
            return DecodeJpegToRgba(img);
        var (w, h, ch, pix) = DecodePngToRgb(img);
        byte[] rgba = new byte[w * h * 4];
        if (ch == 4) Array.Copy(pix, rgba, pix.Length);
        else for (int i = 0, j = 0; i < pix.Length; i += ch, j += 4)
        { rgba[j] = pix[i]; rgba[j + 1] = pix[i + 1]; rgba[j + 2] = pix[i + 2]; rgba[j + 3] = 255; }
        return (w, h, rgba);
    }

    /// <summary>解码 baseline / 扩展序列（Huffman）JPEG 为 RGBA。不支持算术编码 / 渐进式（将抛异常，由调用方降级）。</summary>
    public static (int width, int height, byte[] rgba) DecodeJpegToRgba(byte[] jpg)
    {
        if (jpg == null || jpg.Length < 2 || jpg[0] != 0xFF || jpg[1] != 0xD8) throw new Exception("非 JPEG");
        int p = 2;
        int width = 0, height = 0, nComp = 0;
        var compId = new int[4];
        var compH = new int[4];
        var compV = new int[4];
        var compQ = new int[4];
        var quant = new short[4][];
        var huffDC = new HuffTable[4];
        var huffAC = new HuffTable[4];
        var scanComp = new int[4];
        var scanDc = new int[4];
        var scanAc = new int[4];
        int scanN = 0, restartInterval = 0;
        bool sof = false;

        while (p + 1 < jpg.Length)
        {
            if (jpg[p] != 0xFF) { p++; continue; }
            int marker = jpg[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker == 0xD8) { p += 2; continue; }            // SOI
            if (marker == 0xD9) break;                           // EOI
            if (marker >= 0xD0 && marker <= 0xD7) { p += 2; continue; } // RSTn
            if (p + 4 >= jpg.Length) break;
            int len = (jpg[p + 2] << 8) | jpg[p + 3];
            int segStart = p + 4;
            int segEnd = p + 2 + len;

            if (marker == 0xDB) // DQT
            {
                int q = segStart;
                while (q + 1 < segEnd)
                {
                    int pqtq = jpg[q++];
                    int prec = pqtq >> 4, tq = pqtq & 0x0F;
                    var table = new short[64];
                    for (int i = 0; i < 64; i++)
                    {
                        int val = prec == 0 ? jpg[q++] : (jpg[q++] << 8) | jpg[q++];
                        table[ZigZag[i]] = (short)val; // 还原为自然顺序
                    }
                    quant[tq] = table;
                }
            }
            else if (marker == 0xC0 || (marker >= 0xC1 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)) // SOFn
            {
                height = (jpg[segStart + 1] << 8) | jpg[segStart + 2];
                width = (jpg[segStart + 3] << 8) | jpg[segStart + 4];
                nComp = jpg[segStart + 5];
                int q = segStart + 6;
                for (int i = 0; i < nComp && i < 4; i++)
                {
                    compId[i] = jpg[q++];
                    int hv = jpg[q++];
                    compH[i] = hv >> 4; compV[i] = hv & 0x0F;
                    compQ[i] = jpg[q++];
                }
                sof = true;
            }
            else if (marker == 0xC4) // DHT
            {
                int q = segStart;
                while (q + 1 < segEnd)
                {
                    int tcTh = jpg[q++];
                    int tc = tcTh >> 4, th = tcTh & 0x0F;
                    int[] counts = new int[16];
                    int total = 0;
                    for (int i = 0; i < 16; i++) { counts[i] = jpg[q++]; total += counts[i]; }
                    byte[] values = new byte[total];
                    for (int i = 0; i < total; i++) values[i] = jpg[q++];
                    var tbl = new HuffTable();
                    tbl.Build(counts, values);
                    if (tc == 0) huffDC[th] = tbl; else huffAC[th] = tbl;
                }
            }
            else if (marker == 0xDD) // DRI
            {
                restartInterval = (jpg[segStart] << 8) | jpg[segStart + 1];
            }
            else if (marker == 0xDA) // SOS
            {
                scanN = jpg[segStart];
                int q = segStart + 1;
                for (int i = 0; i < scanN && i < 4; i++)
                {
                    int cid = jpg[q++];
                    int tdta = jpg[q++];
                    scanDc[i] = tdta >> 4; scanAc[i] = tdta & 0x0F;
                    for (int k = 0; k < nComp; k++) if (compId[k] == cid) { scanComp[i] = k; break; }
                }
                if (!sof) throw new Exception("JPEG 缺少 SOF");
                return DecodeScan(jpg, segEnd, width, height, nComp, compId, compH, compV, compQ,
                    quant, huffDC, huffAC, scanComp, scanDc, scanAc, scanN, restartInterval);
            }
            p = segEnd;
        }
        throw new Exception("JPEG 未找到 SOS");
    }

    private static (int, int, byte[]) DecodeScan(byte[] jpg, int dataStart,
        int width, int height, int nComp, int[] compId, int[] compH, int[] compV, int[] compQ,
        short[][] quant, HuffTable[] huffDC, HuffTable[] huffAC, int[] scanComp, int[] scanDc, int[] scanAc, int scanN, int restartInterval)
    {
        int maxH = 1, maxV = 1;
        for (int i = 0; i < nComp; i++) { if (compH[i] > maxH) maxH = compH[i]; if (compV[i] > maxV) maxV = compV[i]; }
        int mcuW = maxH * 8, mcuH = maxV * 8;
        int mcusX = (width + mcuW - 1) / mcuW;
        int mcusY = (height + mcuH - 1) / mcuH;
        var compWs = new int[nComp];
        var compHs = new int[nComp];
        for (int i = 0; i < nComp; i++) { compWs[i] = mcusX * compH[i] * 8; compHs[i] = mcusY * compV[i] * 8; }
        var planes = new byte[nComp][];
        for (int i = 0; i < nComp; i++) planes[i] = new byte[compWs[i] * compHs[i]];
        var pred = new int[4];
        var br = new BitReader(jpg, dataStart);
        int mcuCount = 0;

        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                for (int s = 0; s < scanN; s++)
                {
                    int ci = scanComp[s];
                    var dc = huffDC[scanDc[s]];
                    var ac = huffAC[scanAc[s]];
                    int h = compH[ci], v = compV[ci];
                    for (int by = 0; by < v; by++)
                    {
                        for (int bx = 0; bx < h; bx++)
                        {
                            var blk = new float[64];
                            int t = dc.DecodeHuff(br);
                            int diff = t == 0 ? 0 : br.ReceiveExtend(t);
                            pred[ci] += diff;
                            blk[0] = pred[ci] * quant[compQ[ci]][0];
                            int k = 1;
                            while (k < 64)
                            {
                                int rs = ac.DecodeHuff(br);
                                int r = rs >> 4, sval = rs & 0x0F;
                                if (sval == 0)
                                {
                                    if (r == 15) { k += 16; continue; }
                                    break; // EOB
                                }
                                k += r;
                                if (k >= 64) break;
                                blk[k] = br.ReceiveExtend(sval) * quant[compQ[ci]][k];
                                k++;
                            }
                            var outp = IDCT(blk);
                            int px = mx * h * 8 + bx * 8;
                            int py = my * v * 8 + by * 8;
                            for (int yy = 0; yy < 8; yy++)
                            {
                                int planeY = py + yy;
                                if (planeY >= compHs[ci]) continue;
                                for (int xx = 0; xx < 8; xx++)
                                {
                                    int planeX = px + xx;
                                    if (planeX >= compWs[ci]) continue;
                                    int val = (int)Math.Round(outp[yy * 8 + xx]) + 128;
                                    if (val < 0) val = 0; else if (val > 255) val = 255;
                                    planes[ci][planeY * compWs[ci] + planeX] = (byte)val;
                                }
                            }
                        }
                    }
                }
                mcuCount++;
                if (restartInterval > 0 && mcuCount % restartInterval == 0)
                {
                    br.AlignByte();
                    br.SkipMarker();
                    for (int i = 0; i < nComp; i++) pred[i] = 0;
                }
            }
        }

        var rgba = new byte[width * height * 4];
        if (nComp == 1)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int sx = (int)((x * (double)compWs[0]) / width);
                    int sy = (int)((y * (double)compHs[0]) / height);
                    byte g = planes[0][sy * compWs[0] + sx];
                    int o = (y * width + x) * 4;
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = g; rgba[o + 3] = 255;
                }
        }
        else // YCbCr → RGB（JFIF 顺序 Y,Cb,Cr）
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int sx0 = (int)((x * (double)compWs[0]) / width), sy0 = (int)((y * (double)compHs[0]) / height);
                    int sx1 = (int)((x * (double)compWs[1]) / width), sy1 = (int)((y * (double)compHs[1]) / height);
                    int sx2 = (int)((x * (double)compWs[2]) / width), sy2 = (int)((y * (double)compHs[2]) / height);
                    double Y = planes[0][sy0 * compWs[0] + sx0];
                    double Cb = planes[1][sy1 * compWs[1] + sx1];
                    double Cr = planes[2][sy2 * compWs[2] + sx2];
                    int r = (int)(Y + 1.402 * (Cr - 128) + 0.5);
                    int g = (int)(Y - 0.344136 * (Cb - 128) - 0.714136 * (Cr - 128) + 0.5);
                    int b = (int)(Y + 1.772 * (Cb - 128) + 0.5);
                    if (r < 0) r = 0; else if (r > 255) r = 255;
                    if (g < 0) g = 0; else if (g > 255) g = 255;
                    if (b < 0) b = 0; else if (b > 255) b = 255;
                    int o = (y * width + x) * 4;
                    rgba[o] = (byte)r; rgba[o + 1] = (byte)g; rgba[o + 2] = (byte)b; rgba[o + 3] = 255;
                }
        }
        return (width, height, rgba);
    }

    private static readonly int[] ZigZag = {
        0,1,8,16,9,2,3,10, 17,24,32,25,18,11,4,5,
        12,19,26,33,40,48,41,34, 27,20,13,6,7,14,21,28,
        35,42,49,56,57,50,43,36, 29,22,15,23,30,37,44,51,
        58,59,52,45,38,31,39,46, 53,60,61,54,47,55,62,63 };

    private static float[] IDCT(float[] c)
    {
        var tmp = new float[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                for (int u = 0; u < 8; u++)
                {
                    double cu = u == 0 ? 0.70710678118654752 : 1.0;
                    sum += cu * c[y * 8 + u] * Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                }
                tmp[y * 8 + x] = (float)(sum * 0.5);
            }
        var res = new float[64];
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                double sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    double cv = v == 0 ? 0.70710678118654752 : 1.0;
                    sum += cv * tmp[v * 8 + x] * Math.Cos((2 * y + 1) * v * Math.PI / 16.0);
                }
                res[y * 8 + x] = (float)(sum * 0.5);
            }
        return res;
    }

    private sealed class HuffTable
    {
        private readonly int[] _mincode = new int[16];
        private readonly int[] _maxcode = new int[16];
        private readonly int[] _valptr = new int[16];
        private byte[] _huffval;
        public void Build(int[] bits, byte[] vals)
        {
            _huffval = vals;
            int code = 0, k = 0;
            for (int l = 0; l < 16; l++)
            {
                if (bits[l] == 0) { _maxcode[l] = -1; }
                else
                {
                    _valptr[l] = k;
                    _mincode[l] = code;
                    code += bits[l];
                    _maxcode[l] = code - 1;
                    k += bits[l];
                }
                code <<= 1;
            }
        }
        public int DecodeHuff(BitReader br)
        {
            int code = 0;
            for (int l = 0; l < 16; l++)
            {
                int bit = br.ReadBit();
                code = (code << 1) | bit;
                if (_maxcode[l] >= 0 && code <= _maxcode[l])
                    return _huffval[_valptr[l] + (code - _mincode[l])];
            }
            throw new Exception("Huffman 解码越界");
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bp;
        private int _cache;
        private int _cacheBits;
        public BitReader(byte[] d, int start) { _data = d; _bp = start; _cache = 0; _cacheBits = 0; }
        private int NextByte()
        {
            if (_bp >= _data.Length) return 0;
            int b = _data[_bp++];
            if (b == 0xFF)
            {
                int n = _bp < _data.Length ? _data[_bp] : 0;
                if (n == 0x00) _bp++;            // 填充 0：0xFF 是真实数据
                else if (n >= 0xD0 && n <= 0xD7) _bp++; // 重启标记（理论上已被对齐消费）
            }
            return b;
        }
        public int ReadBit()
        {
            if (_cacheBits == 0) { _cache = NextByte(); _cacheBits = 8; }
            _cacheBits--;
            return (_cache >> _cacheBits) & 1;
        }
        public int GetBits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v = (v << 1) | ReadBit();
            return v;
        }
        public int ReceiveExtend(int s)
        {
            if (s == 0) return 0;
            int v = GetBits(s);
            if (v < (1 << (s - 1))) v += (-1 << s) + 1;
            return v;
        }
        public void AlignByte() { _cacheBits = 0; _cache = 0; }
        public void SkipMarker()
        {
            if (_bp < _data.Length && _data[_bp] == 0xFF) _bp++;
            if (_bp < _data.Length) _bp++;
        }
    }
    #endregion
}
