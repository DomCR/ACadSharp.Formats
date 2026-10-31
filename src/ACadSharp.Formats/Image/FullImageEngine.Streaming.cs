using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace ACadSharp.Formats.Image;

// =====================================================================
// P5 图像流式化（2026-11）：管线实体边流式转换边推入引擎，出图不再
// "写中间 DWG → 重读 DWG → 渲染"，直接对记录的实体列表光栅化/矢量化。
//
// 像素一致性（与重读渲染逐像素一致）由构造保证：
//   1) 实体集合 = 管线经 WriteEntityOrEnqueue 流出的同一批实体对象
//      （与 DWG 直连流同一对象；图框 insert 在流末 = 最顶层，等价于
//       重读 DWG 的 ModelSpace 文档序）；
//   2) $SORTENTS/SORTENTSTABLE 重排（ApplySortentsOrder）对记录列表
//      与完整模式同口径应用（见 AllEntities 的流式分支）；
//   3) 范围扩展走与完整模式完全相同的 per-entity switch（含 Insert 块
//      展开、文字盒、引线过冲、OLE2 两角），只是目标字段为 _st*；
//   4) 每次 Write*（PNG/SVG/PDF）内部对临时状态（画布/精灵缓存/字体表/
//      裁剪数据等）按调用重置 → 同一引擎实例可安全复用做多级别缩放与
//      多格式输出（宿主只需逐级改 opt.OutputZoom/WmtsZ/PngForceTiles）。
//
// ImageEngine 为 internal 且本程序集对宿主（Gdb2CadLib）无 InternalsVisibleTo，
// 故对外入口为下面的 public facade StreamingImageEngine。
// =====================================================================

internal partial class ImageEngine
{
    // ===== P5 流式模式状态（完整模式恒为默认值，行为不变）=====
    private bool _streaming;
    private List<Entity> _streamEnts;
    // Render 期间在线累积的范围（完整模式 ComputeExtents 重置的是 _minX..，二者互不干扰；
    // 流式 ComputeExtents 只做 _st* → _minX.. 回灌，不重置 _st*，供多次 Write* 复用）。
    private double _stMinX = double.MaxValue, _stMinY = double.MaxValue, _stMaxX = double.MinValue, _stMaxY = double.MinValue;
    private bool _stAny;
    // 完整模式 ComputeExtents 的扩展标志（抽出的 ExpandEntityExtents 写此字段）。
    private bool _extAny;

    // [PATCH] 按块实体的规范化文件序迭代：优先 FileOrderEntities（DWG/DXF 解码时由 reader 按
    // 文件序登记，或业务侧登记块时设置）；未设置时退回 Entities（HashSet 后端，迭代 = 哈希序，
    // 跨进程不稳定 → 同一文档两次渲染的元素顺序可能不同，无法复现）。
    private static IEnumerable<Entity> BlockEntitiesInFileOrder(BlockRecord b)
    {
        var fo = b.FileOrderEntities;
        return fo is { Count: > 0 } ? fo : (IEnumerable<Entity>)b.Entities;
    }

    /// <summary>
    /// 进入流式模式：后续 Render 按流序记录实体并在线累积范围；AllEntities/ComputeExtents 走流式分支。
    /// 应在任何 Write* 之前调用；同一实例只调用一次。
    /// </summary>
    /// <param name="seed">可选初始范围（如管线 SQL 外部范围），union 语义只可扩大；默认 null = 纯实体累积。</param>
    public void BeginStreaming(StreamExtents? seed = null)
    {
        if (_streaming) throw new InvalidOperationException("BeginStreaming 只能调用一次");
        _streaming = true;
        _streamEnts = new List<Entity>();
        _stMinX = _stMinY = double.MaxValue;
        _stMaxX = _stMaxY = double.MinValue;
        _stAny = false;
        if (seed is { HasData: true })
        {
            _stMinX = Math.Min(_stMinX, seed.MinX); _stMinY = Math.Min(_stMinY, seed.MinY);
            _stMaxX = Math.Max(_stMaxX, seed.MaxX); _stMaxY = Math.Max(_stMaxY, seed.MaxY);
            _stAny = true;
        }
    }

    /// <summary>按流序推入一个实体（同一对象引用，不克隆），并在线扩展其范围（含块展开/文字盒）。</summary>
    public void Render(Entity e)
    {
        if (e == null) return;
        _streamEnts.Add(e);
        ExpandEntityExtents(e, true);
    }
}

/// <summary>
/// 流式渲染范围累加器（可选）：宿主可在 BeginStreaming 前用管线已知范围（如 SQL 外部范围）
/// 作初始范围（union 语义，只可扩大）。纯实体累积时不必提供。
/// </summary>
public class StreamExtents
{
    public double MinX { get; set; } = double.MaxValue;
    public double MinY { get; set; } = double.MaxValue;
    public double MaxX { get; set; } = double.MinValue;
    public double MaxY { get; set; } = double.MinValue;

    public bool HasData => MinX <= MaxX && MinY <= MaxY;

    public void Expand(double x, double y)
    {
        if (x < MinX) MinX = x; if (y < MinY) MinY = y;
        if (x > MaxX) MaxX = x; if (y > MaxY) MaxY = y;
    }

    public void Expand(double minX, double minY, double maxX, double maxY)
    {
        Expand(minX, minY);
        Expand(maxX, maxY);
    }
}

/// <summary>
/// P5 流式图像渲染入口（public facade，包装 internal 的 <see cref="ImageEngine"/>）：
/// 宿主在管线中按流序经 <see cref="Render"/> 逐实体推入（与 DWG/DXF 直连流同一实体对象，
/// 图框 insert 位于流末），完成后调 <see cref="WritePng"/>/<see cref="WriteSvg"/>/<see cref="WritePdf"/>
/// 直接产出图像 —— 无需中间 DWG 落盘与重读。
/// 实体集合/顺序/范围/显示顺序重排语义与完整模式（重读渲染）完全一致，像素一致由构造保证。
/// 同一实例可复用做多级别缩放与多格式：宿主逐级修改 opt 的
/// OutputZoom/WmtsZ/PngForceTiles 后调 Write* 即可（引擎内部按调用重置全部临时状态）。
/// </summary>
public class StreamingImageEngine
{
    private readonly ImageEngine _eng;

    /// <param name="tablesDoc">管线构建的 doc（全表 + 块定义；ModelSpace 可为空——流式模式不读 ModelSpace）。</param>
    /// <param name="opt">图像参数（可复用实例，每次 Write* 前按需修改）。</param>
    public StreamingImageEngine(CadDocument tablesDoc, ImageConfiguration opt)
    {
        _eng = new ImageEngine(tablesDoc, opt);
        _eng.BeginStreaming();
    }

    /// <summary>按流序推入一个实体。范围在线累积（含 Insert 块展开与文字盒）。</summary>
    public void Render(Entity e) => _eng.Render(e);

    /// <summary>输出 PNG（单张或 WMTS 瓦片树，由 opt.WmtsZ/PngForceTiles/尺寸上限决定）。</summary>
    public ImageExportResult WritePng(string outPath) => _eng.WritePng(outPath);

    /// <summary>输出 SVG（矢量，含透明底/背景选项）。</summary>
    public ImageExportResult WriteSvg(string outPath) => _eng.WriteSvg(outPath);

    /// <summary>输出 PDF（矢量）。</summary>
    public ImageExportResult WritePdf(string outPath) => _eng.WritePdf(outPath);

    /// <summary>源文件路径（用于 RasterImage 等相对路径解析），语义与完整模式包装器一致。</summary>
    public string SourceFilePath { get => _eng.SourceFilePath; set => _eng.SourceFilePath = value; }
}
