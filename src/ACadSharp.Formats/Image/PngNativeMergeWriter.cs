using System;
using System.IO;
using System.IO.Compression;

namespace ACadSharp.Formats.Image;

/// <summary>
/// P6/P6.5：原分辨率行流 PNG 合并写器（不缩小、不丢清晰度）。
///
/// 场景：整幅画布（fullW×fullH）超出 RasterCanvas 单图像素上限（16384/边、32M 像素），
/// 宿主按 WritePngTiled 同一网格列宽渲染，并沿高度再切 bandH 高的子条带（行优先：
/// 每条带内先 (0,cy,ky) 整块、再 (1,cy,ky) 整块……最后 (cols-1,cy,ky) 整块），
/// 把各子条带画布依序喂给本写器，最终输出一张 fullW×fullH 的原分辨率 PNG。
///
/// 机制（PNG 扫描线格式，行流输出）：
///   IHDR(fullW, fullH, 8bit, RGBA)
///   → 每条带到齐：其每个输出行 filter 字节 0(None) + fullW×4 RGBA 字节写入 zlib 流
///   → 单条长生命周期 ZLibStream（RFC1950：header + deflate + adler32，Fastest）流式压缩；
///     压缩缓冲攒到 IdatChunkBytes 落一个 IDAT 块（PNG 允许多 IDAT 块，deflate 流跨块连续）
///   → 全部行尽后 Dispose zlib 流收尾，补尾 IDAT + IEND。
///
/// 关键语义（P6.5：子条带即时拷贝，不持画布引用）：
///   一条整幅输出行 y 只有在该条带最后一列子条带送达时才能拼齐（其余各列的第 y 行必须保留到此刻），
///   因此写器把每个子条带的各行【即时】拷入条带累积缓冲 _acc（fullW×bandH×4 ≤ 16MB 预算，首条带一次分配、
///   跨条带复用；末条带可更短，只用前缀），子条带画布引用随即释放（宿主可立即复用该画布）。
///   画布以瓦片左上角为原点渲染（与瓦片路径逐式一致 ⇒ 像素一致），子条带取画布行 [yOff, yOff+h)。
///   末列子条带到达时逐行从 _acc 写入 zlib 流，随后进入下一条带。
///   （P6 原实现暂存整网格行全部瓦片画布引用至行满，T11 实测 4×64MB=256MB/网格行常驻；
///     改为即时拷贝后写器侧仅 _acc 一条带体积（≤16MB 预算），且画布零垃圾。）
///
/// 内存 = _acc(fullW×bandH×4，首条带高度一次分配，与整幅总高度无关)
///   + _idatBuf(≤1MB) + zlib 内部状态。
///   条带高度 bandH 由宿主按预算选择（见 FullImageEngine.Png 的 WritePngNativeMerge），
///   整幅再高也只多“条带数”，不多“条带体积”。
///
/// AOT 友好：无反射 / 无 Activator / 无 dynamic；仅 System.IO.Compression.ZLibStream（.NET 6+，零新增依赖）。
/// </summary>
internal sealed class PngNativeMergeWriter : IDisposable
{
    private const int IdatChunkBytes = 1 << 20; // ~1MB/IDAT 块（deflate 流跨块连续）

    private readonly FileStream _fs;
    private readonly MemoryStream _idatBuf; // zlib 压缩输出累积（≤ IdatChunkBytes + zlib 内部余量）
    private readonly ZLibStream _zlib;      // 长生命周期，RFC1950，Fastest（大图像优先速度）
    private readonly int _fullW;
    private byte[] _acc;                    // 条带累积缓冲：fullW×bandH×4 RGBA8（首条带一次分配，跨条带复用）
    private int _bandH;                     // 当前条带高度（像素；条带首子条带到达时定标）
    private int _colX;                      // 下一条带的下一子条带列的 x 偏移（像素）
    private int _rowsWritten;
    private bool _failed;

    /// <summary>已写入的整幅行数（正常结束时 == 整幅高）。</summary>
    public int RowsWritten => _rowsWritten;

    /// <summary>是否已发生不可恢复错误（宿主应在每子条带后检查并停止）。</summary>
    public bool Failed => _failed;

    public PngNativeMergeWriter(string path, int fullW, int fullH)
    {
        _fullW = fullW;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        _idatBuf = new MemoryStream();
        _zlib = new ZLibStream(_idatBuf, CompressionLevel.Fastest, leaveOpen: true);

        _fs.Write(RasterCanvas._pngSig, 0, RasterCanvas._pngSig.Length);

        // IHDR：宽、高、位深 8、色彩类型 6(RGBA)、压缩 0、滤波 0、不隔行
        byte[] ihdr = new byte[13];
        RasterCanvas.WriteU32(ihdr, 0, (uint)fullW);
        RasterCanvas.WriteU32(ihdr, 4, (uint)fullH);
        ihdr[8] = 8;
        ihdr[9] = 6;
        RasterCanvas.WriteChunk(_fs, "IHDR", ihdr);
    }

    /// <summary>喂入一个子条带（行优先：同一条带内子条带列高相同、列从左到右无缺口、末列贴 fullW 右边界；
    /// 子条带占据整幅的固定列区间 [_colX, _colX+w)）。
    /// 画布渲染原点 = 瓦片左上角（与瓦片路径逐式一致 ⇒ 像素一致）；宿主显式给出子条带在画布中的
    /// 位置与尺寸：列 [0, w) × 行 [yOff, yOff+h)，即整幅条带行 [0, h)。
    /// （P6.5.1：w/h 由宿主显式传入——渲染画布现为单张固定 maxTw×maxTh 全程复用，
    ///   画布尺寸不再等于子条带尺寸，不能从 rc.Width/rc.Height 推断。）
    /// 即时拷贝：画布 [yOff, yOff+h) 各行按列偏移拷入 _acc（画布引用不保留，宿主可立即复用画布）。
    /// 当本子条带是本网格列行的最后一列（_colX+w == fullW）时，整条带到齐 →
    /// 逐行从 _acc 写入 zlib 流（filter 0 = None），随后进入下一条带。
    /// 前提：宿主按条带行优先喂子条带；同一条带各子条带 h 一致（末条带整体可更短，但带内必须一致）。</summary>
    public void WriteTile(RasterCanvas rc, int yOff, int w, int h)
    {
        if (_failed) return;
        if (yOff < 0 || h < 1 || w < 1) { _failed = true; return; } // 宿主误喂
        if (yOff + h > rc.Height || w > rc.Width) { _failed = true; return; } // 子条带超出画布
        int colStart = _colX;
        if (colStart + w > _fullW) { _failed = true; return; } // 宿主误喂：越过整幅右边界
        if (colStart == 0)
        {
            // 新条带首子条带：定标条带高度；累积缓冲首次按首条带高度分配（后续条带等高或更短）
            _bandH = h;
            long accBytes = (long)_fullW * h * 4;
            if (_acc == null)
            {
                if (accBytes > int.MaxValue) { _failed = true; return; } // 宿主预算失控
                _acc = new byte[accBytes];
            }
            else if (_acc.Length < accBytes) { _failed = true; return; } // 后续条带高于首条带 = 宿主错误
        }
        else if (h != _bandH) { _failed = true; return; } // 同一条带各子条带列高必须一致

        // 即时拷贝：画布第 yOff+y 行的列区间 [0, w) 像素 → _acc 第 y 行 [colStart, colStart+w) 像素（画布引用至此可释放）。
        // P6.5.1：画布 = 单张固定 maxTw×maxTh（宽 ≥ 子条带宽 w）；GetRow 返回整行 Span（宽 = 画布宽 × 4 字节），
        // 必须先 Slice(0, w*4) 再拷 —— 否则末列 w < 画布宽时 "Destination is too short"。
        // 注意单位：Slice 参数是字节，像素数 w 须 ×4；误写 Slice(0, w) 只拷 w/4 像素且 CopyTo 不抛错
        //（目标更长时仅拷源长、尾部保留旧值）→ 每瓦片列仅前 w/4 像素正确，其余为 0（P6.5.1 像素回归根因）。
        for (int y = 0; y < h; y++)
            rc.GetRow(yOff + y).Slice(0, w * 4).CopyTo(_acc.AsSpan((y * _fullW + colStart) * 4, w * 4));
        _colX += w;
        if (_colX != _fullW)
            return; // 条带未到齐：保留累积行，等末列

        // 末列到达，整条带到齐：逐行从累积缓冲写入 zlib 流（瞬态内存仅 _acc 本身）
        for (int y = 0; y < _bandH; y++)
        {
            try
            {
                _zlib.WriteByte(0); // PNG 扫描线 filter：0 = None
                _zlib.Write(_acc, y * _fullW * 4, _fullW * 4);
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
        _colX = 0;   // 下一条带回到首列
        _bandH = 0;
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

    /// <summary>收尾：校验条带完整 → Dispose zlib 流（补 RFC1950 尾）→ 尾 IDAT + IEND。</summary>
    /// <returns>true = 文件完整写出；false = 失败（调用方应删半成品并按失败处理）。</returns>
    public bool Finish()
    {
        if (_failed) return false;
        if (_colX != 0 || _bandH != 0)
        {
            _failed = true; // 宿主未按条带边界喂满（末列未贴 right 边界）→ 行数必不齐
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
