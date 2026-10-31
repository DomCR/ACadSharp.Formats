using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace ACadSharp.Formats.Image;

/// <summary>
/// P6：原分辨率行流 PNG 合并写器（不缩小、不丢清晰度）。
///
/// 场景：整幅画布（fullW×fullH）超出 RasterCanvas 单图像素上限（16384/边、32M 像素），
/// 宿主按 WritePngTiled 同一网格渲染（行优先：cy 外层 / cx 内层），把各瓦片画布依序喂给本写器，
/// 最终输出一张 fullW×fullH 的原分辨率 PNG。
///
/// 机制（PNG 扫描线格式，行流输出）：
///   IHDR(fullW, fullH, 8bit, RGBA)
///   → 每个整幅输出行：filter 字节 0(None) + fullW×4 RGBA 字节
///       （整行拼装：本网格行各瓦片行按列偏移拷入 _rowBuf，行满即写入 zlib 流）
///   → 单条长生命周期 ZLibStream（RFC1950：header + deflate + adler32，Fastest）流式压缩；
///     压缩缓冲攒到 IdatChunkBytes 落一个 IDAT 块（PNG 允许多 IDAT 块，deflate 流跨块连续）
///   → 行尽后 Dispose zlib 流收尾，补尾 IDAT + IEND。
///
/// 关键语义（行拼装必须等整网格行到齐）：
///   宿主按行优先整瓦片喂入（同一网格行内先 (0,0) 整块、再 (1,0) 整块……最后 (cols-1,0) 整块），
///   一条整幅输出行 y 只有在该网格行最后一列瓦片送达时才能拼齐（其余各列的第 y 行必须保留到此刻）。
///   因此写器把当前网格行已到的各瓦片画布暂存于 _pending（仅持引用、不拷贝），
///   末列瓦片到达时逐行拼装并流式写出，随后清空 _pending 进入下一网格行。
///   （若只留 _rowBuf 逐列覆盖，先到的瓦片行会被后到瓦片覆盖旧行 → 行错位，已作为缺陷修复。）
///
/// 内存 = 当前网格行画布合计 fullW×tileH×4（引擎侧瓦片画布，写器持引用使其存活到行满）
///   + _rowBuf(fullW×4) + _idatBuf(≤1MB) + zlib 内部状态。
///   网格行体积由引擎的瓦片上限约束（见 FullImageEngine.Png 的 WritePngNativeMerge 行内存上限），
///   与整幅总高度无关 → 整幅再高也只多“网格行数”，不多“网格行体积”。
///
/// AOT 友好：无反射 / 无 Activator / 无 dynamic；仅 System.IO.Compression.ZLibStream（.NET 6+，零新增依赖）。
/// </summary>
internal sealed class PngNativeMergeWriter : IDisposable
{
    private const int IdatChunkBytes = 1 << 20; // ~1MB/IDAT 块（deflate 流跨块连续）

    private readonly FileStream _fs;
    private readonly MemoryStream _idatBuf; // zlib 压缩输出累积（≤ IdatChunkBytes + zlib 内部余量）
    private readonly ZLibStream _zlib;      // 长生命周期，RFC1950，Fastest（大图像优先速度）
    private readonly byte[] _rowBuf;        // 当前整行像素（fullW×4，RGBA8）
    private readonly int _fullW;
    private readonly System.Collections.Generic.List<PendingTile> _pending = new(); // 当前网格行已到的瓦片
    private int _colX;                      // 下一瓦片列的 x 偏移（像素）
    private int _rowsWritten;
    private bool _failed;

    /// <summary>当前网格行内一个已到达的瓦片（持画布引用直到整网格行拼完）。</summary>
    private readonly struct PendingTile
    {
        public readonly int ColStart; // 瓦片在整幅中的列偏移（像素）
        public readonly int W;        // 瓦片宽（像素）
        public readonly RasterCanvas Rc;
        public PendingTile(int colStart, int w, RasterCanvas rc)
        { ColStart = colStart; W = w; Rc = rc; }
    }

    /// <summary>已写入的整幅行数（正常结束时 == 整幅高）。</summary>
    public int RowsWritten => _rowsWritten;

    /// <summary>是否已发生不可恢复错误（宿主应在每瓦片后检查并停止）。</summary>
    public bool Failed => _failed;

    public PngNativeMergeWriter(string path, int fullW, int fullH)
    {
        _fullW = fullW;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        _idatBuf = new MemoryStream();
        _zlib = new ZLibStream(_idatBuf, CompressionLevel.Fastest, leaveOpen: true);
        _rowBuf = new byte[fullW * 4];

        _fs.Write(RasterCanvas._pngSig, 0, RasterCanvas._pngSig.Length);

        // IHDR：宽、高、位深 8、色彩类型 6(RGBA)、压缩 0、滤波 0、不隔行
        byte[] ihdr = new byte[13];
        RasterCanvas.WriteU32(ihdr, 0, (uint)fullW);
        RasterCanvas.WriteU32(ihdr, 4, (uint)fullH);
        ihdr[8] = 8;
        ihdr[9] = 6;
        RasterCanvas.WriteChunk(_fs, "IHDR", ihdr);
    }

    /// <summary>喂入一个瓦片画布（行优先：同一网格行内先 (0,0) 整块、再 (1,0) 整块……）。
    /// 瓦片占据整幅的固定列区间 [_colX, _colX+w)：画布暂存进 _pending（列偏移固定，不得逐行累加）。
    /// 当本瓦片是本网格行的最后一列（_colX+w == fullW）时，整网格行到齐 →
    /// 逐行拼装（各瓦片第 y 行按列偏移拷入 _rowBuf）写入 zlib 流（filter 0 = None），然后清空暂存。
    /// 前提：宿主按行优先喂瓦片（同一网格行内瓦片列高相同、列从左到右无缺口、末列贴 fullW 右边界）。</summary>
    public void WriteTile(RasterCanvas rc)
    {
        if (_failed) return;
        int w = rc.Width;
        int h = rc.Height;
        int colStart = _colX;
        _pending.Add(new PendingTile(colStart, w, rc));
        _colX += w;
        if (_colX != _fullW)
            return; // 网格行未到齐：保留本瓦片，等末列

        // 末列到达，整网格行到齐：逐行拼装 + 流式写出（瞬态内存仅 _rowBuf 一行）
        foreach (var p in _pending)
            if (p.Rc.Height != h) { _failed = true; return; } // 同网格行各瓦片列高必须一致
        for (int y = 0; y < h; y++)
        {
            foreach (var p in _pending)
                p.Rc.GetRow(y).CopyTo(_rowBuf.AsSpan(p.ColStart * 4, p.W * 4));
            try
            {
                _zlib.WriteByte(0); // PNG 扫描线 filter：0 = None
                _zlib.Write(_rowBuf, 0, _rowBuf.Length);
            }
            catch
            {
                _failed = true;
                return;
            }
            _rowsWritten++;
            if (_idatBuf.Length >= IdatChunkBytes)
                EmitIdatChunk();
        }
        _pending.Clear(); // 本网格行画布至此可被 GC
        _colX = 0;        // 下一网格行回到首列
    }

    private void EmitIdatChunk()
    {
        try
        {
            if (_idatBuf.Length == 0) return;
            RasterCanvas.WriteChunk(_fs, "IDAT", _idatBuf.ToArray());
            _idatBuf.SetLength(0);
        }
        catch
        {
            _failed = true;
        }
    }

    /// <summary>收尾：校验网格行完整 → Dispose zlib 流（补 RFC1950 尾）→ 尾 IDAT + IEND。</summary>
    /// <returns>true = 文件完整写出；false = 失败（调用方应删半成品并按失败处理）。</returns>
    public bool Finish()
    {
        if (_failed) return false;
        if (_pending.Count != 0)
        {
            _failed = true; // 宿主未按网格行边界喂满瓦片（末列未贴 right 边界）→ 行数必不齐
            return false;
        }
        try
        {
            _zlib.Dispose(); // 完成 deflate 流 + adler32
            EmitIdatChunk();
            if (_failed) return false;
            RasterCanvas.WriteChunk(_fs, "IEND", Array.Empty<byte>());
            _fs.Flush();
        }
        catch
        {
            _failed = true;
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        try { _zlib.Dispose(); } catch { /* 收尾异常已在 Finish 中处理 */ }
        try { _fs.Dispose(); } catch { /* 同上 */ }
    }
}
