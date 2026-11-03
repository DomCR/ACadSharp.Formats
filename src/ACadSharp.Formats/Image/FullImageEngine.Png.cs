using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Formats.Pdf;
using ACadSharp.Text;
using CSMath;

namespace ACadSharp.Formats.Image;

/// <summary>
/// PNG 光栅输出端（T4.9）：用纯托管 RasterCanvas 把与 SVG 端完全相同的几何渲染到位图并写出 PNG
/// （零外部依赖，纯托管软件光栅化渲染）。
/// 渲染语义严格对齐 SVG 端（FullImageEngine.Svg.cs）：1:1 画布 + 2% 边距 + OutputZoom 放大
/// + 黑/白背景 + ACI 官方色表（ACI 7 黑底显白/白底显黑）+ 虚线（图案自路径起点）+ 复杂线型（SHX 真实字形）
/// + NURBS 样条 de Boor 采样 + HATCH（实心/渐变/图案）+ 变宽折线（ITaperOffsetBuilder）+ TEXT/MText
/// （基线=插入点，旋转/倾斜/宽度因子；TTF 用 TrueTypeFont 真字形轮廓填充，SHX 用真实笔迹路径）
/// + 点标记（圆盘）+ 嵌套块展开 + ByBlock/ByLayer 继承。
/// </summary>
internal partial class ImageEngine
{
    // ===== 位图尺寸安全上限（仅 PNG 输出端）=====
    private const int PngMaxSide = 16384;           // 单侧像素上限
    private const long PngMaxPixels = 32_000_000L;  // 总像素上限（≈128MB RGBA）

    // PNG 输出上下文（WritePng 设置；单线程导出，非并发）
    private RasterCanvas _rc;
    private double _pngKx; // 画布 X：每数据单位像素数（整幅统一比例，等比 kx==ky = OutputZoom 或其缩小系数，与 SVG/PDF 一致，分块时各瓦片一致）
    private double _pngKy; // 画布 Y：每数据单位像素数（同 _pngKx；不再从整数画布反算，避免 ≤1px 各向异性漂移）
    private double _tileOx; // 瓦片左上角在整幅画布的像素 X 偏移（单图=0；分块=x0）
    private double _tileOy; // 瓦片左上角在整幅画布的像素 Y 偏移（单图=0；分块=y0）

    // 画布坐标映射（与 SVG 的 Sx/Sy 同语义：Y 翻转、边距 _margin）。
    // 分块时 _pngKx/_pngKy 为整幅全局比例，_tileOx/_tileOy 为当前瓦片在整幅中的像素原点，
    // 映射结果=该数据点在「整幅画布」的像素坐标再减去瓦片原点=瓦片内局部像素坐标；
    // 越界像素由 RasterCanvas.Blend 自动裁剪，瓦片间像素无缝（同一全局网格对齐）。
    private double PngSx(double x) => _margin * _pngKx + (x - _minX) * _pngKx - _tileOx;
    private double PngSy(double y) => _h * _pngKy - (_margin * _pngKy + (y - _minY) * _pngKy) - _tileOy;
    private double PngK => (_pngKx + _pngKy) * 0.5;

    #region ===== 空间剔除 + 文字精灵缓存（降低分块重复渲染）=====
    //
    // 性能优化（2026-10-19 用户：PNG 分块太慢、大量点块/文字被每个瓦片重复渲染）：
    //   ① 空间剔除：预计算每个顶层实体的世界空间 AABB（与 ComputeExtents 同展开，含 Insert 嵌套），
    //      分块时仅对与瓦片数据区域相交（按安全外扩）的实体调用 EmitEntityPng，跳过不相交实体。
    //      像素级精确：AABB 不相交 ⇒ 该实体对本瓦片贡献 0 像素（含线宽/点字形半径/余量外扩保证不裁到）。
    //   ② 点/文字精灵缓存：位置无关图形（点块/文字）按【全局像素比例】渲染一次到离屏透明精灵，再用整数
    //      平移 alpha 合成 blit 到每个相交瓦片，相同符号跨瓦片只光栅化 1 次（大量点块场景收益最大）。
    //      ⚠️ 亚像素相位量化（诚实说明）：精灵把锚点取整到整数像素栅格渲染，blit 亦按整数落点；而直接渲染
    //      （无缓存）锚点落在真实【小数】像素。故缓存路径与直接渲染最多差 0.5px 亚像素相位——填充类字形
    //      在 10× 下不可见，但 1px 细线（加号/X 臂）可能整体位移 1 个像素。空间剔除是逐像素精确的，本缓存
    //      是标准"精灵图集"近似，换取"大量相同符号"的显著提速；若需逐字节一致请走无缓存直接渲染路径。
    //
    private List<Entity> _topEnts;
    private (double minX, double minY, double maxX, double maxY)[] _topAabb;
    private bool _cullReady;
    private bool _tiledMode;                        // 仅分块模式启用精灵缓存（单图无重复，省离屏开销）
    private const int MaxSprites = 8000;           // 精灵缓存上限（防内存膨胀）
    private const int MaxSpriteSide = 8192;        // 单精灵边长上限（超此直接渲染不缓存）
    private readonly Dictionary<string, PngSprite> _sprites = new();

    // 字形精灵缓存（单图 + 分块均启用）：同 SHX 字形 + 同缩放 + 同角度(量化) + 同线宽 + 同颜色
    // 光栅化一次、重复整数平移 blit。治本 SDF 多段路径——85K 字形原本展开成 ~130 万碎段各自走 SDF，
    // 精灵化后仅 (字形,缩放,角度,线宽,颜色) 变体首次光栅化，其余命中 blit（O(精灵面积) 像素拷贝，远快于 SDF）。
    // 与 _sprites（点/文字，分块跨瓦片去重）独立：字形在单图内也大量重复，单图模式同样受益。
    private readonly Dictionary<string, PngSprite> _glyphSprites = new();
    private const int MaxGlyphSprites = 12000;     // 字形精灵上限（变体数受角度量化约束，通常远小于此）
    private long _glyphHit = 0, _glyphMiss = 0;    // perf：命中次数 / 未命中(=新增变体数)

    // ===== 性能计时（临时诊断：定位瓶颈阶段 + 各实体类型耗时分布；GDB2CAD_PERF=1 时输出）=====
    // 渲染为单线程（WritePng 顺序遍历实体/瓦片），字段直接累加无锁。
    private static readonly bool PerfOn =
        string.Equals(Environment.GetEnvironmentVariable("GDB2CAD_PERF"), "1", StringComparison.Ordinal);
    private static readonly bool _perfVerboseTile =
        string.Equals(Environment.GetEnvironmentVariable("GDB2CAD_PERF_TILE"), "1", StringComparison.Ordinal);
    private readonly Stopwatch _swExt = new();     // ComputeExtents（含嵌套展开）
    private readonly Stopwatch _swCull = new();    // BuildCullData（预算世界 AABB）
    private long _pngSaveUs = 0;                   // PNG 编码写盘耗时累加（微秒，各 SavePng 累加）
    private readonly Dictionary<string, long> _perfTypeUs = new(StringComparer.Ordinal); // 各实体类型光栅化耗时（微秒）
    private long _perfRenderCount = 0;             // 实际调用 EmitEntityPng 次数
    private long _perfTextChars = 0;               // TTF/SHX 文字字符数（估文字成本）
    private long _perfStartTick;                   // 顶层 EmitEntityPng 起始 TickCount64
    private string _perfTypeStartType;             // 顶层 EmitEntityPng 实体类型名
    private int _perfDepth = 0;                    // 递归深度（仅顶层计耗，避免嵌套双计）

    // Insert 内容签名精灵缓存（单图 + 分块均启用）：顶层 Insert 按【内容签名（不含落点）】缓存——
    // 同一 (块, 旋转/缩放, 颜色, 线型, 线宽, 层) 组合渲染出的像素内容完全相同，仅落点(插入点)平移不同。
    // 命中→整数平移 blit；未命中→离屏光栅化一次（临时重定位使插入点→(Bx,By)，与 TryEmitSprite 同机制）后 blit。
    // 治 Insert 60s 瓶颈：真实负载顶层 Insert ~174 万次，distinct 内容签名仅 ~5k（重复率 99.7%，Top1 ×110 万），
    // 精灵化后仅 distinct 内容各光栅化 1 次，其余命中 blit（O(精灵面积) 像素拷贝，远快于逐次展开块子实体走 SDF）。
    // 与 _sprites（点，分块）/_glyphSprites（字形）独立：Insert 内容跨【不同实体】重复（非仅跨瓦片重绘），单图同样受益。
    private readonly Dictionary<string, PngSprite> _insertSprites = new();
    private const int MaxInsertSprites = 20000;   // Insert 精灵上限（distinct 内容签名数，防内存膨胀）
    private const long MaxInsertSpriteMemBytes = 150L * 1024 * 1024; // Insert 精灵像素总内存预算 150MB（超则新签名直渲，防 OOM）
    private long _insSpriteHit = 0, _insSpriteMiss = 0, _insSpriteDirect = 0;  // 命中 / 新增变体 / 过大直渲
    private long _insSpriteMemBytes = 0;          // 已缓存精灵像素字节和（内存实测）
    // —— 临时诊断：Insert 内容签名【实际最大边像素】分布（决定精灵化策略）——
    private readonly long[] _insSideHist = new long[8]; // 桶：<256 <1024 <4096 <8192 <16384 <32768 <65536 >=65536（按 distinct 签名计）
    private long _insSigDirect = 0;                   // 走直渲的 distinct 签名数
    private long _insSigCached = 0;                   // 成功入缓存的 distinct 签名数
    private long _insDirectCallWeight = 0;            // 直渲签名对应的调用总次数（估可省时间）
    private readonly Dictionary<string, long> _insSigCalls = new(StringComparer.Ordinal); // 签名→调用次数（估权重）
    // —— 临时诊断：空 AABB 直渲（InsertContentExtents 返回 false）的根因定位 ——
    private long _insTopCalls = 0;                    // 顶层 Insert 总调用（parent==null）
    private long _insAttribGeoHit = 0;                // 有属性 Insert：几何精灵命中（blit + 属性直渲）
    private long _insAttribGeoMiss = 0;               // 有属性 Insert：几何新增变体
    private long _insAttribGeoDirect = 0;             // 有属性 Insert：几何空AABB/过大直渲
    private long _insEmptyAabbCalls = 0;              // 空 AABB 直渲调用次数
    private readonly Dictionary<string, long> _insEmptyAabbTypes = new(StringComparer.Ordinal); // 空 AABB 块【直接子实体类型】分布（按调用）
    private readonly Dictionary<string, long> _insEmptyAabbCounts = new(StringComparer.Ordinal); // 空 AABB 块【直接子实体数量】分布（按调用）
    private long _insDrawContentMs = 0;               // DrawInsertContent 总耗时（直渲+离屏各算一次）
    private long _insDrawContentCalls = 0;            // DrawInsertContent 调用次数
    private long _insDirectDrawMs = 0;                // 空 AABB 直渲路径（DrawInsertContent 含）总耗时

    // —— LwPolyline 子路径细分计时（定位 16s 瓶颈在哪个分支）—— 单位 ms（TickCount64 差值）
    private long _lwComplexMs = 0, _lwComplexCnt = 0;  // 复杂线型 TryEmitComplexPng 命中
    private long _lwSampleMs = 0, _lwSamplePts = 0;    // LwPolylineSamplePoints 采样耗时/总采样点数
    private long _lwTaperMs = 0, _lwTaperCnt = 0;      // 变宽 EmitTaperedPng
    private long _lwStrokeMs = 0, _lwStrokeCnt = 0;    // 普通描边 StrokePts
    private long _lwMaxSegs = 0;                        // 单实体最大采样点数
    // —— 复杂线型 WalkPatternPx 内部分支细分（定位 13s 走查瓶颈）——
    // StrokePolyline 分流累计（顶层实体收尾时从当前 _rc 增量读取清零，跨瓦片/离屏累积）
    private long _sdfCount, _sdfMs, _scanCount, _scanMs;
    private long _sdfSingleCount, _sdfMultiCount, _sdfSingleMs, _sdfMultiMs, _sdfPixelIters, _sdfBlendCount, _sdfBboxArea;
    private long _lwWalkMs = 0;        // WalkPatternPx 走查总耗时
    private long _lwWalkIters = 0;     // 走查 while 循环总迭代次数（=dash+gap+shape+text 段总数）
    private long _lwDashCnt = 0;       // dash 段数（走 AppendSpanPb，全量遍历 pts 的 O(n)）
    private long _lwShapeCnt = 0;      // shape 段数（走 EmitShapeGlyphPb）
    private long _lwTextCnt = 0;       // text 段数（收集后走 DrawTextPng）
    private long _lwDashMs = 0;        // dash 分支累加耗时
    private long _lwShapeMs = 0;       // shape 分支累加耗时
    private long _lwStrokeAfterWalkMs = 0; // ToPolylines + StrokePolyline 耗时
    private long _lwDrawTextMs = 0;      // DrawTextPng 循环耗时

    // 渲染收尾：打印各阶段 + 各实体类型耗时汇总（仅 GDB2CAD_PERF=1 时输出）。
    // 用于定位 PNG 光栅化瓶颈（实体类型分布 / 包围盒 / 剔除 / 编码写盘 各占多少）。
    private void LogPerfSummary()
    {
        if (!PerfOn) return;
        // 注意：_perfTypeUs/_pngSaveUs 存的是 TickCount64 差值 = 毫秒（字段名 us 为历史误名）。
        double typeMs = _perfTypeUs.Values.Sum(v => v);
        _log($"[perf] ===== PNG 耗时汇总 =====");
        _log($"[perf] 包围盒 ComputeExtents：{_swExt.Elapsed.TotalMilliseconds:F0} ms");
        _log($"[perf] 剔除 BuildCullData：{_swCull.Elapsed.TotalMilliseconds:F0} ms（瓦片路径）");
        _log($"[perf] 光栅化 EmitEntityPng：{typeMs:F0} ms（{_perfRenderCount} 次顶层实体调用）");
        _log($"[perf] 编码写盘 SavePng：{_pngSaveUs:F0} ms");
        _log($"[perf] 文字字符数（估文字成本）：{_perfTextChars}");
        _log($"[perf] -- LwPolyline 子路径细分 --");
        _log($"[perf]   复杂线型 TryEmitComplexPng：{_lwComplexMs,8:F0} ms（命中 {_lwComplexCnt} 条）");
        _log($"[perf]     · 走查 WalkPatternPx：{_lwWalkMs,8:F0} ms（迭代 {_lwWalkIters} 次；dash {_lwDashCnt} / shape {_lwShapeCnt} / text {_lwTextCnt}）");
        _log($"[perf]         · dash 分支 AppendSpanPb：{_lwDashMs,7:F0} ms（{_lwDashCnt} 段）");
        _log($"[perf]         · shape 分支 EmitShapeGlyphPb：{_lwShapeMs,6:F0} ms（{_lwShapeCnt} 段；精灵命中 {_glyphHit} / 新增变体 {_glyphMiss} / 缓存表 {_glyphSprites.Count}）");
        _log($"[perf]     · 走查后描边 StrokePolyline：{_lwStrokeAfterWalkMs,4:F0} ms");
        _log($"[perf]     · 文字 DrawTextPng：{_lwDrawTextMs,6:F0} ms");
        _log($"[perf] -- StrokePolyline 分流（全部实体）--");
        _log($"[perf]   SDF 描边（逐像素距离）：{_sdfCount,9:F0} 次 / {_sdfMs,8:F0} ms");
        _log($"[perf]     · 单段快速路径 {_sdfSingleCount:F0} 次 / {_sdfSingleMs,6:F0} ms · 多段 {_sdfMultiCount:F0} 次 / {_sdfMultiMs,6:F0} ms");
        _log($"[perf]     · 内层像素迭代 {_sdfPixelIters:F0} 次 / 实际 Blend {_sdfBlendCount:F0} 次 / bbox 面积和 {_sdfBboxArea:F0} px²");
        _log($"[perf]   ScanFill 描边（多边形外扩+扫描填充）：{_scanCount,3:F0} 次 / {_scanMs,8:F0} ms");
        _log($"[perf]   采样 LwPolylineSamplePoints：{_lwSampleMs,8:F0} ms（总点数 {_lwSamplePts}，单实体最大 {_lwMaxSegs} 点）");
        _log($"[perf]   变宽 EmitTaperedPng：{_lwTaperMs,8:F0} ms（{_lwTaperCnt} 条）");
        _log($"[perf]   描边 StrokePts：{_lwStrokeMs,8:F0} ms（{_lwStrokeCnt} 条）");
        _log($"[perf] -- Insert 内容签名精灵缓存 -- 命中 {_insSpriteHit} / 新增变体 {_insSpriteMiss} / 过大直渲 {_insSpriteDirect} / 缓存表 {_insertSprites.Count} / 像素内存 {_insSpriteMemBytes / 1024 / 1024} MB");
        if (PerfOn)
        {
            string[] bn = { "<256", "<1024", "<4096", "<8192", "<16384", "<32768", "<65536", ">=65536" };
            var parts = new List<string>();
            for (int b = 0; b < 8; b++) if (_insSideHist[b] > 0) parts.Add($"{bn[b]}:{_insSideHist[b]}");
            long sigTotal = _insSideHist.Sum();
            _log($"[perf]   · 顶层 Insert 调用 {_insTopCalls}");
            _log($"[perf]   · 有属性 Insert 几何缓存：命中 {_insAttribGeoHit} / 新增 {_insAttribGeoMiss} / 空AABB直渲 {_insAttribGeoDirect}");
            _log($"[perf]   · 内容签名实际最大边像素分布(distinct {sigTotal}个): {string.Join("  ", parts)}");
            _log($"[perf]   · 入缓存签名 {_insSigCached} / 直渲签名 {_insSigDirect} / 直渲签名覆盖调用总次数(可省权重) {_insDirectCallWeight}");
            _log($"[perf]   · 空AABB直渲调用 {_insEmptyAabbCalls}（耗时 {_insDirectDrawMs:F0} ms）");
            if (_insEmptyAabbTypes.Count > 0)
            {
                var topTypes = _insEmptyAabbTypes.OrderByDescending(kv => kv.Value).Take(8)
                    .Select(kv => $"{kv.Key}:{kv.Value}").ToList();
                _log($"[perf]     空AABB块直接子实体类型分布(top8): {string.Join("  ", topTypes)}");
            }
            if (_insEmptyAabbCounts.Count > 0)
            {
                var topCnt = _insEmptyAabbCounts.OrderByDescending(kv => kv.Value).Take(8)
                    .Select(kv => $"子实体数{kv.Key}:{kv.Value}").ToList();
                _log($"[perf]     空AABB块直接子实体数量分布(top8): {string.Join("  ", topCnt)}");
            }
        }
        foreach (var kv in _perfTypeUs.OrderByDescending(p => p.Value))
            _log($"[perf]   {kv.Key,-14} {kv.Value,9:F0} ms");
        _log($"[perf] ============================");
    }

    private sealed class PngSprite
    {
        public byte[] Rgba;   // 精灵透明 RGBA 像素
        public int W, H;      // 精灵像素尺寸
        public int Bx, By;    // 精灵内锚点（图形中心/文字基线起点）的整数局部坐标
    }

    // 可变 AABB 持有器：局部函数 Ex 通过它累加（out 参数不能在 lambda/局部函数中赋值，故用引用类型）。
    private sealed class AabbBox
    {
        public double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
        public bool any = false;
        public void Ex(double x, double y) { if (x < mnx) mnx = x; if (y < mny) mny = y; if (x > mxx) mxx = x; if (y > mxy) mxy = y; any = true; }
    }

    // 取实体的世界空间 AABB（与 ComputeExtents 的逐类型展开一致；Insert 用 ExpandInsert 世界点）。
    // 未知类型/无限线(XLine/Ray)返回覆盖全图盒（保证不被剔除）。
    // finiteOnly=true（离群范围守护专用）：未知类型/无限线无局部几何 → 返回 false（不参与分位、不扩展范围）。
    private bool EntityWorldAabb(Entity ent, out double mnx, out double mny, out double mxx, out double mxy, bool finiteOnly = false)
    {
        var b = new AabbBox();
        switch (ent)
        {
            case Point pt: b.Ex(pt.Location.X, pt.Location.Y); break;
            case Line ln: b.Ex(ln.StartPoint.X, ln.StartPoint.Y); b.Ex(ln.EndPoint.X, ln.EndPoint.Y); break;
            case MLine ml: foreach (var el in MLineElements(ml, null)) foreach (var p in el.Center) b.Ex(p.X, p.Y); break;
            case LwPolyline p2: foreach (var p in LwPolylineSamplePoints(p2, null)) b.Ex(p.X, p.Y); break;
            case Polyline3D p3: foreach (var v in p3.Vertices) if (IsDisplayVertex(v)) b.Ex(v.Location.X, v.Location.Y); break;
            case Polyline2D p2d: foreach (var v in p2d.Vertices) if (IsDisplayVertex(v)) b.Ex(v.Location.X, v.Location.Y); break;
            case Mesh mesh: if (mesh.Vertices != null) foreach (var v in mesh.Vertices) b.Ex(v.X, v.Y); break;
            case PolyfaceMesh pf: if (pf.Vertices != null) foreach (var v in pf.Vertices) b.Ex(v.Location.X, v.Location.Y); break;
            case PolygonMesh pm: if (pm.Vertices != null) foreach (var v in pm.Vertices) b.Ex(v.Location.X, v.Location.Y); break;
            case Wipeout wo: foreach (var p in WipeoutWorldPoly(wo, null)) b.Ex(p.X, p.Y); break;
            case Arc a: b.Ex(a.Center.X - a.Radius, a.Center.Y - a.Radius); b.Ex(a.Center.X + a.Radius, a.Center.Y + a.Radius); break;
            case Circle c: b.Ex(c.Center.X - c.Radius, c.Center.Y - c.Radius); b.Ex(c.Center.X + c.Radius, c.Center.Y + c.Radius); break;
            case Ellipse el: { double r = Math.Max(el.MajorAxis, el.MinorAxis) / 2.0; b.Ex(el.Center.X - r, el.Center.Y - r); b.Ex(el.Center.X + r, el.Center.Y + r); break; }
            case TextEntity t: ExpandTextBox(t.InsertPoint.X, t.InsertPoint.Y, TextMetrics(t), b.Ex); break;
            case MText mt: ExpandTextBox(mt.InsertPoint.X, mt.InsertPoint.Y, TextMetrics(mt), b.Ex); break;
            case Solid sd: b.Ex(sd.FirstCorner.X, sd.FirstCorner.Y); b.Ex(sd.SecondCorner.X, sd.SecondCorner.Y); b.Ex(sd.ThirdCorner.X, sd.ThirdCorner.Y); b.Ex(sd.FourthCorner.X, sd.FourthCorner.Y); break;
            case Face3D fd: b.Ex(fd.FirstCorner.X, fd.FirstCorner.Y); b.Ex(fd.SecondCorner.X, fd.SecondCorner.Y); b.Ex(fd.ThirdCorner.X, fd.ThirdCorner.Y); b.Ex(fd.FourthCorner.X, fd.FourthCorner.Y); break;
            case Spline sp: foreach (var p in SplineSample(sp, null, 4)) b.Ex(p.X, p.Y); break;
            case Hatch ht: foreach (var loop in BuildHatchLoops(ht, null)) foreach (var p in loop) b.Ex(p.X, p.Y); break;
            case RasterImage ri:
                {
                    b.Ex(ri.InsertPoint.X, ri.InsertPoint.Y);
                    b.Ex(ri.InsertPoint.X + ri.UVector.X * ri.Size.X, ri.InsertPoint.Y + ri.UVector.Y * ri.Size.Y);
                    b.Ex(ri.InsertPoint.X + ri.VVector.X * ri.Size.Y, ri.InsertPoint.Y + ri.VVector.Y * ri.Size.Y);
                    break;
                }
            case Ole2Frame olef:
                {
                    // OLE2 嵌入对象：UL/LR 两角（与 ComputeExtents 一致，防分块渲染误剔除）
                    b.Ex(Math.Min(olef.UpperLeftCorner.X, olef.LowerRightCorner.X), Math.Min(olef.UpperLeftCorner.Y, olef.LowerRightCorner.Y));
                    b.Ex(Math.Max(olef.UpperLeftCorner.X, olef.LowerRightCorner.X), Math.Max(olef.UpperLeftCorner.Y, olef.LowerRightCorner.Y));
                    break;
                }
            case Insert ins: ExpandInsert(ins, b.Ex); break;
            case Leader ld: ExpandLeaderExtents(ld, _doc, b.Ex); break; // 与 ComputeExtents 同展开（含样条过冲+箭头 bbox）
            case XLine _:
                if (finiteOnly) { mnx = mny = double.MaxValue; mxx = mxy = double.MinValue; return false; } // 无限线无局部几何：范围守护跳过（渲染时画布裁剪自然处理）
                b.mnx=_minX; b.mny=_minY; b.mxx=_maxX; b.mxy=_maxY; b.any=true; break; // 无限线：不剔除
            case Ray _:
                if (finiteOnly) { mnx = mny = double.MaxValue; mxx = mxy = double.MinValue; return false; }
                b.mnx=_minX; b.mny=_minY; b.mxx=_maxX; b.mxy=_maxY; b.any=true; break;
            default:
                if (finiteOnly) { mnx = mny = double.MaxValue; mxx = mxy = double.MinValue; return false; } // 未知：不剔除（安全）；范围守护跳过
                b.mnx=_minX; b.mny=_minY; b.mxx=_maxX; b.mxy=_maxY; b.any=true; break;
        }
        mnx=b.mnx; mny=b.mny; mxx=b.mxx; mxy=b.mxy;
        return b.any;
    }

    // 递归取文档所有实体（含块嵌套）在各自缩放下的最大线宽半宽（数据单位），用于剔除安全外扩。
    private double MaxHalfStroke(Entity ent, double scaleMul)
    {
        double max = 0;
        if (ent is Insert ins)
        {
            if (ins.Block == null) return 0;
            double s = ins.XScale; if (ins.YScale > s) s = ins.YScale; if (s < 0) s = -s;
            double cs = scaleMul * (s > 1e-9 ? s : 1.0);
            foreach (var e in BlockEntitiesInFileOrder(ins.Block)) { double m = MaxHalfStroke(e, cs); if (m > max) max = m; }
            if (ins.Attributes != null) foreach (var a in ins.Attributes) { double m = MaxHalfStroke(a, scaleMul); if (m > max) max = m; }
            return max;
        }
        // 线型实体（含线宽）
        try { max = StrokeWidth(ent, scaleMul) * 0.5; } catch { max = 0; }
        return max;
    }

    // 预计算顶层实体世界 AABB（含安全外扩），供分块按瓦片剔除。须在 _h/_w 定稿（ExpandViewForPointGlyphs 后）调用。
    private void BuildCullData()
    {
        _topEnts = AllEntities().ToList();
        var abbs = new (double minX, double minY, double maxX, double maxY)[_topEnts.Count];
        double maxHalf = 0;
        foreach (var e in _topEnts) { double m = MaxHalfStroke(e, 1.0); if (m > maxHalf) maxHalf = m; }
        double inflate = maxHalf + PointGlyphRadius() + 1.0; // 线宽半宽 + 点字形半径 + 余量（数据单位）
        for (int i = 0; i < _topEnts.Count; i++)
        {
            if (EntityWorldAabb(_topEnts[i], out double a, out double b, out double c, out double d))
                abbs[i] = (a - inflate, b - inflate, c + inflate, d + inflate);
            else
                abbs[i] = (-double.MaxValue, -double.MaxValue, double.MaxValue, double.MaxValue); // 空盒=总是渲染（保守）
        }
        _topAabb = abbs;
        _cullReady = true;
    }

    // 与瓦片数据区域相交的顶层实体（剔除后）。dx0/dy0=瓦片数据左下，dx1/dy1=瓦片数据右上。
    private IEnumerable<Entity> CulledTopEntities(double dx0, double dy0, double dx1, double dy1)
    {
        if (!_cullReady) { foreach (var e in AllEntities()) yield return e; yield break; }
        for (int i = 0; i < _topEnts.Count; i++)
        {
            var bb = _topAabb[i];
            if (bb.minX < dx1 && bb.maxX > dx0 && bb.minY < dy1 && bb.maxY > dy0)
                yield return _topEnts[i];
        }
    }

    // 把缓存精灵 blit 到当前瓦片画布：锚点(全局像素)对齐 sp 内 (Bx,By)，整数平移（越界由 BlitRgba 裁剪）。
    // 光栅化是像素位置的纯函数，整数平移不改变亚像素 AA 覆盖 → 与逐瓦片重绘视觉一致。
    private void BlitSprite(PngSprite sp, double anchorX, double anchorY)
    {
        // 锚点全局像素 = 当前 PngSx/PngSy + 当前瓦片偏移（还原到整幅画布系）
        double gxg = PngSx(anchorX) + _tileOx;
        double gyg = PngSy(anchorY) + _tileOy;
        int sx = (int)Math.Round(gxg - sp.Bx - _tileOx);
        int sy = (int)Math.Round(gyg - sp.By - _tileOy);
        _rc.BlitRgba(sp.Rgba, sp.W, sp.H, sx, sy);
    }

    // 通用精灵缓存渲染（位置无关图形：点块/文字/MText）。命中→blit；未命中→离屏按全局比例渲染一次缓存后 blit。
    //   anchorX/Y：图形锚点（世界数据坐标）；点=中心、文字=基线起点。
    //   left/right：锚点左/右延伸（数据单位，>=0）；top/bottom：锚点上方/下方延伸（数据单位，>=0，世界 y 向上）。
    //   draw：把该图形画到「当前 _rc」的委托（内部用 PngSx/PngSy 映射世界点 → 随 _rc/_tileOx/_tileOy 重定位）。
    // 关键：渲染离屏精灵时临时把 _tileOx/_tileOy 设为「全局像素 - 锚点局部偏移」，使 PngSx(anchor)=Bx、PngSy(anchor)=By，
    //       从而同一图形在离屏精灵与任意瓦片里像素坐标完全一致（仅整数平移之差）。
    private bool TryEmitSprite(string key, double anchorX, double anchorY,
        double left, double right, double top, double bottom, Action draw)
    {
        if (_sprites.TryGetValue(key, out var hit)) { BlitSprite(hit, anchorX, anchorY); return true; }

        double k = PngK;
        int pad = 2;
        int Bx = (int)Math.Ceiling(Math.Max(left, 0) * k) + pad;     // 锚点在精灵内的 x
        int By = (int)Math.Ceiling(Math.Max(top, 0) * k) + pad;       // 锚点在精灵内的 y（上=small y）
        int sw = (int)Math.Ceiling((Math.Max(left, 0) + Math.Max(right, 0)) * k) + Bx + pad;
        int sh = (int)Math.Ceiling((Math.Max(top, 0) + Math.Max(bottom, 0)) * k) + By + pad;
        if (sw < 1) sw = 1; if (sh < 1) sh = 1;
        if (sw > MaxSpriteSide || sh > MaxSpriteSide || _sprites.Count >= MaxSprites)
        {
            draw(); // 过大/缓存满：直接渲染（仍受剔除保护）
            return true;
        }

        var sprite = new RasterCanvas(sw, sh);
        sprite.Clear(0, 0, 0, 0);
        // 临时重定位：让锚点映射到精灵内 (Bx,By)。PngSx_global(a)=PngSx(当前)(a)+_tileOx；
        // 设 _tileOx = PngSx_global(a) - Bx ⇒ PngSx(精灵)(a)=PngSx_global(a)-_tileOx=Bx。
        double prevOx = _tileOx, prevOy = _tileOy; var prevRc = _rc;
        _rc = sprite;
        _tileOx = PngSx(anchorX) + _tileOx - Bx;
        _tileOy = PngSy(anchorY) + _tileOy - By;
        try { draw(); } finally { _rc = prevRc; _tileOx = prevOx; _tileOy = prevOy; }

        var sp = new PngSprite { Rgba = sprite.ToRgbaBuffer(), W = sw, H = sh, Bx = Bx, By = By };
        _sprites[key] = sp;
        BlitSprite(sp, anchorX, anchorY);
        return true;
    }

    #endregion

    #region ===== 颜色工具 =====

    // hex(#RRGGBB) → Rgb（带缓存；引擎内所有颜色均为该格式）
    private static readonly ConcurrentDictionary<string, RasterCanvas.Rgb> _hexRgbCache = new();
    private static RasterCanvas.Rgb HexToRgb(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return RasterCanvas.Rgb.White;
        if (_hexRgbCache.TryGetValue(hex, out var c)) return c;
        RasterCanvas.Rgb col;
        try
        {
            col = new RasterCanvas.Rgb(
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
        }
        catch { col = RasterCanvas.Rgb.White; }
        _hexRgbCache[hex] = col;
        return col;
    }

    #endregion

    #region ===== PNG 导出入口 =====

    // PNG 文字对齐宽度（真实字体度量，数据单位）：用 TrueTypeFont 实测字宽（CJK≈1em、拉丁≈0.5em），
    // 再乘宽度因子。修正 EstimateTextWidth 估算值（CJK 1.0em/半角 0.55em）与真实字宽不一致导致的
    // 居中/右对正/中间对齐文字漂移。解析失败（字体族不可用）回退到 EstimateTextWidth 估算。
    private double PngTextWidth(TextEntity t, string family)
    {
        string txt = t.Value ?? "";
        double wf = Math.Max(t.WidthFactor, 1e-9);
        if (txt.Length == 0) return 0;
        var fr = PickFont(family, txt);
        if (fr == null) return EstimateTextWidth(txt, t.Height) * wf; // 无字体 → 估算兜底
        double total = 0;
        for (int i = 0; i < txt.Length; i++)
            // 必须 (int) 强转：传 char 时 C# 重载决议优先绑定 GetAdvanceWidth(ushort glyphIndex)
            // （char→ushort 是无符号精确转换，优于 char→int），把 Unicode 码点当字形索引 → 越界返回 0。
            // 中文汉字码点大（U+4E00+）必越界 → advance 读成 0 → 渲染时后续字符叠上前字符（"文字重叠"）。
            total += fr.GetAdvanceWidth((int)txt[i]) / fr.UnitsPerEm;
        // cap-height 字号映射（与 DrawTextPng 的 fs=H*TtfCapScale 一致，见 TtfCapScale）：
        // 实际渲染宽度 = total/em * fs * wf = total/em * H * capScale * wf。
        return total * t.Height * TtfCapScale(family) * wf;
    }

    public ImageExportResult WritePng(string outPath)
    {
        // P6.5：相位边界回收——前置 DWG 写出阶段留下未回收垃圾（T11 实测 PNG 入口 GC存活≈419MB，
        // 而无图路径 peakWS 仅 268MB）。入口显式一次 gen2 回收，配合渲染循环的条带边界回收保持 WS 有界
        // （T11 门禁 peakWS<500MB）。单次 Collect 对批处理工具是标准相位边界手法，代价 ~50-200ms。
        GC.Collect();
        if (PerfOn) _swExt.Restart();
        if (!ComputeExtents())
        {
            _log("⚠️  PNG 无可用实体，跳过");
            return new ImageExportResult { Success = false };
        }
        if (PerfOn) _swExt.Stop();

        _scale = 1.0; // 1:1 实际尺寸
        double dataW = _maxX - _minX;
        double dataH = _maxY - _minY;
        _margin = Math.Max(dataW, dataH) * 0.02 + 1.0; // 少量边距（实际尺寸）
        if (!double.IsFinite(_margin) || _margin <= 0) _margin = 2.0;
        _w = dataW + 2 * _margin;
        _h = dataH + 2 * _margin;
        ExpandViewForPointGlyphs(); // 点样式字形（r 可达视高 5%）四边扩界，避免边缘点被裁

        BuildLinetypeDash();

        double zoom = _opt.OutputZoom > 0 ? _opt.OutputZoom : 1.0;
        // ===== 统一比例语义（2026-10-22 修复：SVG/PDF/PNG 缩放不一致）=====
        // PNG 以恰好 zoom px/数据单位 为统一比例（kx==ky），与 SVG/PDF 的「1:1 内容放大 zoom 倍」严格一致；
        // 整数画布 = 理想画布(_w·zoom × _h·zoom) 向上取整，亚像素余量(<1px)留在右/下缘为空白边，不拉伸铺满。
        // 旧实现从整数画布反算比例（fullW/_w、fullH/_h）→ 各向异性漂移（如 10.0024×10.0027，与矢量端 10×10 不一致）。
        int fullW = (int)Math.Ceiling(_w * zoom);
        int fullH = (int)Math.Ceiling(_h * zoom);
        if (fullW < 1) fullW = 1;
        if (fullH < 1) fullH = 1;
        bool overBitmap = fullW > PngMaxSide || fullH > PngMaxSide || (long)fullW * fullH > PngMaxPixels;
        // 分块触发阈值（cad2png 2026-11-02）：画布任一维 > PngTileThreshold 时也走分块（如 4096，
        // 「大于 4096×4096 自动分片」）；0 = 旧语义（仅超位图上限才分块）。
        bool overThreshold = _opt.PngTileThreshold > 0 && (fullW > _opt.PngTileThreshold || fullH > _opt.PngTileThreshold);
        bool overLimit = overBitmap || overThreshold;
        ReportProgress("extents", 0.08, 0, 0, $"画布 {fullW}×{fullH}px @1:1×{F(zoom)}");

        // WMTS 缩放级别 z：多级导出时由宿主传 0-based 级别索引（WmtsZ）；单级导出回退 OutputZoom 四舍五入（旧行为）。
        int zLevel = _opt.WmtsZ ?? Math.Max(0, (int)Math.Round(zoom));
        // 多级导出（WmtsZ 非 null）且强制网格：即使未超限也输出 tiles/{z}/0/0.png（1×1 网格），
        //   使各级别（单图/分块）统一落入同一 tiles 树，便于下游按 WMTS 协议取图（2026-10-21 用户）。
        bool forceTiles = _opt.WmtsZ != null && _opt.PngForceTiles;

        // ① 未超限且无需强制网格：单图 1:1×zoom 原尺寸（统一比例 = zoom）
        if (!overLimit && !forceTiles)
            return WritePngSingle(outPath, fullW, fullH, zoom, false);

        // ② 超限 + 合并（PngMergeToSingle=true）且未强制网格：合并为 1 张
        if (overBitmap && _opt.PngMergeToSingle && !forceTiles)
        {
            if (_opt.PngMergeScaleToCap)
            {
                // 旧行为（回退开关 PngMergeScaleToCap=true）：等比缩小到上限内合并。
                // 从理想（未取整）画布反解缩小系数，再以统一比例 k=zoom·f 缩小；画布向下取整保证 ≤ 上限
                double idealW = _w * zoom, idealH = _h * zoom;
                double f = Math.Min(PngMaxSide / idealW, PngMaxSide / idealH);
                if (idealW * idealH > PngMaxPixels)
                    f = Math.Min(f, Math.Sqrt(PngMaxPixels / (idealW * idealH)));
                double k = zoom * f;
                int W = Math.Max(1, (int)Math.Floor(idealW * f));
                int H = Math.Max(1, (int)Math.Floor(idealH * f));
                _log($"⚠️  PNG 画布超出位图上限（初始 1:1×{F(zoom)} = {fullW}×{fullH}），已开启合并 → 统一比例缩小合并为 1 张 {W}×{H} @ {F(k)}×");
                return WritePngSingle(outPath, W, H, k, true);
            }
            // P6 默认：原分辨率行流合并——合并图 = 整幅原分辨率（不缩小、不丢清晰度，内存 O(行)）
            return WritePngNativeMerge(outPath, fullW, fullH, zoom);
        }

        // ③ 超限/超阈值分块 / 强制网格（单图也按 1×1 瓦片）：按网格流式输出（每张 ≤ 单侧/总像素上限，不缩小、不丢清晰度）
        return WritePngTiled(outPath, fullW, fullH, zoom, zLevel);
    }

    // 单图渲染（未超限原尺寸 / 超限缩小合并（PngMergeScaleToCap=true 回退） 共用）：建画布→清背景→逐实体光栅化→存盘。
    // k = 统一比例（px/数据单位，kx==ky）：正常单图 = OutputZoom；缩小合并图 = OutputZoom×缩小系数（与 SVG/PDF 同义）。
    // （P6 默认的原分辨率行流合并走 WritePngNativeMerge，不经过本方法。）
    private ImageExportResult WritePngSingle(string outPath, int W, int H, double k, bool merged)
    {
        _skippedCounts.Clear();
        _tiledMode = false; _sprites.Clear(); // 单图：无跨瓦片重复，关闭点精灵缓存（省离屏开销）
        _glyphSprites.Clear(); _glyphHit = 0; _glyphMiss = 0; // 字形精灵缓存：单图内 SHX 字形大量重复，单图同样受益
        _insertSprites.Clear(); _insSpriteHit = 0; _insSpriteMiss = 0; _insSpriteDirect = 0; _insSpriteMemBytes = 0; // Insert 内容签名精灵：单图内大量重复，单图同样受益
        _useRealTextWidth = true; // PNG 端：文字对齐锚点用真实字体度量（修正居中/右对正漂移）
        var rc = new RasterCanvas(W, H);
        ClearPngBg(rc);
        _rc = rc;
        // 统一比例（kx==ky）：与 SVG/PDF 严格一致；不再从整数画布反算（那会引入 ≤1px 的
        // 各向异性漂移，导致 PNG 与矢量端缩放不一致）。内容锚定左上角，右/下缘余量(<1px)为空白边。
        _pngKx = k;
        _pngKy = k;
        _tileOx = 0; _tileOy = 0; // 单图：无瓦片偏移，整幅画布从像素原点起

        var _ents = AllEntities().ToList();
        int _entTotal = _ents.Count;
        for (int _ei = 0; _ei < _entTotal; _ei++)
        {
            ReportEntityProgress("render", _ei, _entTotal, _ents[_ei]);
            EmitEntityPng(_ents[_ei], null, 1.0, 7, null, null, _ltScale); // 顶层无 INSERT 上下文，ByBlock 兜底白/实线
        }
        LogSkipped("PNG");

        ReportProgress("save", 0.92, _entTotal, _entTotal, "写盘");
        long _saveT0 = Environment.TickCount64;
        bool _saveOk = rc.SavePng(outPath);
        _pngSaveUs += Environment.TickCount64 - _saveT0;
        if (!_saveOk)
        {
            _log($"❌  PNG 写文件失败：{outPath}");
            _rc = null;
            return new ImageExportResult { Success = false };
        }
        _rc = null;
        ReportProgress("done", 1.0, _entTotal, _entTotal, outPath);
        _log($"✅  PNG 已生成{(merged ? "（合并单图）" : "")}：{outPath}（画板 {W}×{H}px，1:1 内容放大 {F(k)} 倍（统一比例，与 SVG/PDF 一致）{(_transparentBg ? "，透明背景" : "")}）");
        LogPerfSummary();
        return PngResult(W, H, merged, k, System.Array.Empty<ImageTile>());
    }

    // 分块（瓦片）渲染：整幅 1:1×zoom 画布按 cols×rows 网格切分，每个瓦片建独立小画布，
    // 把整幅场景画进该瓦片画布（PngSx/PngSy 按瓦片 _pngKx/_pngKy 映射，越界像素由 RasterCanvas.Blend 自动裁剪），
    // ===== PNG 编码并行化（2026-10-22 性能优化）=====
    // GDB2CAD_PERF=1 实测（XCTG-2000，110 张 4096² 瓦片）：编码写盘 SavePng 13.1s 占总耗时 25%，
    // 与光栅化 21.5s 串行执行。每瓦片独立 new RasterCanvas（像素缓冲不复用，渲染完 _rc=null 即解绑，
    // SavePng 后 rc 不再被触碰）→ 可零拷贝交后台并行「编码+写盘」，与后续瓦片光栅化重叠。
    // 背压：未完成编码任务数达上限时渲染线程等待，防 64MB/张 的像素缓冲堆积（峰值≈上限×64MB）。
    // 失败语义：任一瓦片编码/写盘失败 → FlushPngSaves 返回 false → 整次导出失败（与原同步一致）。
    private const int PngSaveBacklog = 2;   // 未完成编码任务上限（≈2×64MB=128MB 像素内存峰值；P6.5：8→2 削瓦片路径 backlog 峰值 512MB→128MB，编码延迟仍被光栅化掩盖）
    private readonly List<Task> _pngSaveTasks = new();
    private int _pngSaveFailed = 0;
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _pngSaveFailedPaths = new();

    // ===== 专用 PNG 编码工作池（2026-10-31，修复瓦片导出无限挂起）=====
    // 实测（.NET 10 AOT，win-x64）：编码经 Task.Run 交运行时线程池后，在特定调度时序下池子
    // "不再新增 worker"：12 个编码任务排队、进程仅剩 2 线程（1 个空转 ~1 核 20+ 分钟）、
    // 渲染线程卡死在 FlushPngSaves/Task.WaitAll（z0 或 z1 任意瓦片处均可发生）。
    // 排除：SetMinThreads 无效（懒生成，仍走同一不可靠路径）；DOTNET_ThreadPool_ForceMinWorkerThreads=64
    // （启动时预建）可 100% 消除（同命令 20min 挂起 → 2.4s）。故此处自建固定线程池：
    // 8 个后台线程随类型加载一次性创建，彻底绕开运行时池"按需增长"路径，饥饿不再可能。
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> _encQueue =
        new System.Collections.Concurrent.BlockingCollection<Action>(
            new System.Collections.Concurrent.ConcurrentQueue<Action>(), 256);

    static ImageEngine()
    {
        for (int i = 0; i < 8; i++)
        {
            var t = new System.Threading.Thread(() =>
            {
                foreach (var act in _encQueue.GetConsumingEnumerable())
                {
                    try { act(); } catch { /* SavePng 内部已 try/catch 返回 false；此处双保险防 worker 死掉 */ }
                }
            })
            { IsBackground = true, Name = "PngEnc" + i };
            t.Start();
        }
    }

    /// <summary>异步保存 PNG：编码+写盘在专用后台池执行，渲染线程立即返回继续渲染下一瓦片。</summary>
    private void SavePngAsync(RasterCanvas rc, string path)
    {
        // 背压：清理已完成任务；仍达上限则等待任一完成（渲染线程短暂阻塞，内存有界）。
        // P6.5 修复：旧实现仅入循环前 RemoveAll 一次，而 Task.WaitAny 不改列表 → 任务完成后
        // “计数仍≥上限 && WaitAny 立即返回” 死循环忙等（100% CPU 自旋，测试套件实测挂起 44 分钟）。
        // 现每轮先移除已完成任务，仍达上限才等待；等待返回后必再清理，无自旋窗口。
        if (_pngSaveTasks.Count >= PngSaveBacklog)
        {
            while (true)
            {
                _pngSaveTasks.RemoveAll(t => t.IsCompleted);
                if (_pngSaveTasks.Count < PngSaveBacklog) break;
                Task.WaitAny(_pngSaveTasks.ToArray());
            }
        }
        long t0 = Environment.TickCount64;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _encQueue.Add(() =>
        {
            bool ok = rc.SavePng(path);
            if (!ok)
            {
                Interlocked.Exchange(ref _pngSaveFailed, 1);
                _pngSaveFailedPaths.Add(path);
            }
            Interlocked.Add(ref _pngSaveUs, Environment.TickCount64 - t0);
            tcs.TrySetResult(ok);
        });
        _pngSaveTasks.Add(tcs.Task);
    }

    /// <summary>等待全部后台编码写盘完成（瓦片循环结束后调用）。返回 false = 任一失败。</summary>
    private bool FlushPngSaves()
    {
        Task.WaitAll(_pngSaveTasks.ToArray());
        _pngSaveTasks.Clear();
        bool ok = Interlocked.CompareExchange(ref _pngSaveFailed, 0, 0) == 0;
        if (!ok)
            foreach (var p in _pngSaveFailedPaths)
                _log($"❌  PNG 瓦片写文件失败：{p}");
        return ok;
    }

    // 流式各存一个 PNG。瓦片间像素无缝（同一全局像素网格对齐），合并起来即完整画布。
    private ImageExportResult WritePngTiled(string outPath, int fullW, int fullH, double zoom, int zLevel)
    {
        // 瓦片边长：min(PngTileSize, 单侧上限)，再保证方形瓦片 tile×tile ≤ 总像素上限
        int tile = _opt.PngTileSize > 0 ? _opt.PngTileSize : 4096;
        tile = Math.Min(tile, PngMaxSide);
        while (tile > 1 && (long)tile * tile > PngMaxPixels) tile--;
        int cols = (int)Math.Ceiling(fullW / (double)tile);
        int rows = (int)Math.Ceiling(fullH / (double)tile);

        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        string ext = string.IsNullOrEmpty(Path.GetExtension(outPath)) ? ".png" : Path.GetExtension(outPath);

        _skippedCounts.Clear();
        _tiledMode = true; _sprites.Clear(); // 分块模式启用点精灵缓存：相同点符号跨瓦片只渲染一次、其余瓦片整数平移 blit
        _glyphSprites.Clear(); _glyphHit = 0; _glyphMiss = 0; // 字形精灵缓存跨瓦片复用
        _insertSprites.Clear(); _insSpriteHit = 0; _insSpriteMiss = 0; _insSpriteDirect = 0; _insSpriteMemBytes = 0; // Insert 内容签名精灵跨瓦片复用
        _useRealTextWidth = true;
        if (PerfOn) _swCull.Restart();
        BuildCullData(); // 预计算顶层实体世界 AABB（含线宽/点字形安全外扩），供按瓦片空间剔除
        if (PerfOn) _swCull.Stop();

        var tiles = new List<ImageTile>();
        int seq = 0;
        int emptySkipped = 0; // 无数据瓦片跳过计数（SkipEmptyTiles，2026-11-02 用户要求）
        long renderedPairs = 0, totalPairs = 0;
        int _tileTotal = cols * rows;
        int _tileDone = 0;
        for (int cy = 0; cy < rows; cy++)
        {
            int y0 = cy * tile;
            int th = Math.Min(tile, fullH - y0);
            for (int cx = 0; cx < cols; cx++)
            {
                int x0 = cx * tile;
                int tw = Math.Min(tile, fullW - x0);
                if (tw < 1 || th < 1) continue;
                _tileDone++;
                ReportProgress("tile", 0.10 + 0.87 * _tileDone / (double)Math.Max(1, _tileTotal), _tileDone, _tileTotal, $"tile({cx},{cy}) 渲染");

                var rc = new RasterCanvas(tw, th);
                ClearPngBg(rc);
                _rc = rc;
                // 整幅统一比例 = 恰好 zoom（各瓦片一致，与 SVG/PDF 严格一致；语义见 WritePng），
                // 配合 _tileOx/_tileOy 把整幅场景映射到该瓦片局部坐标系；
                // 越界像素由 RasterCanvas.Blend 裁剪 → 每瓦片显示对应子区域，瓦片间无缝拼接。
                _pngKx = zoom; _pngKy = zoom;
                _tileOx = x0; _tileOy = y0;

                // 空间剔除：仅渲染与瓦片数据区域相交的顶层实体（AABB 不相交 ⇒ 本瓦片 0 像素贡献，像素级安全）。
                // 瓦片像素 [x0,x0+tw]×[y0,y0+th] → 数据坐标矩形（Y 翻转）。
                double dx0 = _minX - _margin + x0 / (double)_pngKx;
                double dx1 = _minX - _margin + (x0 + tw) / (double)_pngKx;
                double dy1 = _maxY + _margin - y0 / (double)_pngKy;
                double dy0 = _maxY + _margin - (y0 + th) / (double)_pngKy;
                totalPairs += _topEnts != null ? _topEnts.Count : 0;
                long _tileRenderT0 = Environment.TickCount64;
                int _tileEnts = 0;
                foreach (var ent in CulledTopEntities(dx0, dy0, dx1, dy1))
                {
                    EmitEntityPng(ent, null, 1.0, 7, null, null, _ltScale);
                    renderedPairs++;
                    _tileEnts++;
                }
                long _tileRenderMs = Environment.TickCount64 - _tileRenderT0;
                _rc = null;

                // 无数据瓦片跳过（2026-11-02 用户要求）：空间剔除判定本瓦片 0 个相交实体 ⇒ 0 像素贡献
                // （像素级精确，与剔除安全外扩同口径）→ 不写文件、不进瓦片列表（坐标 JSON 不列出该瓦片）。
                // 空白瓦片不占磁盘、不占编码/写盘时间；透明背景缺瓦片=透明，白底背景由下游按白底处理。
                if (_tileEnts == 0 && _opt.SkipEmptyTiles)
                {
                    emptySkipped++;
                    if (_perfVerboseTile)
                        _log($"   [perf] tile({cx},{cy}) 无数据，跳过（不输出）");
                    continue;
                }

                // OGC WMTS 标准命名：tiles/{z}/{row}/{col}.png。
                //   z = 缩放级别：单级导出取 OutputZoom 四舍五入；多级导出取宿主传入的 0-based 级别索引（zLevel）。
                //   row=cy 自顶向下，col=cx 自左向右；叶子文件名即 col（同一 (z,row) 目录下 col 唯一，无碰撞），与 ImageTile.RelPath 一致。
                int z = zLevel;
                string name = $"{cx}{ext}";
                string fp = Path.Combine(dir, "tiles", z.ToString(), cy.ToString(), name);
                Directory.CreateDirectory(Path.GetDirectoryName(fp));
                SavePngAsync(rc, fp); // 编码写盘交后台并行（与后续瓦片光栅化重叠，失败在 FlushPngSaves 统一上报）
                if (_perfVerboseTile)
                    _log($"   [perf] tile({cx},{cy}) 实体{_tileEnts} 光栅{_tileRenderMs}ms（编码后台并行）");
                tiles.Add(new ImageTile
                {
                    FileName = name,
                    FilePath = fp,
                    TilePxW = tw,
                    TilePxH = th,
                    Px0 = x0,
                    Py0 = y0,
                    Z = z,
                    X = cx,
                    Y = cy,
                });
                seq++;
            }
            // P6.5：瓦片行边界回收（与合并路径条带边界同因）：本行已编码完成的 64MB 画布垃圾及时释放，WS 有界
            GC.Collect();
        }

        LogSkipped("PNG");
        // 等待全部后台编码写盘完成；任一失败 → 整次导出失败（保持原同步失败语义）
        ReportProgress("save", 0.97, _tileDone, _tileTotal, "编码写盘");
        if (!FlushPngSaves())
            return new ImageExportResult { Success = false };
        double skipPct = totalPairs > 0 ? 100.0 * (1 - (double)renderedPairs / totalPairs) : 0;
        ReportProgress("done", 1.0, seq, seq, $"{seq} 张瓦片");
        _log($"✅  PNG 分块输出：{seq} 张瓦片（{cols} 列 × {rows} 行网格，单瓦片 ≤ {tile}×{tile}px，整幅 {fullW}×{fullH} @1:1×{F(zoom)}，OGC WMTS 命名 tiles/{zLevel}/{{row}}/{{col}}.png{(_transparentBg ? "，透明背景" : "")}{(emptySkipped > 0 ? $"，跳过 {emptySkipped} 张无数据空白瓦片（未输出）" : "")}）");
        _log($"   空间剔除：{seq} 瓦片共需 {totalPairs} 次实体渲染，实际 {renderedPairs} 次（剔除 {skipPct:F0}%；编码写盘已后台并行）");
        if (PerfOn)
            _log($"[perf][mem] 瓦片渲染完成：GC存活≈{GC.GetTotalMemory(false) / 1024 / 1024}MB / Insert精灵 {_insertSprites.Count} 个 {_insSpriteMemBytes / 1024 / 1024}MB / 字形精灵 {_glyphSprites.Count} 个 / 点精灵 {_sprites.Count} 个");
        LogPerfSummary();
        return PngResult(fullW, fullH, false, zoom, tiles);
    }

    // P6/P6.5：原分辨率行流合并（不缩小）。网格列与 WritePngTiled 完全一致（同 tile 宽、同空间剔除、同光栅化参数），
    // 再沿高度切成 bandH 高的子条带，行优先（先 (0,cy,ky) 整块、再 (1,cy,ky) 整块……末列 (cols-1,cy,ky)）
    // 喂给 PngNativeMergeWriter → 单张 fullW×fullH 原分辨率 PNG。
    // 内存（P6.5：子条带即时拷贝 + 单固定画布，零画布垃圾、零重分配）：
    //   写器把每个子条带各行即时拷入条带累积缓冲（fullW×bandH×4 ≤ PngMergeBandCapBytes（16MB），首条带一次分配、跨条带复用）
    //   渲染画布 = 单张固定 maxTw×maxTh（≤ tile×tile），全程一次分配、所有子条带复用
    //   （P6.5.1：原按 tw×(sy+sh) 逐子条带重分配，T11 实测 19 次重分配 × ≤64MiB；旧画布虽被
    //     GC 释放，但 LOH 段 WS 不随 GC 回落，WS−Heap 间隙实测振荡至 ~297MB（perf5）→ 固定画布后
    //     画布侧零垃圾，间隙稳定在基线水平）
    //   → 合并内存 = 条带累积缓冲 + 1 张固定渲染画布（≤67MB）+ ≤1MB IDAT，与整幅总高度无关。
    //   （P6 原实现写器持整网格行画布引用至行满，T11 实测 4×64MB=256MB/网格行常驻 → peakWS 1197MB；
    //     P6.5 后 T11 目标 <500MB。）
    // 像素一致（P6.5 修复）：子条带画布 = 瓦片宽 ×（子条带底 − 瓦片顶），渲染原点 = 【瓦片】左上角
    //   （_tileOx/_tileOy = x0/y0，与 WritePngTiled 逐式一致）；写器只取画布 [sy, sy+sh) 行作条带。
    //   必须用瓦片原点而非子条带原点：端点经 PngSx/PngSy(double)→float 舍入，原点取整分解不同
    //   ⇒ float 端点可差 1 ulp ⇒ AA 边缘覆盖量化 ±1 计数（P6 像素一致测试实测 163px 差异，整数缩放亦现）。
    //   瓦片原点下局部坐标与瓦片路径逐位相同 ⇒ 整数/分数缩放均与瓦片拼合逐像素一致。
    /// <summary>P6.5 内存探针（PerfOn 时）：WS/Private/GC堆/已提交/碎片/线程数——定位 WS 与 GC 堆之外的常驻内存。</summary>
    private static string MemProbe()
    {
        var mi = GC.GetGCMemoryInfo();
        var proc = Process.GetCurrentProcess();
        return $"WS={proc.WorkingSet64 / 1024 / 1024}MB Private={proc.PrivateMemorySize64 / 1024 / 1024}MB Heap={mi.HeapSizeBytes / 1024 / 1024}MB Threads={proc.Threads.Count}";
    }

    private ImageExportResult WritePngNativeMerge(string outPath, int fullW, int fullH, double zoom)
    {
        // 瓦片宽：与 WritePngTiled 同一公式（min(PngTileSize, 单侧上限) 且 tile×tile ≤ 总像素上限）。
        // 网格列必须与瓦片路径一致：光栅化 AA 覆盖为 float 量级精度，瓦片网格不同 ⇒ 局部坐标取整路径不同
        // ⇒ 边缘像素覆盖可差 ±1 计数（T11②“合并图=瓦片拼合逐像素一致”要求同网格）。
        int tile = _opt.PngTileSize > 0 ? _opt.PngTileSize : 4096;
        tile = Math.Min(tile, PngMaxSide);
        while (tile > 1 && (long)tile * tile > PngMaxPixels) tile--;
        // 子条带高（P6.5）：条带累积缓冲 fullW×bandH×4 ≤ PngMergeBandCapBytes（16MB），向下取 64 的倍数，
        // 最小 64px、不超过 tile。极宽画布 bandH 自动变小；无 OOM 风险（旧 512MB 网格行硬上限随“写器持画布”语义一并移除）。
        // 预算 16MB（P6.5 门禁标定）：64MB 预算 peakWS 522MB（perf7）、32MB 预算 501.2MB（perf8）、
        // 16MB 预算 502MB（perf9）——峰值对累积缓冲尺寸不敏感（acc 31→15.5MB 仅使峰值 501.2→502），
        // 超出 500MB 的真实来源 = strip 后基线 404MB + 渲染画布 64MB + 首条带一次性预热瞬态≈30-38MB
        // （JIT/字体/字形精灵/Insert 精灵），瞬态与画布共存于首条带窗口 ⇒ perf10：画布分配前预热缓存 +
        // GC.Collect() 把瞬态移出画布窗口（见方法内预热线）。总压缩字节数不变。
        const long PngMergeBandCapBytes = 16L * 1024 * 1024;
        long bandRows = PngMergeBandCapBytes / (Math.Max(1, fullW) * 4L);
        int bandH = (int)Math.Max(64L, bandRows) & ~63;
        if (bandH < 64) bandH = 64;
        if (bandH > tile) bandH = tile;
        int cols = (int)Math.Ceiling(fullW / (double)tile);
        int rows = (int)Math.Ceiling(fullH / (double)tile);

        _skippedCounts.Clear();
        _tiledMode = true; _sprites.Clear(); // 与原分块渲染同一光栅化参数（点精灵缓存跨瓦片复用）
        _glyphSprites.Clear(); _glyphHit = 0; _glyphMiss = 0; // 字形精灵缓存跨瓦片复用
        _insertSprites.Clear(); _insSpriteHit = 0; _insSpriteMiss = 0; _insSpriteDirect = 0; _insSpriteMemBytes = 0;
        _useRealTextWidth = true;
        if (PerfOn) _swCull.Restart();
        BuildCullData(); // 预计算顶层实体世界 AABB（含线宽/点字形安全外扩），供按瓦片空间剔除
        if (PerfOn) _swCull.Stop();
        if (PerfOn)
            _log($"[perf][mem] 剔除后：流式实体 {_streamEnts?.Count ?? 0} / 顶层剔除 {_topEnts?.Count ?? 0} / GC存活≈{GC.GetTotalMemory(false) / 1024 / 1024}MB（未强制回收） / {MemProbe()}");

        using var writer = new PngNativeMergeWriter(outPath, fullW, fullH);
        // P6.5.1：单张固定渲染画布（最大瓦片宽 × 最大瓦片高），全程一次分配、所有子条带复用。
        // 原实现按 tw×(sy+sh) 逐子条带重分配（T11 共 19 次、单张 ≤64MiB）：旧画布虽被 GC 回收，
        // 但 LOH 段的 WS 不随 GC 回落，WS−Heap 间隙实测振荡至 ~297MB（perf5 探针）→ 固定画布后零重分配。
        // 像素一致不受影响：渲染原点仍为瓦片左上角（Option D：_tileOx/_tileOy = x0/y0），
        // 写器仍只取画布 [0,tw)×[sy,sy+sh) 行区间；画布多出的行列仅被 ClearPngBg 清背景、不被读取，
        // 子条带区覆盖度计算与原小画布逐位相同。
        int maxTw = Math.Min(tile, fullW);
        int maxTh = Math.Min(tile, fullH);
        var pooled = new RasterCanvas(maxTw, maxTh);
        int pw = maxTw, ph = maxTh;
        long renderedPairs = 0, totalPairs = 0;
        long subStripCount = 0, bandCount = 0;
        int _mergeTotal = 0;
        for (int _ry = 0; _ry < rows; _ry++)
        {
            int _thy = Math.Min(tile, fullH - _ry * tile);
            _mergeTotal += (int)((_thy + bandH - 1) / (long)bandH) * cols;
        }
        for (int cy = 0; cy < rows && !writer.Failed; cy++)
        {
            int y0 = cy * tile;
            int th = Math.Min(tile, fullH - y0);
            int bands = (th + bandH - 1) / bandH;
            for (int ky = 0; ky < bands && !writer.Failed; ky++)
            {
                int sy = ky * bandH;                    // 子条带在瓦片内偏移（像素）
                int sh = Math.Min(bandH, th - sy);      // 子条带高（末条带可更短）
                for (int cx = 0; cx < cols && !writer.Failed; cx++)
                {
                    int x0 = cx * tile;
                    int tw = Math.Min(tile, fullW - x0);
                    if (tw < 1 || sh < 1) continue;

                    // 渲染画布：单张固定 maxTw×maxTh（见方法头 P6.5.1 注释）；
                    // 本子条带 = 画布列 [0, tw) × 行 [sy, sy+sh)（原点 = 瓦片左上角，Option D）
                    var rc = pooled;
                    if (PerfOn && subStripCount < 8)
                        _log($"   [diag] sub{subStripCount + 1}({cx},{cy},{ky}) pre: GC≈{GC.GetTotalMemory(false) / 1024 / 1024}MB / {MemProbe()} / ins={_insertSprites.Count}:{_insSpriteMemBytes / 1024 / 1024}MB glyph={_glyphSprites.Count} pt={_sprites.Count}");
                    ClearPngBg(rc);
                    _rc = rc;
                    // 整幅统一比例 = 恰好 zoom（与 WritePngTiled/WritePngSingle 严格一致）；
                    // 原点 = 瓦片左上角（与 WritePngTiled 逐式一致）→ 局部 float 坐标与瓦片路径逐位相同 ⇒ 像素一致。
                    _pngKx = zoom; _pngKy = zoom;
                    _tileOx = x0; _tileOy = y0;

                    // 空间剔除：与 WritePngTiled 逐式一致（子条带像素 → 数据坐标矩形，Y 翻转）
                    double dx0 = _minX - _margin + x0 / (double)_pngKx;
                    double dx1 = _minX - _margin + (x0 + tw) / (double)_pngKx;
                    double dy1 = _maxY + _margin - (y0 + sy) / (double)_pngKy;
                    double dy0 = _maxY + _margin - (y0 + sy + sh) / (double)_pngKy;
                    totalPairs += _topEnts != null ? _topEnts.Count : 0;
                    foreach (var ent in CulledTopEntities(dx0, dy0, dx1, dy1))
                    {
                        EmitEntityPng(ent, null, 1.0, 7, null, null, _ltScale);
                        renderedPairs++;
                    }
                    _rc = null;
                    if (PerfOn && subStripCount < 8)
                        _log($"   [diag] sub{subStripCount + 1}({cx},{cy},{ky}) post-render: GC≈{GC.GetTotalMemory(false) / 1024 / 1024}MB / {MemProbe()} / ins={_insertSprites.Count}:{_insSpriteMemBytes / 1024 / 1024}MB glyph={_glyphSprites.Count} pt={_sprites.Count}");

                    writer.WriteTile(rc, sy, tw, sh); // 即时拷贝画布 [0,tw)×[sy,sy+sh) 入条带累积缓冲；rc 立即可复用
                    if (PerfOn && subStripCount < 8)
                        _log($"   [diag] sub{subStripCount + 1}({cx},{cy},{ky}) post-write: GC≈{GC.GetTotalMemory(false) / 1024 / 1024}MB / {MemProbe()}");
                    subStripCount++;
                    ReportProgress("tile", 0.10 + 0.87 * subStripCount / (double)Math.Max(1, _mergeTotal), (int)subStripCount, _mergeTotal, $"strip({cx},{cy},{ky}) 行流并入");
                    if (cx == cols - 1)
                    {
                        bandCount++;
                        // P6.5：条带边界回收——渲染临时对象（每实体路径/点数组等）在条带内累积为垃圾，
                        // workstation GC 不会自行触发 gen2 → WS 持续爬升（T11 实测 565→678MB/18 条带）。
                        // 每带一次 gen2（~50-200ms）使 WS 有界 ≈ 存活集 + 条带累积缓冲（门禁 peakWS<500MB）。
                        GC.Collect();
                        if (PerfOn)
                            _log($"   [perf][mem] 条带 {bandCount} ({cx},{cy},{ky}) 完成：GC存活≈{GC.GetTotalMemory(false) / 1024 / 1024}MB / 渲染画布 {pw}×{ph} / {MemProbe()}");
                    }
                    if (_perfVerboseTile)
                        _log($"   [perf] 合并子条带({cx},{cy},{ky}) 实体已并入整幅行流");
                }
            }
        }

        bool ok = writer.Finish();
        LogSkipped("PNG");
        if (!ok)
        {
            try { File.Delete(outPath); } catch { /* 半成品删除失败仅告警 */ }
            _log($"❌  PNG 原分辨率行流合并失败：{outPath}");
            return new ImageExportResult { Success = false };
        }
        double skipPct = totalPairs > 0 ? 100.0 * (1 - (double)renderedPairs / totalPairs) : 0;
        ReportProgress("done", 1.0, (int)subStripCount, _mergeTotal, outPath);
        _log($"✅  PNG 原分辨率流式合并（不缩小）：{outPath}（整幅 {fullW}×{fullH} @1:1×{F(zoom)}，{cols}×{rows} 瓦片网格 × 子条带 {bandH}px 高共 {bandCount} 条带 {subStripCount} 块行流并入，合并内存≈条带累积缓冲 {(long)fullW * bandH * 4 / 1024 / 1024}MB + 渲染画布(瓦片宽×至子条带底，单张复用)（与整幅总高无关）{(_transparentBg ? "，透明背景" : "")}）");
        _log($"   空间剔除：{subStripCount} 子条带共需 {totalPairs} 次实体渲染，实际 {renderedPairs} 次（剔除 {skipPct:F0}%）");
        if (PerfOn)
            _log($"[perf][mem] 渲染完成：GC存活≈{GC.GetTotalMemory(false) / 1024 / 1024}MB / Insert精灵 {_insertSprites.Count} 个 {_insSpriteMemBytes / 1024 / 1024}MB / 字形精灵 {_glyphSprites.Count} 个 / 点精灵 {_sprites.Count} 个 / 渲染画布 {pw}×{ph}");
        LogPerfSummary();
        // 合并图 = 整幅原分辨率单图：MergedToSingle=true，无瓦片列表（宿主坐标 JSON 走单画布分支）
        return PngResult(fullW, fullH, true, zoom, System.Array.Empty<ImageTile>());
    }

    // 清背景（黑/白/透明）
    private void ClearPngBg(RasterCanvas rc)
    {
        if (_transparentBg) rc.Clear(0, 0, 0, 0);
        else rc.Clear(_whiteBg ? (byte)255 : (byte)0, _whiteBg ? (byte)255 : (byte)0, _whiteBg ? (byte)255 : (byte)0, 255);
    }

    // 组装 PNG 导出结果（整幅画布包围盒 + 瓦片列表 + 合并标志 + 实际统一比例 k）
    private ImageExportResult PngResult(int fullW, int fullH, bool merged, double k, System.Collections.Generic.IEnumerable<ImageTile> tiles)
    {
        return new ImageExportResult
        {
            Success = true,
            CanvasPixelsW = fullW,
            CanvasPixelsH = fullH,
            PixelsPerUnit = k,
            DataX0 = _minX - _margin,
            DataX1 = _maxX + _margin,
            DataY0 = _minY - _margin,
            DataY1 = _maxY + _margin,
            MergedToSingle = merged,
            Tiles = tiles as System.Collections.Generic.IReadOnlyList<ImageTile> ?? tiles.ToList(),
        };
    }

    #endregion

    #region ===== 实体分发（与 EmitEntitySvg 逐 case 对齐）=====

    // 发射单个实体（含 Insert 展开）。参数语义与 EmitEntitySvg 完全一致。
    private void EmitEntityPng(Entity ent, Transform2D tr, double scaleMul, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul, short? byLayerOverride = null)
    {
        if (PerfOn)
        {
            if (_perfDepth == 0) { _perfStartTick = Environment.TickCount64; _perfTypeStartType = ent.GetType().Name; _perfRenderCount++; }
            _perfDepth++;
        }
        short layerAci;
        if (byLayerOverride.HasValue) layerAci = byLayerOverride.Value;
        else { layerAci = 7; try { layerAci = LayerAciIndex(ent.Layer); } catch { } }
        double[] dash = ResolveDash(ent, byBlockDash, ltMul);
        LtpPattern pat = ResolvePattern(ent, byBlockPat, ltMul); // 复杂线型（含 SHX 段）→ 沿路径真实渲染

        switch (ent)
        {
            case Insert ins:
                EmitInsertPng(ins, tr, byBlockAci, dash, pat, ltMul);
                break;

            case Line ln:
                {
                    var (x1, y1) = tr == null ? (ln.StartPoint.X, ln.StartPoint.Y) : tr.Apply(ln.StartPoint.X, ln.StartPoint.Y);
                    var (x2, y2) = tr == null ? (ln.EndPoint.X, ln.EndPoint.Y) : tr.Apply(ln.EndPoint.X, ln.EndPoint.Y);
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (!TryEmitComplexPng(ent, tr, pat, col, w))
                        StrokePts(new List<(double X, double Y)> { (x1, y1), (x2, y2) }, false, col, w, dash);
                }
                break;

            case LwPolyline pl:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (PerfOn)
                    {
                        long _t0 = Environment.TickCount64;
                        if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) { _lwComplexMs += Environment.TickCount64 - _t0; _lwComplexCnt++; break; }
                        _lwComplexMs += Environment.TickCount64 - _t0;
                    }
                    else if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    // 普通描边路径采样世界折线点（含 bulge 弧段）；变宽路径走 EmitTaperedPng（自有 bulge 逻辑）
                    long _s0 = PerfOn ? Environment.TickCount64 : 0;
                    var pts = LwPolylineSamplePoints(pl, tr);
                    if (PerfOn)
                    {
                        _lwSampleMs += Environment.TickCount64 - _s0;
                        _lwSamplePts += pts.Count;
                        if (pts.Count > _lwMaxSegs) _lwMaxSegs = pts.Count;
                    }
                    // 变宽判断看**任意**顶点 StartWidth(40) 或 ConstantWidth(43)（首顶点窄端 sw=0 的纺锤形不能漏判）；
                    // EndWidth(41) 不单独触发（闭合圆环全 sw=0/ew=常数 应走描边+bulge 弧，否则变宽路径退化成 0 宽消失）。
                    // 常数宽(43)：全顶点等宽归一形式 → 物化为逐顶点
                    // [PATCH] Option B：仅当 ConstantWidthSpecified（源文件确有 43）才物化/走变宽
                    // （与 SVG/PDF 同口径；GDB2CAD 合成的 ConstantWidth 未设 Specified，读回侧无 43）。
                    if (pl.ConstantWidthSpecified && pl.ConstantWidth > 0 && !pl.Vertices.Any(v => v.StartWidth > 0))
                        foreach (var v in pl.Vertices) { v.StartWidth = pl.ConstantWidth; v.EndWidth = pl.ConstantWidth; }
                    // 变宽：任意顶点 起宽(40)/终宽(41) 非零 或 常数宽(43) 非零 → 走填充变宽路径；逐顶点有效宽度
                    // 由 TaperedVertexWidths 计算（正确处理"每节起点/终点线宽不等"，与 SVG/PDF 一致）。
                    bool hasTaper = pl.Vertices.Any(v => v.StartWidth > 0 || v.EndWidth > 0) || (pl.ConstantWidthSpecified && pl.ConstantWidth > 0);
                    if (hasTaper && pts.Count >= 2)
                    {
                        if (PerfOn)
                        {
                            long _t1 = Environment.TickCount64;
                            EmitTaperedPng(pl, tr, col, scaleMul);
                            _lwTaperMs += Environment.TickCount64 - _t1; _lwTaperCnt++;
                        }
                        else EmitTaperedPng(pl, tr, col, scaleMul);
                        break;
                    }
                    if (PerfOn)
                    {
                        long _t2 = Environment.TickCount64;
                        StrokePts(pts, pl.IsClosed, col, StrokeWidth(ent, scaleMul), dash);
                        _lwStrokeMs += Environment.TickCount64 - _t2; _lwStrokeCnt++;
                    }
                    else StrokePts(pts, pl.IsClosed, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Polyline3D p3:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = new List<(double X, double Y)>(p3.Vertices.Count);
                    foreach (var v in p3.Vertices)
                    {
                        if (!IsDisplayVertex(v)) continue; // 跳过样条框架控制点（不显示）
                        if (tr == null) pts.Add((v.Location.X, v.Location.Y));
                        else { var a = tr.Apply(v.Location.X, v.Location.Y); pts.Add((a.Item1, a.Item2)); }
                    }
                    if (pts.Count >= 2) StrokePts(pts, p3.IsClosed, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Polyline2D p2d:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = new List<(double X, double Y)>(p2d.Vertices.Count);
                    foreach (var v in p2d.Vertices)
                    {
                        if (!IsDisplayVertex(v)) continue; // 跳过样条框架控制点（不显示）
                        if (tr == null) pts.Add((v.Location.X, v.Location.Y));
                        else { var a = tr.Apply(v.Location.X, v.Location.Y); pts.Add((a.Item1, a.Item2)); }
                    }
                    if (pts.Count >= 2) StrokePts(pts, p2d.IsClosed, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Mesh mesh:
                {
                    // 3D 网格：等轴测 3D 着色实体渲染（Bug4 DOME）
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    var isoRes = MeshIsoRender(mesh);
                    if (isoRes.Faces.Count > 0)
                    {
                        // 先画面（远→近，画家算法）
                        for (int fi = 0; fi < isoRes.Faces.Count; fi++)
                        {
                            int g = isoRes.Grays[fi];
                            var contour = new List<RasterCanvas.P>(isoRes.Faces[fi].Count);
                            foreach (var p in isoRes.Faces[fi])
                                contour.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
                            _rc.Fill(new List<List<RasterCanvas.P>> { contour }, false, new RasterCanvas.Rgb((byte)g, (byte)g, (byte)g));
                        }
                        // 可见边实线
                        foreach (var ed in isoRes.Vis)
                            StrokePts(new List<(double X, double Y)> { (ed.X1, ed.Y1), (ed.X2, ed.Y2) }, false, "#000000", Math.Max(0.4, w), null);
                        // 隐藏边虚线
                        double[] hidDash = { 1.2, 0.8 };
                        foreach (var ed in isoRes.Hid)
                            StrokePts(new List<(double X, double Y)> { (ed.X1, ed.Y1), (ed.X2, ed.Y2) }, false, "#666666", Math.Max(0.25, w * 0.6), hidDash);
                    }
                    else if (mesh.Edges != null && mesh.Vertices != null && mesh.Vertices.Count > 0)
                        foreach (var e in mesh.Edges)
                        {
                            if (e.Start < 0 || e.End < 0 || e.Start >= mesh.Vertices.Count || e.End >= mesh.Vertices.Count) continue;
                            var va = mesh.Vertices[e.Start];
                            var vb = mesh.Vertices[e.End];
                            if (tr == null)
                                StrokePts(new List<(double X, double Y)> { (va.X, va.Y), (vb.X, vb.Y) }, false, col, w, null);
                            else
                            {
                                var a = tr.Apply(va.X, va.Y); var b = tr.Apply(vb.X, vb.Y);
                                StrokePts(new List<(double X, double Y)> { (a.Item1, a.Item2), (b.Item1, b.Item2) }, false, col, w, null);
                            }
                        }
                }
                break;

            case PolyfaceMesh pf:
                {
                    // 旧式 3D 多面体网格：按 Face 索引连线成线框
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexPng(ent, tr, pat, col, w)) break;
                    foreach (var (a, b) in PolyfaceMeshWire(pf, tr))
                        StrokePts(new List<(double X, double Y)> { (a.AX, a.AY), (b.BX, b.BY) }, false, col, w, null);
                }
                break;

            case PolygonMesh pm:
                {
                    // M×N 多边形网格：网格线框
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexPng(ent, tr, pat, col, w)) break;
                    foreach (var (a, b) in PolygonMeshWire(pm, tr))
                        StrokePts(new List<(double X, double Y)> { (a.AX, a.AY), (b.BX, b.BY) }, false, col, w, null);
                }
                break;

            case Wipeout wo:
                {
                    // WIPEOUT 属性渲染（三引擎共享 WipeoutFillHex/WipeoutFrame）：填充=42(0/缺省=背景擦除,透明底不画)；
                    // 边框=文件全局 DisplayImageFrame(WIPEOUTVARIABLES 组码70)=1 或 41>0，色=图元/图层色（有图/无图均适用）。
                    // 边框为真实颜色（图元/图层色），透明底也绘制；仅填充(背景擦除)在透明底跳过。
                    if (_rc != null)
                    {
                        var wpoly = WipeoutWorldPoly(wo, tr);
                        if (wpoly.Count >= 3)
                        {
                            if (!_transparentBg)
                            {
                                string fill = WipeoutFillHex(wo.FillColor, _whiteBg);
                                var pp = new List<RasterCanvas.P>(wpoly.Count);
                                foreach (var p in wpoly) pp.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
                                _rc.Fill(new List<List<RasterCanvas.P>> { pp }, false, HexToRgb(fill));
                            }
                            var frame = WipeoutFrame(wo, layerAci, byBlockAci, scaleMul);
                            if (frame.Show)
                                StrokePts(wpoly, true, frame.Col, Math.Max(0.1, frame.W), null);
                        }
                    }
                }
                break;

            case RasterImage ri:
                EmitRasterImagePng(ri, tr);
                break;

            case Ole2Frame olef4:
                EmitOle2FramePng(olef4, tr);
                break;

            case Arc a: // 必须先于 Circle（fork 继承关系）
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (a.Center.X, a.Center.Y) : tr.Apply(a.Center.X, a.Center.Y);
                    double r = a.Radius * (tr?.Scale ?? 1.0);
                    if (r > 0)
                    {
                        double s = a.StartAngle * 180.0 / Math.PI, e = a.EndAngle * 180.0 / Math.PI;
                        double span = e - s;
                        while (span < 0) span += 360.0;
                        while (span >= 360.0) span -= 360.0;
                        if (span < 1e-6) span = 360.0;
                        int seg = Math.Max(8, (int)Math.Ceiling(span / 2.0));
                        var pts = new List<(double X, double Y)>(seg + 1);
                        for (int i = 0; i <= seg; i++)
                        {
                            double ang = (s + span * i / seg) * Math.PI / 180.0;
                            pts.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
                        }
                        StrokePts(pts, false, col, StrokeWidth(ent, scaleMul), dash);
                    }
                }
                break;

            case Circle c:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (c.Center.X, c.Center.Y) : tr.Apply(c.Center.X, c.Center.Y);
                    double r = c.Radius * (tr?.Scale ?? 1.0);
                    if (r > 0)
                    {
                        int m = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * r / 0.5), 64, 100000);
                        var pts = new List<(double X, double Y)>(m + 1);
                        for (int i = 0; i <= m; i++)
                        {
                            double ang = 2 * Math.PI * i / m;
                            pts.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
                        }
                        StrokePts(pts, true, col, StrokeWidth(ent, scaleMul), dash);
                    }
                }
                break;

            case Ellipse el:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (el.Center.X, el.Center.Y) : tr.Apply(el.Center.X, el.Center.Y);
                    // fork MajorAxis/MinorAxis 为主/短轴全长(2a/2b)，采样点用半轴 → 各除以 2
                    double rx = el.MajorAxis / 2.0 * (tr?.Scale ?? 1.0);
                    double ry = el.MinorAxis / 2.0 * (tr?.Scale ?? 1.0);
                    if (rx > 0 && ry > 0)
                    {
                        double rot = el.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0);
                        double ca = Math.Cos(rot * Math.PI / 180.0), sa = Math.Sin(rot * Math.PI / 180.0);
                        int m = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * Math.Max(rx, ry) / 0.5), 64, 100000);
                        var pts = new List<(double X, double Y)>(m + 1);
                        for (int i = 0; i <= m; i++)
                        {
                            double th = 2 * Math.PI * i / m;
                            double ex = rx * Math.Cos(th), ey = ry * Math.Sin(th);
                            pts.Add((cx + ex * ca - ey * sa, cy + ex * sa + ey * ca));
                        }
                        StrokePts(pts, true, col, StrokeWidth(ent, scaleMul), dash);
                    }
                }
                break;

            case Point pt:
                {
                    // 2026-10-26 用户裁定：$PDSIZE=0 不再抑制点——中心点恒按基础 1 像素绘制（见 EmitPointPng），
                    // 外框/臂按设置比例（PointGlyphRadius）绘制；PDMODE=1 且无外框 → 不绘制。
                    var (cx, cy) = tr == null ? (pt.Location.X, pt.Location.Y) : tr.Apply(pt.Location.X, pt.Location.Y);
                    double lw = StrokeWidth(ent, scaleMul);
                    var col = HexToRgb(ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                    // 分块 + 开启点精灵缓存时走离屏精灵 blit；否则逐瓦片直接光栅化（像素级精确）。
                    bool usePtSprite = _tiledMode && _opt.PngPointSpriteCache;
                    if (usePtSprite)
                    {
                        // 点字形以中心为原点、位置无关，仅由 (PDMODE, 半径, 线宽, 颜色, 背景) 决定
                        // → 相同点符号跨瓦片只光栅化 1 次，其余瓦片整数平移 blit（大量点块场景收益最大）。
                        // ⚠️ 亚像素相位量化见 ImageConfiguration.PngPointSpriteCache（1px 细线可能位移 1 像素）。
                        double k = PngK;
                        double r = PointGlyphRadius();
                        double wPx = Math.Max(lw * k, 1.0);
                        double ext = Math.Max(2.0 * r, 1.0 / k) + 0.5 * wPx / k + 1.0 / k; // 加号/X 半伸出 2r 或 1px 点径(取大) + 线宽半宽 + 1px 余量（数据单位）
                        string key = "P" + _pdMode + "|" + r.ToString("R") + "|" + wPx.ToString("R")
                            + "|" + col.R + "," + col.G + "," + col.B
                            + "|" + (_transparentBg ? 1 : 0) + (_whiteBg ? 1 : 0);
                        TryEmitSprite(key, cx, cy, ext, ext, ext, ext, () => EmitPointPng(cx, cy, col, lw));
                    }
                    else
                        EmitPointPng(cx, cy, col, lw);
                }
                break;

            case TextEntity t:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    // SHX 样式：真实 SHX 笔迹路径（与 AutoCAD 一致字宽/字形）；非 SHX/字体缺失 → 回退原 TTF 路径
                    var shx = TryBuildShxTextWorld(t, tr, scaleMul);
                    if (shx != null)
                    {
                        if (PerfOn) _perfTextChars += (t.Value ?? "").Length;   // SHX 文字字符（原计数器从未累加）
                        var pb = new PolyBuilder();
                        for (int k = 0; k < shx.Value.Type.Count; k++)
                        {
                            var p = shx.Value.Pts[k];
                            switch (shx.Value.Type[k])
                            {
                                case 0: pb.MoveTo((float)PngSx(p[0]), (float)PngSy(p[1])); break;
                                case 1: pb.LineTo((float)PngSx(p[0]), (float)PngSy(p[1])); break;
                                case 2: pb.QuadTo((float)PngSx(p[0]), (float)PngSy(p[1]), (float)PngSx(p[2]), (float)PngSy(p[3])); break;
                                case 3: pb.CubicTo((float)PngSx(p[0]), (float)PngSy(p[1]), (float)PngSx(p[2]), (float)PngSy(p[3]), (float)PngSx(p[4]), (float)PngSy(p[5])); break;
                            }
                        }
                        var penCol = HexToRgb(col);
                        float pw = (float)Math.Max(0.0, shx.Value.PenWidth * PngK);
                        foreach (var pl in pb.ToPolylines(false))
                            if (pl.Count >= 2) _rc.StrokePolyline(pl, false, pw, penCol);
                        break;
                    }
                    // TTF 样式：纯托管真字形轮廓填充（TrueTypeFont 替代 SkiaSystemFont）
                    string family = ResolveFontFamily(GetStyleFont(t.Style));
                    double? wReal = _useRealTextWidth ? PngTextWidth(t, family) : null;
                    var (bx, by, brot, bscale) = TextBaseline(t, tr, wReal);
                    double fs = t.Height * scaleMul * bscale * TtfCapScale(family);
                    DrawTextPng(bx, by, t.Value ?? "", Math.Max(fs, 0.5), col, -brot, family, t.WidthFactor);
                }
                break;

            case MText mt:
                EmitMTextPng(mt, tr, scaleMul, layerAci, byBlockAci, GetStyleFont(mt.Style));
                break;

            case Solid so:
                EmitQuadFillPng(SolidQuadPts(so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner, tr),
                    ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                break;

            case Face3D fd:
                EmitQuadFillPng(QuadPts(fd.FirstCorner, fd.SecondCorner, fd.ThirdCorner, fd.FourthCorner, tr),
                    ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                break;

            case Hatch ht:
                EmitHatchPng(ht, tr, scaleMul, layerAci, byBlockAci);
                break;

            case Spline sp:
                {
                    string col = ColorOf(EntityAciIndex(sp), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(sp, scaleMul))) break;
                    var sp2 = SplineSample(sp, tr, 16);
                    if (sp2.Count < 2) { RecordSkip("SPLINE(空)"); break; }
                    var pts = new List<(double X, double Y)>(sp2.Count);
                    foreach (var p in sp2) pts.Add((p.X, p.Y));
                    StrokePts(pts, false, col, StrokeWidth(sp, scaleMul), dash);
                }
                break;

            case XLine xl:
                {
                    var (px, py) = tr == null ? (xl.FirstPoint.X, xl.FirstPoint.Y) : tr.Apply(xl.FirstPoint.X, xl.FirstPoint.Y);
                    var (dx, dy) = TransformDirection(xl.Direction.X, xl.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, true, out var x1, out var y1, out var x2, out var y2))
                        StrokePts(new List<(double X, double Y)> { (x1, y1), (x2, y2) }, false,
                            ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Ray ray:
                {
                    var (px, py) = tr == null ? (ray.StartPoint.X, ray.StartPoint.Y) : tr.Apply(ray.StartPoint.X, ray.StartPoint.Y);
                    var (dx, dy) = TransformDirection(ray.Direction.X, ray.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, false, out var x1, out var y1, out var x2, out var y2))
                        StrokePts(new List<(double X, double Y)> { (x1, y1), (x2, y2) }, false,
                            ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case MLine ml:
                {
                    // 多线：按 DXF 41 参数化提取各子线的实际画线段（含画/断模式与开口长短），
                    // 逐段描边细线。MLINE 正确语义=一组平行细线（AutoCAD 2D 渲染永不填充）。
                    double w = StrokeWidth(ent, scaleMul);
                    foreach (var el in MLineDrawnElements(ml, tr))
                    {
                        string col = ColorOf(el.Aci, layerAci, byBlockAci);
                        foreach (var s in el.Segs)
                        {
                            var pts = new List<(double X, double Y)> { (s.X1, s.Y1), (s.X2, s.Y2) };
                            // 画/断由 41 参数决定；线型 dash 不再叠加（避免小段被 dash 切碎）
                            StrokePts(pts, false, col, w, null);
                        }
                    }
                }
                break;

            case Leader ld:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    // DXF 组码71：缺省=有箭头，0=无箭头；XData 341 指向空块（_None）= 显式无箭头
                    var (arrowBr, asz, noArrow) = LeaderArrowResolve(ld, _doc, Math.Max(w * 6.0, 4.0 * (tr?.Scale ?? 1.0)));
                    bool arrow = ld.ArrowHeadEnabled && !noArrow;
                    // 世界坐标显示路径：直线引线含钩线合成（角点 + 水平着陆段，端点按标注控制延长，见 LeaderDisplayPath）；
                    // 样条引线返回 null（下方 LeaderSplineFlatten 处理，末段切向强制水平着陆）。
                    var worldPath = LeaderDisplayPath(ld, _doc, asz);
                    var pts = new List<(double X, double Y)>();
                    if (worldPath != null)
                    {
                        foreach (var p in worldPath) pts.Add(tr == null ? p : tr.Apply(p.X, p.Y));
                    }
                    else if (ld.Vertices != null)
                    {
                        foreach (var v in ld.Vertices)
                        {
                            var p = tr == null ? (v.X, v.Y) : tr.Apply(v.X, v.Y);
                            pts.Add(p);
                        }
                    }
                    if (pts.Count >= 2)
                    {
                        var trA = arrow ? LeaderArrowTransform(pts, asz) : null;
                        // 箭头几何先算得底边距离 trim，再截引线起点：线停于箭头底边（LeaderTrimDistance），
                        // 空心箭头不再有线穿入内部、块箭头尖端不再有线伸入块内（AutoCAD 行为）。
                        // 刻线 _ArchTick/_Oblique 斜线过尖端 → trim≈0（不截断，线接斜线中点=尖端）。
                        List<List<(double X, double Y)>> aStrokes = null, aFills = null;
                        List<(double X, double Y)> aTri = null;
                        double trim = 0;
                        if (arrow && trA != null)
                        {
                            if (arrowBr != null && !IsClosedFilledArrow(arrowBr))
                            {
                                aStrokes = new List<List<(double X, double Y)>>();
                                aFills = new List<List<(double X, double Y)>>();
                                LeaderArrowGeom(arrowBr, trA, aStrokes, aFills);
                                trim = LeaderTrimDistance(pts, aStrokes, aFills);
                            }
                            else
                            {
                                var tip = trA.Apply(0, 0);
                                var c1 = trA.Apply(-1.0, 0.16666667);
                                var c2 = trA.Apply(-1.0, -0.16666667);
                                aTri = new List<(double X, double Y)> { tip, c1, c2 };
                                trim = LeaderTrimDistance(pts, null, new List<List<(double X, double Y)>> { aTri });
                            }
                        }
                        double firstSeg = Math.Sqrt(
                            (pts[1].X - pts[0].X) * (pts[1].X - pts[0].X) +
                            (pts[1].Y - pts[0].Y) * (pts[1].Y - pts[0].Y));
                        if (trim > firstSeg) trim = firstSeg;
                        // 样条引线（组码72=1）：LeaderSplineFlatten 平滑曲线（末段强制水平着陆，与 GT 一致）；
                        // 直线引线用 LeaderDisplayPath 合成的显示路径（含钩线/水平着陆段）。
                        var lp = (ld.PathType == LeaderPathType.Spline)
                            ? LeaderSplineFlatten(pts, 24)
                            : pts;
                        StrokePts(TrimPolylineStart(lp, trim), false, col, w, dash);
                        // 箭头（无箭头 71=0 / XData 空块时不画）：
                        //  · 块箭头（非 _Closed）→ 按块几何忠实渲染（空心描边/常宽闭合填充，_ClosedBlank 空心、_Dot 实心圆点等）。
                        //  · _Closed 块或无块（系统默认闭合箭头）→ 实心三角，按解析尺寸 asz（trA 已缩放）。
                        if (arrow && trA != null)
                        {
                            if (aStrokes != null)
                            {
                                foreach (var s in aStrokes) StrokePts(s, false, col, w, dash);
                                foreach (var f in aFills) EmitQuadFillPng(f, col);
                            }
                            else if (aTri != null)
                            {
                                EmitQuadFillPng(aTri, col);
                            }
                        }
                    }
                }
                break;

            case Dimension dim:
                {
                    // 标注：递归渲染 dim.Block 内实体（尺寸线/箭头/文字/弧/Insert 均为世界坐标）。
                    // 跳过 Defpoints 图层的参考点（AutoCAD 不显示）。
                    if (dim.Block != null && dim.Block.Entities.Count > 0)
                    {
                        short dimAci = EffectiveAci(ent, byBlockAci);
                        short dimLayerAci = 7;
                        try { dimLayerAci = LayerAciIndex(dim.Layer); } catch { }
                        foreach (var be in BlockEntitiesInFileOrder(dim.Block))
                        {
                            if (be is Point)
                            {
                                try { if (be.Layer != null && be.Layer.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) continue; } catch { }
                            }
                            EmitEntityPng(be, null, 1.0, dimAci, null, null, ltMul, dimLayerAci);
                        }
                    }
                    else
                        RecordSkip(ent.GetType().Name);
                }
                break;

            case Shape shp:
                {
                    // SHX 形状实体：用 shape 字体库真实字形笔迹渲染（PolyBuilder 曲线 → 画布描边）
                    EmitShapeEntityPng(shp, tr,
                        ColorOf(EntityAciIndex(shp), layerAci, byBlockAci),
                        StrokeWidth(shp, scaleMul));
                }
                break;

            default:
                RecordSkip(ent.GetType().Name);
                break;
        }
        if (PerfOn)
        {
            if (--_perfDepth == 0)
            {
                long us = Environment.TickCount64 - _perfStartTick;
                _perfTypeUs.TryGetValue(_perfTypeStartType, out long acc);
                _perfTypeUs[_perfTypeStartType] = acc + us;
                // 从当前 _rc 增量读取 SDF/ScanFill 分流计数并清零（跨瓦片/离屏 _rc 切换时安全）
                if (_rc != null)
                {
                    _sdfCount += _rc.SdfCount; _sdfMs += _rc.SdfMs; _rc.SdfCount = 0; _rc.SdfMs = 0;
                    _scanCount += _rc.ScanCount; _scanMs += _rc.ScanMs; _rc.ScanCount = 0; _rc.ScanMs = 0;
                    _sdfSingleCount += _rc.SdfSingleCount; _sdfMultiCount += _rc.SdfMultiCount;
                    _sdfSingleMs += _rc.SdfSingleMs; _sdfMultiMs += _rc.SdfMultiMs;
                    _sdfPixelIters += _rc.SdfPixelIters; _sdfBlendCount += _rc.SdfBlendCount;
                    _sdfBboxArea += _rc.SdfBboxArea;
                    _rc.SdfSingleCount = _rc.SdfMultiCount = _rc.SdfSingleMs = _rc.SdfMultiMs = _rc.SdfPixelIters = _rc.SdfBlendCount = _rc.SdfBboxArea = 0;
                }
            }
        }
    }

    // PNG 光栅图片（RasterImage）：外部图片解码为 RGBA 后，按 CAD 四边形（a0=左下、a10=右下、a01=左上）
    // 仿射贴图绘制到软件光栅化缓冲。JPEG 走新增的基线解码器，PNG 走既有解码器；任一失败降级为灰色矩形。
    private void EmitRasterImagePng(RasterImage ri, Transform2D tr)
    {
        if (_rc == null) return;
        var ip = ri.InsertPoint;
        double W = ri.Size.X, H = ri.Size.Y;
        var u = ri.UVector; var v = ri.VVector;
        (double X, double Y) P(double x, double y) => tr == null ? (x, y) : tr.Apply(x, y);
        var a0 = P(ip.X, ip.Y);
        var a10 = P(ip.X + u.X * W, ip.Y + u.Y * W);
        var a01 = P(ip.X + v.X * H, ip.Y + v.Y * H);
        double p0x = PngSx(a0.X), p0y = PngSy(a0.Y);
        double p10x = PngSx(a10.X), p10y = PngSy(a10.Y);
        double p01x = PngSx(a01.X), p01y = PngSy(a01.Y);
        string path = ResolveAssetPath(ri.Definition?.FileName);
        if (string.IsNullOrEmpty(path))
        {
            PngImageRectFallback(p0x, p0y, p10x, p10y, p01x, p01y, 136, 136, 136);
            RecordSkip("IMAGE(文件缺失)");
            _log("⚠️  IMAGE 图片文件缺失，降级为边框: " + (ri.Definition?.FileName ?? "?"));
            return;
        }
        byte[] bytes = File.ReadAllBytes(path);
        int iw = 0, ih = 0; byte[] pix = null;
        try { var d = RasterHelpers.DecodeToRgba(bytes); iw = d.width; ih = d.height; pix = d.rgba; }
        catch (Exception ex)
        {
            _log("⚠️  IMAGE 解码失败，降级为边框: " + path + " (" + ex.Message + ")");
        }
        if (iw <= 0 || ih <= 0 || pix == null)
        {
            PngImageRectFallback(p0x, p0y, p10x, p10y, p01x, p01y, 136, 136, 136);
            RecordSkip("IMAGE(无法解码)");
            return;
        }
        _rc.DrawImageParallelogram(iw, ih, pix, p0x, p0y, p10x, p10y, p01x, p01y);
    }

    // PNG 图片降级：把 CAD 四边形（画布像素）填充为灰色矩形
    // PNG OLE2 嵌入对象（Ole2Frame，组码310 内嵌 OLE CFB 容器）：
    // 提取内嵌 BMP（OLE 图片对象的 DIB 预览）→ 解码 RGBA → 按 UL/LR 两角矩形仿射贴图
    // （DrawImageParallelogram：p0=左下、p10=右下、p01=左上，图像顶行 → yT 侧，与 CAD 光栅图朝向一致）。
    // 非图片类 OLE/提取/解码失败 → 灰色矩形边框降级（与 IMAGE 一致）。
    private void EmitOle2FramePng(Ole2Frame ole, Transform2D tr)
    {
        if (_rc == null) return;
        double xL = Math.Min(ole.UpperLeftCorner.X, ole.LowerRightCorner.X);
        double xR = Math.Max(ole.UpperLeftCorner.X, ole.LowerRightCorner.X);
        double yB = Math.Min(ole.UpperLeftCorner.Y, ole.LowerRightCorner.Y);
        double yT = Math.Max(ole.UpperLeftCorner.Y, ole.LowerRightCorner.Y);
        (double X, double Y) P(double x, double y) => tr == null ? (x, y) : tr.Apply(x, y);
        var a0 = P(xL, yB);   // 左下
        var a10 = P(xR, yB);  // 右下
        var a01 = P(xL, yT);  // 左上
        double p0x = PngSx(a0.X), p0y = PngSy(a0.Y);
        double p10x = PngSx(a10.X), p10y = PngSy(a10.Y);
        double p01x = PngSx(a01.X), p01y = PngSy(a01.Y);
        if (!Ole2Frame.TryExtractBmp(ole.BinaryData, out byte[] bmpBytes))
        {
            PngImageRectFallback(p0x, p0y, p10x, p10y, p01x, p01y, 136, 136, 136);
            RecordSkip("OLE2FRAME(无内嵌BMP)");
            _log("⚠️  OLE2FRAME 未提取到内嵌 BMP（类型=" + ole.OleObjectType + "），降级为边框");
            return;
        }
        int iw = 0, ih = 0; byte[] pix = null;
        try { var d = RasterHelpers.DecodeBmpToRgba(bmpBytes); iw = d.width; ih = d.height; pix = d.rgba; }
        catch (Exception ex) { _log("⚠️  OLE2FRAME 内嵌 BMP 解码失败: " + ex.Message); }
        if (iw <= 0 || ih <= 0 || pix == null)
        {
            PngImageRectFallback(p0x, p0y, p10x, p10y, p01x, p01y, 136, 136, 136);
            RecordSkip("OLE2FRAME(BMP解码失败)");
            return;
        }
        _rc.DrawImageParallelogram(iw, ih, pix, p0x, p0y, p10x, p10y, p01x, p01y);
    }

    private void PngImageRectFallback(double p0x, double p0y, double p10x, double p10y, double p01x, double p01y, byte r, byte g, byte b)
    {
        double p11x = p10x + p01x - p0x, p11y = p10y + p01y - p0y;
        var pp = new List<RasterCanvas.P>(4)
        {
            new RasterCanvas.P((float)p0x, (float)p0y),
            new RasterCanvas.P((float)p10x, (float)p10y),
            new RasterCanvas.P((float)p11x, (float)p11y),
            new RasterCanvas.P((float)p01x, (float)p01y),
        };
        _rc.Fill(new List<List<RasterCanvas.P>> { pp }, false, new RasterCanvas.Rgb(r, g, b));
    }

    // 点样式（$PDMODE/$PDSIZE）→ PNG 位图，与 SVG/PDF 端逐样式一致（2026-10-16 定稿 + 10-17/10-26 两次各缩小一半）：
    //   ① 笔画与直线一致（lw×k，保底 1px 不加粗）；② 框半径 = 点大小值 × 0.25（数据单位；2026-10-26 用户“边框再减半”）
    //   ③ 方框半宽/圆框半径 = 同一半径；④ 加号半伸出（沿轴）= 2r；X 臂长（沿 45° 对角）= 2r（尖在 ±r√2，伸出圆外一 r）；竖线总长 = r（第 5 列）
    //   位0-2：0=点(实心小圆) 1=不显示 2=加号 3=X 4=竖线；位5(32)=外加圆框；位6(64)=外加方框
    private void EmitPointPng(double cx, double cy, RasterCanvas.Rgb col, double lw)
    {
        // 2026-10-26 用户裁定：$PDSIZE=0 不再抑制点——中心点恒按基础 1 像素（0.5px 半径）绘制，
        // 不受 PDSIZE/缩放影响；外框/臂按设置比例（PointGlyphRadius）绘制；PDMODE=1 且无外框 → 不绘制。
        int mode = _pdMode;
        int shape = mode & 7;
        bool hasFrame = (mode & 96) != 0;
        if (shape == 1 && !hasFrame) return;       // PDMODE=1：无中心图形也无外框 → 完全无显示
                                                      // 位0=1 但带外框（33/65/97）= 只画外框、无中心图形
        double r = PointGlyphRadius();                // 框半径（数据单位）= 点大小值
        if (!double.IsFinite(r) || r <= 0) return;
        double k = PngK;                              // 每数据单位像素数
        // 笔画与直线一致（像素）不加粗，但保底 1px：ScanFill 按像素中心线(py+0.5)扫描，
        // 亚像素(<1px)描边带可能整体落在两条采样线之间被整段丢弃（sample2004 加号臂消失根因）；
        // 任意长度≥1 的区间必含一条采样线，且符合用户定稿"线段像素为1，点渲染1像素"。
        float w = (float)Math.Max(lw * k, 1.0);
        double arm = r * 2.0;                         // 加号半伸出（沿轴）= 2r（伸出框外一 r）
        double xarm = r * Math.Sqrt(2.0);             // X 臂尖坐标偏移 = 2r/√2：沿 45° 对角臂长恰为 2r，伸出圆外恰一 r（2026-10-26 用户修正）
        float fx = (float)PngSx(cx), fy = (float)PngSy(cy);
        float x0 = (float)PngSx(cx - r), x1 = (float)PngSx(cx + r);
        float y0 = (float)PngSy(cy - r), y1 = (float)PngSy(cy + r);
        float ax0 = (float)PngSx(cx - arm), ax1 = (float)PngSx(cx + arm);
        float ay0 = (float)PngSy(cy - arm), ay1 = (float)PngSy(cy + arm);
        float xax0 = (float)PngSx(cx - xarm), xax1 = (float)PngSx(cx + xarm);
        float xay0 = (float)PngSy(cy - xarm), xay1 = (float)PngSy(cy + xarm);
        RasterCanvas.P P(float px, float py) => new RasterCanvas.P(px, py);
        // 外加圆框（位5=32；对话框第 2 行）：48 段折线逼近圆，半径 = 点大小值
        if ((mode & 32) != 0)
        {
            float rad = Math.Abs(x1 - fx);
            int m = Math.Max(16, (int)Math.Ceiling(2 * Math.PI * Math.Max(rad, 1f) / 2.0));
            var pts = new List<RasterCanvas.P>(m + 1);
            for (int i = 0; i <= m; i++)
            {
                double th = 2 * Math.PI * i / m;
                pts.Add(P(fx + (float)(rad * Math.Cos(th)), fy + (float)(rad * Math.Sin(th))));
            }
            _rc.StrokePolyline(pts, true, w, col);
        }
        // 外加方框（位6=64；对话框第 3 行）：半宽 = 点大小值
        if ((mode & 64) != 0)
            _rc.StrokePolyline(new List<RasterCanvas.P> { P(x0, y0), P(x0, y1), P(x1, y1), P(x1, y0) }, true, w, col);
        // 中心图形
        switch (shape)
        {
            case 0: // 点：基础 1 像素小实心圆（2026-10-26 用户裁定：中心点恒 1px 屏幕直径 = 0.5px 半径，
            // 不随 PDSIZE/缩放变化，无背景光晕；仅外框/臂随 PointGlyphRadius 比例缩放）
            {
                _rc.FillDisc(fx, fy, 0.5f, col);
                break;
            }
            case 2: // 加号（±2r，伸出框外一 r）
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(ax0, fy), P(ax1, fy) }, false, w, col);
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(fx, ay0), P(fx, ay1) }, false, w, col);
                break;
            case 3: // X（臂长沿 45° 对角 = 2r，尖在 ±r√2，伸出圆外一 r）
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(xax0, xay0), P(xax1, xay1) }, false, w, col);
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(xax0, xay1), P(xax1, xay0) }, false, w, col);
                break;
            case 4: // 竖线（总长 = r；2026-10-16 用户定稿：第 5 列竖线长度为 r（点大小值）；10-17 缩小一半后 = 点大小值/2）
            {
                double vh = r * 0.5;
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(fx, (float)PngSy(cy - vh)), P(fx, (float)PngSy(cy + vh)) }, false, w, col);
                break;
            }
        }
    }

    // SHX Shape 实体 → PNG（EmitShapeEntityPb）。AutoCAD shape 变换：
    //   局部点(字体单位) → 缩放(Size，X 额外×RelativeXScale) → 斜切(Oblique: x+=y·tanθ)
    //   → 旋转(Rotation) → 平移(InsertionPoint)；块内 shape 再整体过块变换 tr。
    // 插入点 = 字形原点(0,0)（与复杂线型 shape 的"包围盒中心对齐"不同）。
    private void EmitShapeEntityPng(Shape shp, Transform2D tr, string col, double w)
    {
        var pb = new PolyBuilder();
        GlyphRec? rec = null;
        var style = shp.ShapeStyle;
        if (style != null && !string.IsNullOrEmpty(style.Filename))
        {
            var font = GetShapeFont(style.Filename);
            if (font != null) rec = GetGlyph(font, style.Filename, shp.ShapeIndex);
        }
        double ipx = shp.InsertionPoint.X, ipy = shp.InsertionPoint.Y;
        if (tr != null) { var a = tr.Apply(ipx, ipy); ipx = a.Item1; ipy = a.Item2; }
        double size = shp.Size > 1e-9 ? shp.Size : 1.0;
        double relX = shp.RelativeXScale > 1e-9 ? shp.RelativeXScale : 1.0;
        double shear = Math.Tan(shp.ObliqueAngle);
        double cr = Math.Cos(shp.Rotation), sr = Math.Sin(shp.Rotation);
        // 字形点(字体单位) → 世界坐标（再经 PngSx/PngSy 映射到像素）
        void Xf(double gx, double gy, out double wx, out double wy)
        {
            double sx = gx * relX * size + gy * size * shear; // X 缩放 + 斜切
            double sy = gy * size;                            // Y 缩放
            double lx = sx * cr - sy * sr;                    // 旋转
            double ly = sx * sr + sy * cr;
            wx = ipx + lx; wy = ipy + ly;
            if (tr != null) { var t = tr.Apply(wx, wy); wx = t.Item1; wy = t.Item2; }
        }
        if (rec == null)
        {
            // 回退"点"：插入点处短实线
            double dd = 0.2 * size;
            Xf(-dd, 0, out double ax, out double ay);
            Xf(dd, 0, out double bx, out double by);
            pb.MoveTo((float)PngSx(ax), (float)PngSy(ay));
            pb.LineTo((float)PngSx(bx), (float)PngSy(by));
        }
        else
        {
            for (int k = 0; k < rec.Type.Count; k++)
            {
                var p = rec.Pts[k];
                switch (rec.Type[k])
                {
                    case 0: Xf(p[0], p[1], out double mx, out double my); pb.MoveTo((float)PngSx(mx), (float)PngSy(my)); break;
                    case 1: Xf(p[0], p[1], out double lx, out double ly); pb.LineTo((float)PngSx(lx), (float)PngSy(ly)); break;
                    case 2:
                        Xf(p[0], p[1], out double qx, out double qy);
                        Xf(p[2], p[3], out double ex, out double ey);
                        pb.QuadTo((float)PngSx(qx), (float)PngSy(qy), (float)PngSx(ex), (float)PngSy(ey));
                        break;
                    case 3:
                        Xf(p[0], p[1], out double c1x, out double c1y);
                        Xf(p[2], p[3], out double c2x, out double c2y);
                        Xf(p[4], p[5], out double ex2, out double ey2);
                        pb.CubicTo((float)PngSx(c1x), (float)PngSy(c1y), (float)PngSx(c2x), (float)PngSy(c2y), (float)PngSx(ex2), (float)PngSy(ey2));
                        break;
                }
            }
        }
        var penCol = HexToRgb(col);
        float pw = (float)Math.Max(1.0, w * PngK);
        foreach (var pl in pb.ToPolylines(false))
            if (pl.Count >= 2) _rc.StrokePolyline(pl, false, pw, penCol);
    }

    // Insert 展开（与 EmitInsertSvg 同逻辑）
    private void EmitInsertPng(Insert ins, Transform2D parent, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul)
    {
        if (ins?.Block == null) return;
        // 环防护（与 SVG 端同款，双保险）：自引用/循环块截断该层展开，防爆栈。
        if (!_insertAncestors.Add(ins.Block))
        {
            _log($"⚠️  块引用环，截断展开：{ins.Block.Name}");
            return;
        }
        try
        {
            EmitInsertPngCore(ins, parent, byBlockAci, byBlockDash, byBlockPat, ltMul);
        }
        finally
        {
            _insertAncestors.Remove(ins.Block);
        }
    }

    private void EmitInsertPngCore(Insert ins, Transform2D parent, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul)
    {
        var tr = Transform2D.Compose(ins, parent);
        short insAci = EffectiveAci(ins, byBlockAci);
        double[] insDash = ResolveDash(ins, byBlockDash, ltMul);
        LtpPattern insPat = ResolvePattern(ins, byBlockPat, ltMul);
        short insLayerAci = 7;
        try { insLayerAci = LayerAciIndex(ins.Layer); } catch { }
        double childLtMul = ltMul * BlockScaleOf(ins);
        bool hasAttribs = ins.Attributes != null && ins.Attributes.Count > 0;

        // 顶层 Insert：内容签名精灵缓存（位置作 blit 锚点，不入 key）。
        // 同一 (块, 旋转/缩放, 颜色, 线型, 线宽, 层) 渲染出的像素内容完全相同，仅落点平移不同 →
        // 命中整数平移 blit；未命中离屏光栅化一次后缓存。治真实负载 Insert 60s 瓶颈（distinct 内容仅 ~5k）。
        // 有属性 Insert 拆两部分：【几何】(跳过 ATTDEF) 位置无关 → 进缓存(|geo)；【属性文字】值随插入不同 → 单独直渲。
        if (parent == null)
        {
            if (hasAttribs)
            {
                // 有属性 Insert：【几何】（块子实体，跳过 ATTDEF）位置无关 → 按内容签名缓存 blit；
                // 【属性文字】(ins.Attributes，世界坐标、值随插入不同) 单独直渲。
                // 治真实负载主瓶颈：1,175,040 个有属性 Insert 原全走 DrawInsertContent 直渲（~62s）。
                if (PerfOn) _insTopCalls++;
                string geoSig = InsertSig(ins, tr, insAci, insDash, insPat, ltMul, insLayerAci, true);
                if (PerfOn) _insSigCalls[geoSig] = _insSigCalls.TryGetValue(geoSig, out var _ac) ? _ac + 1 : 1;
                if (_insertSprites.TryGetValue(geoSig, out var geoHit))
                {
                    if (PerfOn) _insAttribGeoHit++;
                    BlitSprite(geoHit, ins.InsertPoint.X, ins.InsertPoint.Y);
                    DrawInsertAttribs(ins, tr, insAci, insDash, insPat, childLtMul, insLayerAci);
                    return;
                }
                if (InsertContentExtents(ins, out double gl, out double gr, out double gt, out double gb))
                {
                    if (PerfOn) _insAttribGeoMiss++;
                    TryEmitInsertSprite(geoSig, ins.InsertPoint.X, ins.InsertPoint.Y, gl, gr, gt, gb,
                        () => DrawInsertGeometry(ins, tr, insAci, insDash, insPat, childLtMul, insLayerAci));
                    DrawInsertAttribs(ins, tr, insAci, insDash, insPat, childLtMul, insLayerAci);
                    return;
                }
                // 空 AABB（几何无定界）：几何直渲 + 属性直渲
                if (PerfOn) { _insEmptyAabbCalls++; _insAttribGeoDirect++; }
                DrawInsertGeometry(ins, tr, insAci, insDash, insPat, childLtMul, insLayerAci);
                DrawInsertAttribs(ins, tr, insAci, insDash, insPat, childLtMul, insLayerAci);
                return;
            }
            if (PerfOn) _insTopCalls++;
            string sig = InsertSig(ins, tr, insAci, insDash, insPat, ltMul, insLayerAci, false);
            if (PerfOn) _insSigCalls[sig] = _insSigCalls.TryGetValue(sig, out var _cc) ? _cc + 1 : 1;
            if (_insertSprites.TryGetValue(sig, out var hit))
            {
                if (PerfOn) _insSpriteHit++;
                BlitSprite(hit, ins.InsertPoint.X, ins.InsertPoint.Y);
                return;
            }
            if (InsertContentExtents(ins, out double left, out double right, out double top, out double bottom))
            {
                if (PerfOn) _insSpriteMiss++;
                TryEmitInsertSprite(sig, ins.InsertPoint.X, ins.InsertPoint.Y,
                    left, right, top, bottom,
                    () => DrawInsertGeometry(ins, tr, insAci, insDash, insPat, childLtMul, insLayerAci));
                return;
            }
            // 空 AABB 直渲：记录块直接子实体类型/数量分布（定位为何 ExpandInsert 取不到点）
            if (PerfOn)
            {
                _insEmptyAabbCalls++;
                var ents = ins.Block?.Entities;
                int cnt = ents == null ? 0 : ents.Count;
                _insEmptyAabbCounts[cnt.ToString()] = _insEmptyAabbCounts.TryGetValue(cnt.ToString(), out var cc2) ? cc2 + 1 : 1;
                if (ents != null)
                    foreach (var be in ents)
                    {
                        string t = be.GetType().Name;
                        _insEmptyAabbTypes[t] = _insEmptyAabbTypes.TryGetValue(t, out var tt) ? tt + 1 : 1;
                    }
            }
            if (PerfOn) _insSpriteDirect++;
            long t0 = Stopwatch.GetTimestamp();
            DrawInsertGeometry(ins, tr, insAci, insDash, insPat, childLtMul, insLayerAci); // 空内容/无法定界：几何直渲（无属性，等价全内容）
            if (PerfOn) _insDirectDrawMs += (Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency;
            return;
        }

        // 嵌套 Insert（parent != null）：走完整展开（含属性值，随父变换），不进精灵缓存。
        DrawInsertContent(ins, tr, parent, insAci, insDash, insPat, childLtMul, insLayerAci);
    }

    // Insert 内容签名（不含落点）：块名 + 旋转/缩放(不含平移) + 颜色 + 线型 dash + 复杂线型 + 线型比例 + 层 + 内容模式。
    // 顶层 Insert 的父上下文固定（byBlockAci=7/byBlockDash=null/byBlockPat=null），故签名完整覆盖影响像素的全部因素。
    // geometryOnly：true=有属性 Insert 的【几何】（跳过 ATTDEF）；false=无属性 Insert 的【全内容】（含 ATTDEF 模板默认值）。
    //   两者像素不同（有属性版不画 ATTDEF，改画真实属性值），必须用不同 key，否则缓存串味。
    private static string InsertSig(Insert ins, Transform2D tr, short insAci, double[] insDash, LtpPattern insPat, double ltMul, short insLayerAci, bool geometryOnly)
    {
        string dash = insDash == null ? "n" : string.Join(",", insDash.Select(v => Math.Round(v, 4).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        return ins.Block.Name + "|"
            + Math.Round(tr.Ca * 1e6) + "," + Math.Round(tr.Sa * 1e6) + ","
            + Math.Round(tr.Sx * 1e6) + "," + Math.Round(tr.Sy * 1e6) + "|"
            + insAci + "|" + dash + "|"
            + (insPat == null ? "n" : insPat.Segs.Count) + "|" + Math.Round(ltMul * 1e4) + "|" + insLayerAci
            + (geometryOnly ? "|geo" : "|full");
    }

    // 顶层 Insert 内容包围盒（相对插入点的世界轴对齐 AABB，含安全外扩）。
    // EntityWorldAabb(Insert) 经 ExpandInsert 用 Insert 自身变换（顶层=世界）展开块子实体 → 已覆盖块内全部几何（含 ATTDEF 文字盒）。
    // 外扩须 ≥ 实际绘制像素超出 AABB 的量：
    //   · 线型实体：MaxHalfStroke（线宽半宽）。
    //   · 点实体：EntityWorldAabb 只记【中心】(不含字形)，故仅当块内【确有 Point】时才加 2*PointGlyphRadius（+/X 臂伸出 2r）。
    //     ⚠️ 不能无条件加 PointGlyphRadius：zoom10 下它 ≈95 数据单位 ≈950px，会把 ~7000px 的建筑符号 inflate 到 >8192px
    //        全部误判"过大直渲"（首测 547200 次过大、Insert 60s 无改善的根因）。建筑块几乎不含 Point，条件外扩后绝大多数可进精灵。
    //   · 1.0/PngK = 1px AA 余量（数据单位）。
    // 外扩保证精灵盒 ⊇ 实际绘制像素（不裁剪内容，最多略大）。返回 false = 空内容（无可绘制几何）。
    private bool InsertContentExtents(Insert ins, out double left, out double right, out double top, out double bottom)
    {
        left = right = top = bottom = 0;
        if (!EntityWorldAabb(ins, out double mnx, out double mny, out double mxx, out double mxy))
            return false;
        double half = MaxHalfStroke(ins, 1.0) + 1.0 / PngK;
        if (BlockContainsPoint(ins.Block))
            half += 2.0 * PointGlyphRadius(); // 仅块内含点：加字形半径（+ / X 臂伸出 2r）
        mnx -= half; mny -= half; mxx += half; mxy += half;
        double ax = ins.InsertPoint.X, ay = ins.InsertPoint.Y;
        left = Math.Max(0, ax - mnx);
        right = Math.Max(0, mxx - ax);
        top = Math.Max(0, mxy - ay);
        bottom = Math.Max(0, ay - mny);
        return true;
    }

    // 块内（含嵌套 Insert 递归）是否含 Point 实体——决定 Insert 精灵包围盒是否需外扩点字形半径。
    private static bool BlockContainsPoint(ACadSharp.Tables.BlockRecord br)
    {
        if (br == null) return false;
        foreach (var e in br.Entities)
        {
            if (e is Point) return true;
            if (e is Insert ni && ni.Block != null && BlockContainsPoint(ni.Block)) return true;
        }
        return false;
    }

    // 仅画【几何】（块子实体）：无属性 → 含 ATTDEF 模板默认值（全内容，|full 签名）；
    // 有属性 → 跳过 ATTDEF 模板默认值（改由 DrawInsertAttribs 画真实值，|geo 签名）。
    // 几何位置无关（相对插入点固定）→ 可进内容签名精灵缓存。
    private void DrawInsertGeometry(Insert ins, Transform2D tr, short insAci, double[] insDash, LtpPattern insPat, double childLtMul, short insLayerAci)
    {
        bool hasAttribs = ins.Attributes != null && ins.Attributes.Count > 0;
        foreach (var e in BlockEntitiesInFileOrder(ins.Block))
        {
            if (hasAttribs && e is AttributeDefinition) continue;
            EmitEntityPng(e, tr, tr.Scale, insAci, insDash, insPat, childLtMul, insLayerAci);
        }
    }

    // 仅画【属性文字】（ins.Attributes，世界坐标，值随插入不同）：顶层直渲，位置=世界坐标（变换=null）。
    // 字高=世界字高：AutoCAD 存档时已把字高预乘插入比例，不再乘 tr.Scale（否则 gc200 scale=10 时
    // "3.94" 字高 20 被双重缩放开成 200；GT AutoCAD 实测按 20 渲染）。位置与值均随每个插入变化 → 不进缓存。
    private void DrawInsertAttribs(Insert ins, Transform2D tr, short insAci, double[] insDash, LtpPattern insPat, double childLtMul, short insLayerAci)
    {
        if (ins.Attributes == null) return;
        foreach (var att in ins.Attributes)
            EmitEntityPng(att, null, 1.0, insAci, insDash, insPat, childLtMul, insLayerAci);
    }

    // 展开 Insert 块子实体（原 Core 主体）。ATTDEF 只在【无】实际属性值时画（模板默认值）；
    // 有 ATTRIB 时跳过模板默认值、改画 ins.Attributes 的真实值（其 InsertPoint 已是世界坐标，只受父层累积变换 parent，不再套本 tr）。
    private void DrawInsertContent(Insert ins, Transform2D tr, Transform2D parent, short insAci, double[] insDash, LtpPattern insPat, double childLtMul, short insLayerAci)
    {
        bool hasAttribs = ins.Attributes != null && ins.Attributes.Count > 0;
        foreach (var e in BlockEntitiesInFileOrder(ins.Block))
        {
            if (hasAttribs && e is AttributeDefinition) continue; // 有实际属性值 → 跳过模板默认值
            EmitEntityPng(e, tr, tr.Scale, insAci, insDash, insPat, childLtMul, insLayerAci);
        }
        if (hasAttribs)
            // ATTRIB 字高（组40）与位置（组10）同一坐标系（AutoCAD 已预乘插入比例）：按位置所用变换 parent 缩放，
            // 不乘本 INSERT 全变换 tr 的 tr.Scale（双重缩放；见 DrawInsertAttribs 注释）。
            foreach (var att in ins.Attributes)
                EmitEntityPng(att, parent, parent?.Scale ?? 1.0, insAci, insDash, insPat, childLtMul, insLayerAci);
    }

    // Insert 内容签名精灵渲染：命中→blit；未命中→离屏光栅化一次（临时重定位使插入点→(Bx,By)，与 TryEmitSprite 同机制）后 blit。
    // 独立缓存 _insertSprites（区别于点/字形）：Insert 内容跨【不同实体】重复，单图+分块均受益。
    // 内存预算守卫：内容包围盒可能很大（MaxHalfStroke 外扩），按像素字节设上限，超限走直接渲染（防内存膨胀）。
    private void TryEmitInsertSprite(string key, double anchorX, double anchorY,
        double left, double right, double top, double bottom, Action draw)
    {
        double k = PngK;
        int pad = 2;
        int Bx = (int)Math.Ceiling(Math.Max(left, 0) * k) + pad;
        int By = (int)Math.Ceiling(Math.Max(top, 0) * k) + pad;
        int sw = (int)Math.Ceiling((Math.Max(left, 0) + Math.Max(right, 0)) * k) + Bx + pad;
        int sh = (int)Math.Ceiling((Math.Max(top, 0) + Math.Max(bottom, 0)) * k) + By + pad;
        if (sw < 1) sw = 1; if (sh < 1) sh = 1;
        long memBytes = (long)sw * sh * 4;
        bool tooBig = sw > MaxSpriteSide || sh > MaxSpriteSide;
        bool overBudget = _insertSprites.Count >= MaxInsertSprites || (_insSpriteMemBytes + memBytes) > MaxInsertSpriteMemBytes;
        if (PerfOn)
        {
            // 临时诊断：按 distinct 签名记录实际最大边像素分布 + 调用权重
            int side = Math.Max(sw, sh);
            int b = side < 256 ? 0 : side < 1024 ? 1 : side < 4096 ? 2 : side < 8192 ? 3
                : side < 16384 ? 4 : side < 32768 ? 5 : side < 65536 ? 6 : 7;
            _insSideHist[b]++;
            if (_insSigCalls.TryGetValue(key, out var cc)) _insDirectCallWeight += (tooBig || overBudget) ? cc : 0;
            if (tooBig || overBudget) _insSigDirect++; else _insSigCached++;
        }
        if (tooBig || overBudget)
        {
            if (PerfOn) _insSpriteDirect++;
            draw(); // 过大/超预算：直接渲染（仍受剔除保护）
            return;
        }
        var sprite = new RasterCanvas(sw, sh);
        sprite.Clear(0, 0, 0, 0);
        // 临时重定位：让插入点映射到精灵内 (Bx,By)。PngSx_global(a)=PngSx(当前)(a)+_tileOx；
        // 设 _tileOx = PngSx_global(a) - Bx ⇒ PngSx(精灵)(a)=Bx（与 TryEmitSprite 同）。
        double prevOx = _tileOx, prevOy = _tileOy; var prevRc = _rc;
        _rc = sprite;
        _tileOx = PngSx(anchorX) + _tileOx - Bx;
        _tileOy = PngSy(anchorY) + _tileOy - By;
        try { draw(); } finally { _rc = prevRc; _tileOx = prevOx; _tileOy = prevOy; }
        var sp = new PngSprite { Rgba = sprite.ToRgbaBuffer(), W = sw, H = sh, Bx = Bx, By = By };
        _insertSprites[key] = sp;
        _insSpriteMemBytes += memBytes;
        BlitSprite(sp, anchorX, anchorY);
    }

    #endregion

    #region ===== 描边基础（折线 + 虚线走查，相位与 SVG stroke-dasharray 一致）=====

    // 描边折线（数据单位坐标）。dashed 时手动沿路径走查 dash/gap（相位从路径起点），闭合 + 无虚线 → 干净转角。
    private void StrokePts(List<(double X, double Y)> pts, bool closed, string col, double w, double[] dash)
    {
        if (pts == null || pts.Count < 2 || _rc == null) return;
        var colRgb = HexToRgb(col);
        // 保底 1px：描边带半宽 h=w/2，ScanFill 在像素中心线(py+0.5)采样；
        // 亚像素(<1px)描边带可能整体落在两条采样线之间 → 零像素覆盖 → 整条线被丢弃
        // （低分辨率下 MLine/PolygonMesh/细多段线部分消失的根因，与 EmitPointPng 同款处理）。
        float widthPx = (float)Math.Max(1.0, w * PngK);
        bool dashed = dash != null && dash.Length > 0;
        if (dashed)
        {
            var cum = BuildCum(pts);
            var spans = WalkDashPx(pts, cum, dash);
            foreach (var sp in spans)
                if (sp.Count >= 2) _rc.StrokePolyline(sp, false, widthPx, colRgb);
        }
        else
        {
            var pp = new List<RasterCanvas.P>(pts.Count);
            foreach (var p in pts) pp.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
            _rc.StrokePolyline(pp, closed, widthPx, colRgb);
        }
    }

    // 沿折线走查 [dash, gap, dash, ...]（正长度交替，dash 起头），落笔区间生成像素子折线。
    private List<List<RasterCanvas.P>> WalkDashPx(List<(double X, double Y)> pts, double[] cum, double[] dash)
    {
        var result = new List<List<RasterCanvas.P>>();
        double L = cum[cum.Length - 1];
        if (L <= 1e-9) return result;
        double period = 0;
        foreach (var v in dash) period += v;
        if (period <= 1e-9) return result;
        double pos = 0;
        int i = 0, guard = 0;
        while (pos < L - 1e-9 && guard++ < 50000000)
        {
            int idx = i % dash.Length;
            double span = dash[idx];
            // dash 数组按 [dash, gap, dash, gap, ...] 正长度交替（dash 起头）：
            // 偶数下标=dash 段（落笔）、奇数下标=gap 段（跳过）。
            // 旧实现每段都落笔 → 简单虚线（DASHED 等）在 PNG 下被画成实线。
            if (idx % 2 == 0 && span > 1e-9)
            {
                var sub = new List<RasterCanvas.P>();
                AppendSpanPathPx(sub, pts, cum, pos, Math.Min(pos + span, L));
                if (sub.Count >= 2) result.Add(sub);
            }
            pos += span;
            i++;
        }
        return result;
    }

    // 把弧长区间 [s0, s1] 的子路径追加到像素折线（端点精确插值 + 区间内原始采样顶点）。
    private void AppendSpanPathPx(List<RasterCanvas.P> sub, List<(double X, double Y)> pts, double[] cum, double s0, double s1)
    {
        if (s1 - s0 < 1e-9) return;
        int n = pts.Count;
        double L = cum[n - 1];
        if (s0 < 0) s0 = 0;
        if (s1 > L) s1 = L;
        if (s1 - s0 < 1e-9) return;

        PathPointAt(pts, cum, s0, out double ax, out double ay, out _, out _);
        sub.Add(new RasterCanvas.P((float)PngSx(ax), (float)PngSy(ay)));
        for (int i = 1; i < n; i++)
        {
            double c = cum[i];
            if (c <= s0 + 1e-9 || c >= s1 - 1e-9) continue;
            sub.Add(new RasterCanvas.P((float)PngSx(pts[i].X), (float)PngSy(pts[i].Y)));
        }
        PathPointAt(pts, cum, s1, out double bx, out double by, out _, out _);
        sub.Add(new RasterCanvas.P((float)PngSx(bx), (float)PngSy(by)));
    }

    #endregion

    #region ===== 复杂线型（SHX 真实字形，WalkPattern 同算法）=====

    // 复杂线型（PNG）：沿路径走查模式生成描边路径（dash 子路径 + 字形子路径）。返回 false = 不支持/空。
    private bool TryEmitComplexPng(Entity ent, Transform2D tr, LtpPattern pat, string col, double w)
    {
        if (pat == null) return false;
        if (!TryGetPathPoints(ent, tr, out var pts) || pts.Count < 2) return false;
        var cum = BuildCum(pts);
        if (cum[cum.Length - 1] <= 1e-9) return false;
        var pb = new PolyBuilder();
        var texts = new List<LtpTextPlace>();
        var colRgb = HexToRgb(col);
        float widthPx = (float)Math.Max(1.0, w * PngK);
        int shapeEmitted = 0;
        if (PerfOn)
        {
            long _tw0 = Environment.TickCount64;
            WalkPatternPx(pb, pts, cum, pat, texts, colRgb, widthPx, ref shapeEmitted);
            _lwWalkMs += Environment.TickCount64 - _tw0;
        }
        else WalkPatternPx(pb, pts, cum, pat, texts, colRgb, widthPx, ref shapeEmitted);
        // SHX 字形段已在走查内经「字形精灵缓存」直接光栅化到 _rc（不再进 pb），故字形也算"已渲染"。
        if (pb.IsEmpty && texts.Count == 0 && shapeEmitted == 0) return false;
        if (PerfOn)
        {
            long _ts0 = Environment.TickCount64;
            foreach (var pl in pb.ToPolylines(false))
                if (pl.Count >= 2) _rc.StrokePolyline(pl, false, widthPx, colRgb);
            _lwStrokeAfterWalkMs += Environment.TickCount64 - _ts0;
            long _td0 = Environment.TickCount64;
            foreach (var p in texts)
                DrawTextPng(p.X, p.Y, p.Text, p.Height, col, -p.RotDeg, ResolveFontFamily(p.Font), p.WidthFactor);
            _lwDrawTextMs += Environment.TickCount64 - _td0;
        }
        else
        {
            foreach (var pl in pb.ToPolylines(false))
                if (pl.Count >= 2) _rc.StrokePolyline(pl, false, widthPx, colRgb);
            // 文字段：用 TTF 实心字形渲染（rotCanvas 传 -数据系角）
            foreach (var p in texts)
                DrawTextPng(p.X, p.Y, p.Text, p.Height, col, -p.RotDeg, ResolveFontFamily(p.Font), p.WidthFactor);
        }
        return true;
    }

    // 沿路径走查模式（WalkPattern 的位图版，算法与 SVG/PDF 完全一致）：
    //   dash 段→线子路径；正跨度 shape 段→线子路径 + 段末字形；间隙型 shape→段末字形（不画线）；
    //   gap 段→留空；text 段→跨度中点文字。起始相位同 PatternPhase（首字形两端对称规则）。
    // colRgb/widthPx 透传给字形精灵（同线宽+同颜色变体才可缓存）；shapeEmitted 累加已 blit 的字形段数。
    private void WalkPatternPx(PolyBuilder pb, List<(double X, double Y)> pts, double[] cum, LtpPattern pat,
        List<LtpTextPlace> textOut, RasterCanvas.Rgb colRgb, float widthPx, ref int shapeEmitted)
    {
        double L = cum[cum.Length - 1];
        var segs = pat.Segs;
        int n = segs.Count;
        if (L <= 1e-9 || n == 0) return;
        var cumpat = new double[n + 1];
        for (int j = 0; j < n; j++) cumpat[j + 1] = cumpat[j] + segs[j].Len;
        double phi = PatternPhase(pat, L);
        int i = 0;
        while (i < n - 1 && cumpat[i + 1] <= phi) i++;
        double pos = phi;
        double into = phi - cumpat[i];
        bool perf = PerfOn;
        double firstStart = double.MaxValue, lastEnd = -1.0; // 已画实线跨度的首/末（供端部空段填充，与 SVG/PDF 一致）
        int guard = 0;
        while (pos < L + phi - 1e-9 && guard++ < 50000000)
        {
            var seg = segs[i % n];
            double span = seg.Len;
            double take = Math.Max(0, Math.Min(span - into, (L + phi) - pos));
            if (take > 1e-12)
            {
                double p0 = pos - phi;
                double p1 = p0 + take;
                if (seg.Kind == LtpSeg.Dash)
                {
                    if (perf) { _lwDashCnt++; long _a0 = Environment.TickCount64; AppendSpanPb(pb, pts, cum, p0, p1); _lwDashMs += Environment.TickCount64 - _a0; }
                    else AppendSpanPb(pb, pts, cum, p0, p1);
                    if (p0 < firstStart) firstStart = p0;
                    if (p1 > lastEnd) lastEnd = p1;
                }
                else if (seg.Kind == LtpSeg.Shape && !seg.InGap)
                {
                    AppendSpanPb(pb, pts, cum, p0, p1); // 正跨度 shape 段同样画线（与 SVG/PDF 一致）
                    if (p0 < firstStart) firstStart = p0;
                    if (p1 > lastEnd) lastEnd = p1;
                }
            }
            if (seg.Kind == LtpSeg.Shape)
            {
                long _b0 = 0;
                if (perf) { _lwShapeCnt++; _b0 = Environment.TickCount64; }
                // 字形置于段跨度末端（图案绝对坐标含周期偏移：pos 为段起点的图案绝对坐标，
                // 末端 = pos + (span−into)；若用 cumpat[(i%n)+1] 则丢失周期数，字形冻结在第一周期，
                // 且 φ 大于段内坐标时首字形位置为负被误裁剪；含零长间隙型段——take=0 也需输出）
                double glyphAt = pos + (span - into) - phi;
                // 对齐 AutoCAD 参考（参考模型 PDF 实测）：仅当段末之后紧接的下一段也完整落在线内时
                // 才输出该段末字形——如线端附近的 tick 段完整画出、但下一 shape 段溢出线端时，
                // 该段末的 tick 字形被省略（线跨度本身仍画）。仅用 glyphAt<=L 会多画这类端部字形。
                double nextLen = Math.Abs(segs[(i + 1) % n].Len);
                // 头端规则（同 SVG WalkPattern）：首实线跨度出现前（头端填充区）不输出字形，
                // 头端保持干净直线（BATTING 弹簧头端 ∪ 省略，与 AutoCAD 参考一致）。
                if (glyphAt >= -1e-9 && glyphAt + nextLen <= L + 1e-9 && firstStart != double.MaxValue)
                {
                    double at = Math.Max(0.0, Math.Min(L, glyphAt));
                    PathPointAt(pts, cum, at, out double px, out double py, out double tx, out double ty);
                    EmitShapeGlyphPng(px, py, tx, ty, seg, colRgb, widthPx);
                    shapeEmitted++;
                }
                if (perf) _lwShapeMs += Environment.TickCount64 - _b0;
            }
            else if (seg.Kind == LtpSeg.Text && textOut != null && !string.IsNullOrEmpty(seg.TextContent))
            {
                if (perf) _lwTextCnt++;
                // 文字段端部规则（与 SVG/PDF 一致，参考 PDF 实测）：文字段【整体】完整落在线内才输出
                // （GAS 第 4 段 [134.528,142.148] 末越出 L=141.421 即省略，起点在线内也不例外）。
                double textAt = pos + (span - into) - seg.Len / 2.0 - phi;
                if (textAt - seg.Len / 2.0 >= -1e-9 && textAt + seg.Len / 2.0 <= L + 1e-9)
                {
                    double at = Math.Max(0.0, Math.Min(L, textAt));
                    PathPointAt(pts, cum, at, out double cx, out double cy, out double tx, out double ty);
                    textOut.Add(BuildTextPlace(cx, cy, tx, ty, seg));
                }
            }
            if (perf) _lwWalkIters++;
            pos += take;
            i++;
            into = 0;
        }
        // 端部空段填充（参考 AutoCAD 模型 PDF 流实测，与 WalkPattern 一致）：线端落在
        // gap/文字段的不完整空段画成实心（'GAS_LINE' 实测 [0,6.8932]∪[134.5282,141.4214] 实线）；
        // 两端落在实线段中时为无操作（RED/CYAN 不受影响）。
        if (lastEnd < 0.0)
            AppendSpanPb(pb, pts, cum, 0.0, L);
        else
        {
            if (firstStart > 1e-9) AppendSpanPb(pb, pts, cum, 0.0, firstStart);
            if (L - lastEnd > 1e-9) AppendSpanPb(pb, pts, cum, lastEnd, L);
        }
    }

    // 把弧长区间 [s0, s1] 的子路径追加到 PolyBuilder。
    private void AppendSpanPb(PolyBuilder pb, List<(double X, double Y)> pts, double[] cum, double s0, double s1)
    {
        if (s1 - s0 < 1e-9) return;
        int n = pts.Count;
        double L = cum[n - 1];
        if (s0 < 0) s0 = 0;
        if (s1 > L) s1 = L;
        if (s1 - s0 < 1e-9) return;
        PathPointAt(pts, cum, s0, out double ax, out double ay, out _, out _);
        pb.MoveTo((float)PngSx(ax), (float)PngSy(ay));
        for (int i = 1; i < n; i++)
        {
            double c = cum[i];
            if (c <= s0 + 1e-9 || c >= s1 - 1e-9) continue;
            pb.LineTo((float)PngSx(pts[i].X), (float)PngSy(pts[i].Y));
        }
        PathPointAt(pts, cum, s1, out double bx, out double by, out _, out _);
        pb.LineTo((float)PngSx(bx), (float)PngSy(by));
    }

    // 在路径点绘制真实 SHX 字形（位图版，走「字形精灵缓存」——同变体离屏光栅化一次、重复整数平移 blit）。
    // 治本 SDF 多段路径：原 85K 字形展开成 ~130 万碎段各自走 SDF（多段 12.17s），现仅 (字形,缩放,角度,线宽,颜色)
    // 变体首次光栅化到小离屏精灵，其余命中 blit（O(精灵面积) 像素拷贝，远快于 SDF）。
    // 角度按 1° 量化（rotType=0/2 角度恒定无损；rotType=1 随切向连续→量化保命中，亚 0.5° 旋转误差对细线字形不可见）。
    // anchor = 字形原点(0,0)的世界系坐标（= 路径点 + offset），与 SVG/PDF 的 EmitShapeGlyph 原点锚定语义一致。
    private void EmitShapeGlyphPng(double px, double py, double tx, double ty, LtpSeg seg, RasterCanvas.Rgb colRgb, float widthPx)
    {
        GlyphRec? rec = null;
        if (!string.IsNullOrEmpty(seg.ShapeFile))
        {
            var font = GetShapeFont(seg.ShapeFile);
            if (font != null && font.TryGetShapeNumber(seg.ShapeName, out int num))
                rec = GetGlyph(font, seg.ShapeFile, num);
        }
        if (rec == null || rec.Empty)
        {
            // SHX 库/形状不可用 → 回退"点"（沿切向居中的短实线），保持图案节奏（直接渲染，不走缓存）
            double dd = seg.Len > 1e-9 ? Math.Min(seg.ShapeScale * 4.0, 0.9 * seg.Len) : 0.1;
            if (dd < 0.05) dd = 0.05;
            _rc.StrokePolyline(new List<RasterCanvas.P>
            {
                new RasterCanvas.P((float)PngSx(px - tx * dd / 2), (float)PngSy(py - ty * dd / 2)),
                new RasterCanvas.P((float)PngSx(px + tx * dd / 2), (float)PngSy(py + ty * dd / 2))
            }, false, widthPx, colRgb);
            return;
        }
        // shape 缩放 = 组码46 标称缩放（已含 $LTSCALE 全局线型缩放）。3 位量化防浮点噪声，亚像素级差异不影响命中。
        double S = Math.Round((seg.ShapeScale > 1e-9 ? seg.ShapeScale : 1.0) * 1000.0) / 1000.0;
        double ang;
        switch (seg.RotType)
        {
            // relative（74 bit0=0）：字形角 = 切向角 + 组码50 偏移。漏加 ShapeRot 会让
            // "180° 交替"线型（BATTING 弹簧）两个 shape 同向画成锯齿。
            case 1: ang = Math.Atan2(ty, tx) + seg.ShapeRot * Math.PI / 180.0; break;
            case 2: ang = seg.ShapeRot * Math.PI / 180.0; break; // absolute（74 bit0=1）
            default: ang = 0; break;                   // upright
        }
        // 角度量化到 1°（key 与渲染同用量化值，保证同 key 同像素）。
        double angQ = Math.Round(ang * 180.0 / Math.PI) * Math.PI / 180.0;
        double ca = Math.Cos(angQ), sa = Math.Sin(angQ);
        // offset（DXF 44/45）：字形原点（插入点）= 路径点 + OX·切向 + OY·法向(-ty,tx)，AutoCAD 语义，
        // 与 Svg.cs EmitShapeGlyph 一致（旧实现切向取负 → BATTING 拱错位半周期、螺旋方向看似与 CAD 相反）。
        // anchor=路径点+offset：字形原点的精确世界位置（OX/OY 只影响 anchor 位置，不影响精灵内容，故不入 key）。
        double oxw = seg.OX * tx - seg.OY * ty;
        double oyw = seg.OX * ty + seg.OY * tx;
        double anchorX = px + oxw, anchorY = py + oyw;

        // 精灵内字形相对 anchor 的旋转包围盒（4 角点旋转取极值）→ left/right/top/bottom（数据单位）。
        double cx0 = rec.MinX * S, cx1 = rec.MaxX * S, cy0 = rec.MinY * S, cy1 = rec.MaxY * S;
        double mnx = double.MaxValue, mxx = double.MinValue, mny = double.MaxValue, mxy = double.MinValue;
        double[] ccx = { cx0, cx1, cx1, cx0 }, ccy = { cy0, cy0, cy1, cy1 };
        for (int j = 0; j < 4; j++)
        {
            double rx = ccx[j] * ca - ccy[j] * sa;
            double ry = ccx[j] * sa + ccy[j] * ca;
            if (rx < mnx) mnx = rx; if (rx > mxx) mxx = rx;
            if (ry < mny) mny = ry; if (ry > mxy) mxy = ry;
        }
        double left = -mnx, right = mxx, top = mxy, bottom = -mny;

        string key = seg.ShapeFile + "|" + seg.ShapeName + "|" + S.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "|" + (int)Math.Round(ang * 180.0 / Math.PI)
            + "|" + Math.Round(widthPx * 100.0) / 100.0
            + "|" + (int)colRgb.R + "," + (int)colRgb.G + "," + (int)colRgb.B;
        TryEmitGlyphSprite(rec, anchorX, anchorY, ca, sa, S, widthPx, colRgb, left, right, top, bottom, key);
    }

    // 字形精灵缓存：命中→blit；未命中→离屏光栅化一次（临时重定位使 anchor→(Bx,By)，与 TryEmitSprite 同机制）后 blit。
    // 单图/分块通用（BlitSprite 含 _tileOx/_tileOy 还原，整数平移不改变亚像素 AA 覆盖→与逐次重绘一致）。
    private void TryEmitGlyphSprite(GlyphRec rec, double anchorX, double anchorY,
        double ca, double sa, double S, float widthPx, RasterCanvas.Rgb colRgb,
        double left, double right, double top, double bottom, string key)
    {
        if (_glyphSprites.TryGetValue(key, out var hit))
        {
            if (PerfOn) _glyphHit++;
            BlitSprite(hit, anchorX, anchorY);
            return;
        }
        if (PerfOn) _glyphMiss++;

        double k = PngK;
        int pad = 2;
        int Bx = (int)Math.Ceiling(Math.Max(left, 0) * k) + pad;     // anchor 在精灵内的 x
        int By = (int)Math.Ceiling(Math.Max(top, 0) * k) + pad;       // anchor 在精灵内的 y（上=small y）
        int sw = (int)Math.Ceiling((Math.Max(left, 0) + Math.Max(right, 0)) * k) + Bx + pad;
        int sh = (int)Math.Ceiling((Math.Max(top, 0) + Math.Max(bottom, 0)) * k) + By + pad;
        if (sw < 1) sw = 1; if (sh < 1) sh = 1;
        if (sw > MaxSpriteSide || sh > MaxSpriteSide || _glyphSprites.Count >= MaxGlyphSprites)
        {
            StrokeGlyphDirect(rec, anchorX, anchorY, ca, sa, S, widthPx, colRgb); // 过大/缓存满：直接渲染
            return;
        }
        var sprite = new RasterCanvas(sw, sh);
        sprite.Clear(0, 0, 0, 0);
        // 临时重定位：使 PngSx(anchor)=Bx、PngSy(anchor)=By（与 TryEmitSprite 同），离屏精灵与任意瓦片像素坐标仅整数平移之差。
        double prevOx = _tileOx, prevOy = _tileOy; var prevRc = _rc;
        _rc = sprite;
        _tileOx = PngSx(anchorX) + _tileOx - Bx;
        _tileOy = PngSy(anchorY) + _tileOy - By;
        try { StrokeGlyphDirect(rec, anchorX, anchorY, ca, sa, S, widthPx, colRgb); }
        finally { _rc = prevRc; _tileOx = prevOx; _tileOy = prevOy; }
        var sp = new PngSprite { Rgba = sprite.ToRgbaBuffer(), W = sw, H = sh, Bx = Bx, By = By };
        _glyphSprites[key] = sp;
        BlitSprite(sp, anchorX, anchorY);
    }

    // 把字形笔迹光栅化到当前 _rc（离屏精灵或主画布）：局部点 (gx,gy) → 世界 (anchor + (gx*S,gy*S) 旋转) → PngSx/PngSy → 折线描边。
    // anchor=字形原点世界坐标。曲线（Q/C）经 PolyBuilder.ToPolylines 展平为折线后走 SDF 描边（小精灵内仅变体首次调用）。
    private void StrokeGlyphDirect(GlyphRec rec, double anchorX, double anchorY, double ca, double sa, double S, float widthPx, RasterCanvas.Rgb colRgb)
    {
        var pb = new PolyBuilder();
        for (int k = 0; k < rec.Type.Count; k++)
        {
            var p = rec.Pts[k];
            void Xf(float gx, float gy, out double mx, out double my)
            {
                double lx = gx * S, ly = gy * S;
                mx = anchorX + lx * ca - ly * sa;
                my = anchorY + lx * sa + ly * ca;
            }
            switch (rec.Type[k])
            {
                case 0: Xf(p[0], p[1], out double mx, out double my); pb.MoveTo((float)PngSx(mx), (float)PngSy(my)); break;
                case 1: Xf(p[0], p[1], out double lx, out double ly); pb.LineTo((float)PngSx(lx), (float)PngSy(ly)); break;
                case 2:
                    Xf(p[0], p[1], out double qx, out double qy); Xf(p[2], p[3], out double ex, out double ey);
                    pb.QuadTo((float)PngSx(qx), (float)PngSy(qy), (float)PngSx(ex), (float)PngSy(ey)); break;
                case 3:
                    Xf(p[0], p[1], out double c1x, out double c1y); Xf(p[2], p[3], out double c2x, out double c2y);
                    Xf(p[4], p[5], out double ex2, out double ey2);
                    pb.CubicTo((float)PngSx(c1x), (float)PngSy(c1y), (float)PngSx(c2x), (float)PngSy(c2y),
                        (float)PngSx(ex2), (float)PngSy(ey2)); break;
            }
        }
        foreach (var pl in pb.ToPolylines(false))
            if (pl.Count >= 2) _rc.StrokePolyline(pl, false, widthPx, colRgb);
    }

    #endregion

    #region ===== 文字（TEXT / MText：基线=插入点，旋转/倾斜/宽度因子）=====

    // MTEXT → PNG（EmitMTextSvg 的位图版：逐 run 定位，同一局部坐标系→世界→画布映射）
    private void EmitMTextPng(MText mt, Transform2D tr, double scaleMul, short layerAci, short byBlockAci, string styleFont)
    {
        string baseCol = ColorOf(EntityAciIndex(mt), layerAci, byBlockAci);
        string styleBig = GetStyleBigFont(mt.Style);
        // \pxsm 段落行距倍数同样走扩展字典缓存提取（与 SVG 端一致；组码 44 常保持 1.0）：
        // 此前 PDF/PNG 未传 paraProps → 多倍行距丢失、整块偏矮（AutoCAD 图明显行距更大）。
        var layout = WrapMText(ParseMText(mt.Value ?? "", mt.Height, styleFont, styleBig, MTextCacheParaProps(mt)), mt.RectangleWidth, styleFont, styleBig);
        if (layout.Lines.Count == 0) return;
        var (baselines, anchorX, anchorY) = MTextLocalLayout(layout, mt, styleFont, styleBig);

        double rot = mt.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0);
        double rad = rot * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        double px = mt.InsertPoint.X, py = mt.InsertPoint.Y;
        if (tr != null) { var ap = tr.Apply(px, py); px = ap.Item1; py = ap.Item2; }

        for (int li = 0; li < layout.Lines.Count; li++)
        {
            // 行起点 = 段落首行缩进 − 首字左侧留白：AutoCAD 以【墨迹左缘】对齐左边界（插入点），
            // 而非字形 advance 占位盒左界（参考图实测：每行墨迹左缘距插入点≈0，我们旧版右偏 0.44 单位）。
            // 左对齐：墨迹左缘对齐文本框左缘；居中/右对齐：每行在文本框内按附件点水平定位
            // （多行 MTEXT 的短行必须各自居中/右对齐，而非都贴在文本框左缘——合并单元格多行居中修复）。
            bool leftAligned = layout.Lines[li].InkLeft > 0 &&
                (mt.AttachmentPoint == AttachmentPointType.TopLeft
                 || mt.AttachmentPoint == AttachmentPointType.MiddleLeft
                 || mt.AttachmentPoint == AttachmentPointType.BottomLeft);
            bool centerAligned = mt.AttachmentPoint == AttachmentPointType.TopCenter
                || mt.AttachmentPoint == AttachmentPointType.MiddleCenter
                || mt.AttachmentPoint == AttachmentPointType.BottomCenter;
            bool rightAligned = mt.AttachmentPoint == AttachmentPointType.TopRight
                || mt.AttachmentPoint == AttachmentPointType.MiddleRight
                || mt.AttachmentPoint == AttachmentPointType.BottomRight;
            double lineX = layout.Lines[li].FirstIndent;
            if (centerAligned) lineX += (layout.MaxLineWidth - layout.Lines[li].Width) / 2;
            else if (rightAligned) lineX += layout.MaxLineWidth - layout.Lines[li].Width;
            double x = (lineX - (leftAligned ? layout.Lines[li].InkLeft : 0.0)) * scaleMul;
            foreach (var run in layout.Lines[li].Runs)
            {
                double rw = MTextRunWidth(run, styleFont, styleBig) * scaleMul;
                if (run.IsFraction)
                {
                    // \S<num>/<den> 分数：分子/分母上下分层 + 中间分数线（与 SVG 端一致）
                    string col = run.Color.HasValue ? AciColor(run.Color.Value) : baseCol;
                    string fracFont = !string.IsNullOrEmpty(run.Font) ? run.Font : styleFont;
                    string fam = ResolveFontFamily(fracFont);
                    double fracCap = TtfCapScale(fam);
                    double fs = Math.Max(run.Height * scaleMul * fracCap, 0.2);
                    int sl = run.Text.IndexOf('/');
                    string num = run.Text.Substring(0, sl);
                    string den = sl + 1 < run.Text.Length ? run.Text.Substring(sl + 1) : "";
                    double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;
                    double numW = EstimateTextWidth(num, run.Height) * wf * scaleMul * fracCap;
                    double denW = EstimateTextWidth(den, run.Height) * wf * scaleMul * fracCap;
                    double fracW = Math.Max(numW, denW);
                    // 分数堆叠几何（CAD y 向上，相对主基线；与 SVG 端同一套 AutoCAD 实测值）
                    double H = run.Height;
                    double numOff = 0.72 * H;   // 分子基线：主基线上方
                    double denOff = 0.30 * H;   // 分母基线：主基线下方
                    double barOff = 0.58 * H;   // 分数线：主基线上方
                    double ob = EffOblique(run);
                    double numX = x + (fracW - numW) / 2.0;
                    double denX = x + (fracW - denW) / 2.0;
                    void LocToWorld(double lx, double ly, out double wx, out double wy)
                    {
                        double dx = (lx - anchorX) * scaleMul, dy = (ly - anchorY) * scaleMul;
                        wx = px + dx * ca - dy * sa; wy = py + dx * sa + dy * ca;
                    }
                    if (!string.IsNullOrEmpty(num))
                    {
                        LocToWorld(numX, baselines[li] + numOff, out double wnx, out double wny);
                        DrawTextPng(wnx, wny, num, fs, col, -rot, fam, run.WidthFactor, run.Bold, run.Italic, ob, false, false, numW);
                    }
                    if (!string.IsNullOrEmpty(den))
                    {
                        LocToWorld(denX, baselines[li] - denOff, out double wdx, out double wdy);
                        DrawTextPng(wdx, wdy, den, fs, col, -rot, fam, run.WidthFactor, run.Bold, run.Italic, ob, false, false, denW);
                    }
                    if (_rc != null && fracW > 1e-6)
                    {
                        LocToWorld(x, baselines[li] + barOff, out double h1x, out double h1y);
                        LocToWorld(x + fracW, baselines[li] + barOff, out double h2x, out double h2y);
                        var pp = new List<RasterCanvas.P>
                        {
                            new RasterCanvas.P((float)PngSx(h1x), (float)PngSy(h1y)),
                            new RasterCanvas.P((float)PngSx(h2x), (float)PngSy(h2y))
                        };
                        _rc.StrokePolyline(pp, false, (float)Math.Max(1.0, fs * PngK * 0.04), HexToRgb(col));
                    }
                }
                else if (!string.IsNullOrEmpty(run.Text))
                {
                    string col = run.Color.HasValue ? AciColor(run.Color.Value) : baseCol;
                    double fs = Math.Max(run.Height * scaleMul, 0.2);
                    string effFont = !string.IsNullOrEmpty(run.Font) ? run.Font : styleFont;
                    string effBig = string.IsNullOrEmpty(run.Font) ? styleBig : null;
                    double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;
                    double baseLineY = baselines[li] + run.BaselineShift * run.Height;
                    double dx = (x - anchorX) * scaleMul;
                    // \S 堆叠：基线按 BaselineShift（em）上下移（与 SVG 端一致）
                    double dy = (baseLineY - anchorY) * scaleMul;
                    double ox = px + dx * ca - dy * sa;
                    double oy = py + dx * sa + dy * ca;
                    // SHX 笔迹优先（simplex.shx 等）：真实 SHX 字形描边，字高随 em 自然（不再经 SimSun 压缩→偏小）
                    if (TryBuildMTextShxLocal(effFont, effBig, run.Text, run.Height, wf, out List<int> st, out List<double[]> sp, out _))
                    {
                        DrawShxRunPng(ox, oy, st, sp, run.Height, wf, fs, -rot, EffOblique(run), col);
                    }
                    else
                    {
                        // 回退 TTF（含 CJK 无 bigfont / 内联 TTF / 字形缺失）→ DrawTextPng
                        string fam = ResolveFontFamily(effFont);
                        // TTF cap-height 字号映射（Phase 2）：AutoCAD 把 H 当 cap height，非 em。
                        // 与单行 TTF（line 510）及宽度估算 TtfMTextWidth 一致——此前 MText TTF 未套
                        // cap-scale，字形比单行/AutoCAD 小 ~28%（"多行富文本字体不一致"）。SHX 分支不受影响。
                        double ttfFs = Math.Max(run.Height * scaleMul * TtfCapScale(fam), 0.2);
                        DrawTextPng(ox, oy, run.Text, ttfFs, col, -rot, fam, wf,
                            run.Bold, run.Italic, EffOblique(run), run.Underline, run.Overline, rw, run.Tracking);
                    }
                }
                x += rw;
            }
        }
    }

    // 基线文字绘制（画布坐标系）。
    //   (x,y)        = 基线左端点（数据坐标，TextBaseline/MText 已算好）
    //   fsData       = 字高（数据单位，含 scaleMul）
    //   rotCanvas    = 画布系旋转角（度；数据系 CCW 角 θ 传 −θ：画布 y 向下，正角=视觉顺时针）
    //   widthFactor  = 字宽因子（沿文字自身基线方向压缩，先缩后转）
    //   oblique      = 倾斜角（度，CAD \Q 语义：正=上端右倾）
    // 字体解析：PdfFontManager.FindTtf（族名→TTF/TTC 文件）→ TrueTypeFont 取字形轮廓，纯托管填充。
    private void DrawTextPng(double x, double y, string content, double fsData, string colHex, double rotCanvas,
        string family, double widthFactor, bool bold = false, bool italic = false, double oblique = 0.0,
        bool underline = false, bool overline = false, double runWidthData = 0.0, double tracking = 1.0)
    {
        if (string.IsNullOrEmpty(content) || _rc == null) return;
        if (PerfOn) _perfTextChars += content.Length;   // 2026-10-31：原声明后从未累加（恒 0），此处补上
        double sx = PngSx(x), sy = PngSy(y);
        float fs = (float)(fsData * PngK);
        if (fs <= 0) return;

        var fr = PickFont(family, content);
        if (fr == null) return; // 无任何可用 TTF → 跳过（不劣化）

        double upm = fr.UnitsPerEm;
        double wf = widthFactor > 1e-9 ? widthFactor : 1.0;
        double tanA = Math.Tan(-oblique * Math.PI / 180.0); // SVG skewX(−oblique)
        double th = rotCanvas * Math.PI / 180.0;
        double cosT = Math.Cos(th), sinT = Math.Sin(th);
        var col = HexToRgb(colHex);

        double penAdv = 0; // 累计 advance（em 单位，未乘 wf）
        for (int ci = 0; ci < content.Length; ci++)
        {
            char ch = content[ci];
            int gi = fr.GetGlyphIndex(ch);
            if (gi != 0)
            {
                var sink = new GlyphSink();
                // gi 是字形索引（glyph index），必须显式 (ushort) 命中 TryGetGlyph(ushort) 重载。
                // 若传 int 会选中 TryGetGlyph(int codepoint) 重载，内部把 gi 当 Unicode 码点再查一次
                // cmap → 取错字形（多个字符 gi 作码点映射到同一/默认 glyph）→ 文字乱码/竖条纹。
                fr.TryGetGlyph((ushort)gi, sink);
                // 同一字形的所有轮廓（外环 + 字怀内环）收集后【一次性】Fill(evenOdd=多轮廓)：
                // 逐轮廓单独 Fill(evenOdd:false) 会把字怀内环也独立填实 → 中间空洞粘连（O/A/0/8/9/6 的孔被堵）。
                // 奇偶规则下内环挖空，与 HATCH 实心路径（evenOdd = contours.Count>1）一致。
                var glyphContours = new List<List<RasterCanvas.P>>();
                foreach (var contour in sink.Contours)
                {
                    if (contour.Count < 3) continue;
                    var px = new List<RasterCanvas.P>(contour.Count + 1);
                    foreach (var fp in contour)
                    {
                        double ex = fp.X / upm + penAdv;
                        double ey = fp.Y / upm;
                        double Lx = ex * fs * wf;
                        double Ly = -ey * fs;          // 翻转 y 到屏幕向下
                        Lx += tanA * Ly;              // 倾斜
                        double Xr = Lx * cosT - Ly * sinT; // 旋转
                        double Yr = Lx * sinT + Ly * cosT;
                        px.Add(new RasterCanvas.P((float)(sx + Xr), (float)(sy + Yr)));
                    }
                    if (px.Count >= 3)
                    {
                        px.Add(px[0]); // 闭合轮廓
                        glyphContours.Add(px);
                    }
                }
                if (glyphContours.Count > 0)
                    _rc.Fill(glyphContours, glyphContours.Count > 1, col);
            }
            // (int) 强转强制走 GetAdvanceWidth(int codepoint) 重载：传 char 时 C# 优先绑定
            // GetAdvanceWidth(ushort glyphIndex)，把码点当字形索引 → 中文（U+4E00+）越界返回 0，
            // penAdv 不推进 → 后续字符叠上前字符（"文字重叠"根因）。英文码点小恰好未暴露。
            double adv = fr.GetAdvanceWidth((int)ch) / upm;
            // penAdv 只累加原始 advance（em），不再乘 wf：
            // Lx=(fp.X/upm+penAdv)×fs×wf 已统一乘一次 wf——若 penAdv 再乘 wf 会变成 wf²，
            // 使 \W 宽字距文本第 2 字起被右推（advance 放大 wf 倍），中文出现异常空隙。
            // \T 字距：每字符附加 (因子-1)×0.1em ÷ wf（em 单位，最终像素字距独立于 \W，与 SVG letter-spacing / PDF Tc 一致）
            penAdv += adv + (tracking - 1.0) * 0.1 / (wf > 1e-9 ? wf : 1.0);
        }

        // 下/上划线：沿文字方向（旋转后的局部 x 轴），长度 = run 宽（数据单位 × PngK）
        if ((underline || overline) && runWidthData > 0)
        {
            float oy = underline ? (float)(0.10 * fs) : (float)(-0.85 * fs);
            double dirx = cosT, diry = sinT;
            double offx = -oy * sinT, offy = oy * cosT;
            double len = runWidthData * PngK;
            double x0 = sx + offx, y0 = sy + offy;
            double x1 = x0 + dirx * len, y1 = y0 + diry * len;
            float wline = (float)Math.Max(1f, fs * 0.05f);
            var pp = new List<RasterCanvas.P> { new RasterCanvas.P((float)x0, (float)y0), new RasterCanvas.P((float)x1, (float)y1) };
            _rc.StrokePolyline(pp, false, wline, col);
        }
    }

    // SHX 笔迹 → 画布描边（与 DrawTextPng 同一屏幕变换：wf 压缩 / tanA 倾斜 / rotCanvas 旋转）。
    // types/pts = TryBuildMTextShxLocal 输出（基线原点、数据单位、y 上，不含 wf）；H=字高（数据单位，1 em）。
    // 用于 MTEXT simplex.shx 等 SHX 基础样式：真实 SHX 字形描边，字高随 em 自然（不再经 SimSun 压缩→偏小）。
    private void DrawShxRunPng(double ox, double oy, List<int> types, List<double[]> pts, double H, double wf,
        double fsData, double rotCanvas, double oblique, string colHex)
    {
        if (_rc == null || pts.Count == 0) return;
        float originSx = (float)PngSx(ox), originSy = (float)PngSy(oy);
        float fs = (float)(fsData * PngK);
        if (fs <= 0) return;
        double wf2 = wf > 1e-9 ? wf : 1.0;
        double tanA = Math.Tan(-oblique * Math.PI / 180.0); // 与 DrawTextPng 同（skewX(−oblique)）
        double th = rotCanvas * Math.PI / 180.0;
        double cosT = Math.Cos(th), sinT = Math.Sin(th);
        var col = HexToRgb(colHex);
        float penW = (float)Math.Max(fs * 0.05, 0.2);

        // 局部点（基线原点、数据单位、y 上）→ 画布像素（与 DrawTextPng 同：ex_em=lx/H，ey_em=ly/H）
        void ToScr(double lx, double ly, out float X, out float Y)
        {
            double ex = lx / H, ey = ly / H;
            double Lx = ex * fs * wf2;
            double Ly = -ey * fs;
            Lx += tanA * Ly; // 倾斜（SVG skewX(−oblique) 经 Y 翻转等价）
            double Xr = Lx * cosT - Ly * sinT;
            double Yr = Lx * sinT + Ly * cosT;
            X = originSx + (float)Xr;
            Y = originSy + (float)Yr;
        }

        var cur = new List<RasterCanvas.P>();
        for (int k = 0; k < types.Count; k++)
        {
            var p = pts[k];
            switch (types[k])
            {
                case 0: // Move：抬笔 → 新子路径
                    if (cur.Count >= 2) _rc.StrokePolyline(cur, false, penW, col);
                    ToScr(p[0], p[1], out float mx, out float my);
                    cur = new List<RasterCanvas.P> { new RasterCanvas.P(mx, my) };
                    break;
                case 1: // Line
                    ToScr(p[0], p[1], out float lx, out float ly);
                    cur.Add(new RasterCanvas.P(lx, ly));
                    break;
                case 2: // Quad（控制, 终点）
                    ToScr(p[0], p[1], out float qcx, out float qcy);
                    ToScr(p[2], p[3], out float qex, out float qey);
                    if (cur.Count > 0)
                    {
                        var s = cur[cur.Count - 1];
                        RasterCanvas.FlattenQuad(s.X, s.Y, qcx, qcy, qex, qey, cur);
                    }
                    break;
                case 3: // Cubic（控制1, 控制2, 终点）
                    ToScr(p[0], p[1], out float c1x, out float c1y);
                    ToScr(p[2], p[3], out float c2x, out float c2y);
                    ToScr(p[4], p[5], out float e1x, out float e1y);
                    if (cur.Count > 0)
                    {
                        var s = cur[cur.Count - 1];
                        RasterCanvas.FlattenCubic(s.X, s.Y, c1x, c1y, c2x, c2y, e1x, e1y, cur);
                    }
                    break;
            }
        }
        if (cur.Count >= 2) _rc.StrokePolyline(cur, false, penW, col);
    }

    // 族名 → 可用 TrueTypeFont（纯托管）。先按 family（CSS 列表支持）尝试，含 CJK 时追加常用 CJK 兜底，
    // 选取能覆盖文本全部码位的字体；均不可用返回 null（调用方跳过文字）。
    private static readonly Dictionary<string, TrueTypeFont> _ttfCache = new(StringComparer.OrdinalIgnoreCase);
    private static TrueTypeFont PickFont(string family, string text)
    {
        var fams = new List<string>();
        if (!string.IsNullOrEmpty(family))
            foreach (var p in family.Split(','))
            {
                string f = p.Trim();
                if (f.Length == 0 || f == "serif" || f == "sans-serif" || f == "monospace") continue;
                fams.Add(f);
            }
        if (fams.Count == 0) fams.Add("Microsoft YaHei");
        // 常用 CJK 兜底（覆盖校验失败回退）
        foreach (var c in new[] { "Microsoft YaHei", "SimSun", "SimHei", "DengXian", "KaiTi", "FangSong" })
            if (!fams.Contains(c)) fams.Add(c);

        TrueTypeFont first = null;
        foreach (var f in fams)
        {
            if (_ttfCache.TryGetValue(f, out var cached))
            {
                if (cached == null) continue;        // 已知缺失
                if (Covers(cached, text)) return cached;
                if (first == null) first = cached;
                continue;
            }
            string path = PdfFontManager.FindTtf(f);
            if (string.IsNullOrEmpty(path)) { _ttfCache[f] = null; continue; }
            var tf = TrueTypeFont.LoadFromFile(path);
            if (tf == null) { _ttfCache[f] = null; continue; }
            _ttfCache[f] = tf;
            if (Covers(tf, text)) return tf;
            if (first == null) first = tf;
        }
        return first;
    }

    private static bool Covers(TrueTypeFont tf, string text)
    {
        foreach (var ch in text)
        {
            if (ch < 0x20) continue;
            if (tf.GetGlyphIndex(ch) == 0) return false;
        }
        return true;
    }

    // IGlyphSink 收集器：把字形轮廓命令收为若干闭合轮廓（字体单位）。
    private sealed class GlyphSink : IGlyphSink
    {
        public readonly List<List<RasterCanvas.P>> Contours = new();
        private List<RasterCanvas.P> _cur;
        public void MoveTo(float x, float y) { _cur = new List<RasterCanvas.P>(); Contours.Add(_cur); _cur.Add(new RasterCanvas.P(x, y)); }
        public void LineTo(float x, float y) { _cur?.Add(new RasterCanvas.P(x, y)); }
        public void QuadTo(float cx, float cy, float x, float y)
        {
            if (_cur == null || _cur.Count == 0) return;
            var s = _cur[_cur.Count - 1];
            RasterCanvas.FlattenQuad(s.X, s.Y, cx, cy, x, y, _cur);
        }
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            if (_cur == null || _cur.Count == 0) return;
            var s = _cur[_cur.Count - 1];
            RasterCanvas.FlattenCubic(s.X, s.Y, c1x, c1y, c2x, c2y, x, y, _cur);
        }
        public void Close() { /* 字形轮廓本就闭合，无需额外操作 */ }
    }

    #endregion

    #region ===== 填充（四边形 / HATCH 实心/渐变/图案）=====

    // 四边形填充（Solid/Face3D；填充 + 0.15 宽同色描边防 AA 缝隙）
    private void EmitQuadFillPng(List<(double X, double Y)> pts, string color)
    {
        if (pts == null || pts.Count < 3 || _rc == null) return;
        var contour = new List<RasterCanvas.P>(pts.Count);
        foreach (var p in pts) contour.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
        var col = HexToRgb(color);
        _rc.Fill(new List<List<RasterCanvas.P>> { contour }, false, col);
        float w = (float)Math.Max(0.05, 0.15 * PngK);
        _rc.StrokePolyline(contour, true, w, col);
    }

    // HATCH → PNG（实心=实体色/真色；渐变=线性/径向双端插值；图案=裁剪线段描边）
    private void EmitHatchPng(Hatch ht, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var loops = BuildHatchLoops(ht, tr);
        if (loops.Count == 0) { RecordSkip("HATCH(空)"); return; }
        var gp = ht.GradientColor;
        bool gradient = gp != null && gp.Enabled;
        if (!gradient && !IsSolidPattern(ht))
        {
            EmitHatchPatternPng(ht, loops, tr, scaleMul, layerAci, byBlockAci);
            return;
        }
        var contours = new List<List<RasterCanvas.P>>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            var c = new List<RasterCanvas.P>(loop.Count);
            foreach (var p in loop)
            {
                float fx = (float)PngSx(p.X), fy = (float)PngSy(p.Y);
                c.Add(new RasterCanvas.P(fx, fy));
                if (fx < minX) minX = fx; if (fy < minY) minY = fy;
                if (fx > maxX) maxX = fx; if (fy > maxY) maxY = fy;
            }
            contours.Add(c);
        }
        if (contours.Count == 0) { RecordSkip("HATCH(空)"); return; }
        bool evenOdd = contours.Count > 1;
        if (gradient)
        {
            // 接入共享 9 类型 LUT 模型：minX/Y 已是像素单位（PngSx/PngSy），画布系 Y 翻转→uy 取反、yDown=true。
            var spec = MakeHatchGradSpec(gp);
            double ang = gp.Angle;
            double ux = Math.Cos(ang), uy = -Math.Sin(ang);
            var geom = ComputeHatchGradGeom(spec, minX, minY, maxX, maxY, ux, uy, true);
            var c1 = HexToRgb(StopHex(spec.C1));
            var c2 = HexToRgb(StopHex(spec.C2));
            var prof = new Func<float, float>(u => (float)HatchGradProfileLut(spec, u));
            if (geom.Radial)
                _rc.FillGradientProfile(contours, evenOdd, new RasterCanvas.P(0, 0), new RasterCanvas.P(0, 0), c1, c2, true, (float)geom.Fx, (float)geom.Fy, (float)geom.R, prof);
            else
                _rc.FillGradientProfile(contours, evenOdd,
                    new RasterCanvas.P((float)geom.Px0, (float)geom.Py0),
                    new RasterCanvas.P((float)geom.Px1, (float)geom.Py1),
                    c1, c2, false, 0, 0, 0, prof);
        }
        else
        {
            _rc.Fill(contours, evenOdd, HexToRgb(HatchSolidFillHex(ht, layerAci, byBlockAci)));
        }
    }

    // 图案 HATCH（EmitHatchPatternSvg 的位图版：BuildPatternSegments 共享几何 → 描边）
    private void EmitHatchPatternPng(Hatch ht, List<List<XY>> loops, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var segs = BuildPatternSegments(loops, ht, tr, MaxPatternLinesPerHatch);
        if (segs.Count == 0) { RecordSkip("HATCH(图案)"); return; }
        string col = HatchSolidFillHex(ht, layerAci, byBlockAci);
        // 图案线宽为屏幕空间常量：不乘块缩放 scaleMul（与显示线宽同语义，2026-10-29 用户裁定）；
        // 块/图案缩放只影响图案【间距】（BuildPatternSegments 走 tr 世界坐标）。
        double w = 0.1;
        var colRgb = HexToRgb(col);
        // 保底 1px：图案线在低分辨率下 w*PngK 可能 <1px，亚像素描边带落在两行像素采样线之间会被整段丢弃
        float widthPx = (float)Math.Max(1.0, w * PngK);
        foreach (var s in segs)
        {
            var pp = new List<RasterCanvas.P> {
                new RasterCanvas.P((float)PngSx(s.X1), (float)PngSy(s.Y1)),
                new RasterCanvas.P((float)PngSx(s.X2), (float)PngSy(s.Y2)) };
            _rc.StrokePolyline(pp, false, widthPx, colRgb);
        }
    }

    #endregion

    #region ===== 变宽折线（填充多边形，EmitTaperedPolyline 的位图版）=====

    private void EmitTaperedPng(LwPolyline pl, Transform2D tr, string stroke, double scaleMul)
    {
        var verts = pl.Vertices;
        int n = verts.Count;
        if (n < 2 || _rc == null) return;
        // 逐顶点有效宽度：无 bulge 弦偏移路径用（顶点 i 宽 = 起宽40，为0时继承入段终宽41）
        double[] w = TaperedVertexWidths(pl);
        // 逐段起/终点宽：含 bulge 弧偏移路径用（段 i 从 40_i 渐变到 41_i，段间顶点收尖）
        var (sw, ew) = TaperedSegmentWidths(pl);

        List<(double X, double Y)> left, right;
        var taper = TryBuildTaperOffset(pl, sw, ew, tr, _opt.TaperOffsetBuilder);
        if (taper.Ok)
        {
            left = taper.Left; right = taper.Right;
        }
        else
        {
            // 无 bulge：逐顶点弦偏移（与 SVG 端逐行同式）
            left = new List<(double X, double Y)>(n);
            right = new List<(double X, double Y)>(n);
            for (int i = 0; i < n; i++)
            {
                double px = verts[i].Location.X, py = verts[i].Location.Y;
                int ni = pl.IsClosed ? (i + 1) % n : (i + 1 < n ? i + 1 : i);
                int pi = pl.IsClosed ? (i - 1 + n) % n : (i - 1 >= 0 ? i - 1 : i);
                double fx = verts[ni].Location.X - px, fy = verts[ni].Location.Y - py;
                double bx = px - verts[pi].Location.X, by = py - verts[pi].Location.Y;
                double fl = Math.Sqrt(fx * fx + fy * fy); if (fl < 1e-9) { fx = 1; fy = 0; } else { fx /= fl; fy /= fl; }
                double bl = Math.Sqrt(bx * bx + by * by); if (bl < 1e-9) { bx = 1; by = 0; } else { bx /= bl; by /= bl; }
                double tx = fx + bx, ty = fy + by;
                double tl = Math.Sqrt(tx * tx + ty * ty); if (tl < 1e-9) { tx = fx; ty = fy; } else { tx /= tl; ty /= tl; }
                double nx = -ty, ny = tx;
                double hw = w[i] / 2.0;
                double lx = px + nx * hw, ly = py + ny * hw;
                double rx = px - nx * hw, ry = py - ny * hw;
                if (tr != null) { var a = tr.Apply(lx, ly); left.Add((a.Item1, a.Item2)); var b = tr.Apply(rx, ry); right.Add((b.Item1, b.Item2)); }
                else { left.Add((lx, ly)); right.Add((rx, ry)); }
            }
        }

        var col = HexToRgb(stroke);
        var contours = new List<List<RasterCanvas.P>>();
        int m = left.Count;
        if (m < 2) return;
        if (pl.IsClosed)
        {
            // 闭合：left/right 各一条封闭偏移轮廓（内/外环），even-odd 填充得"环"
            var cl = new List<RasterCanvas.P>(m); foreach (var p in left) cl.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
            var cr = new List<RasterCanvas.P>(m); foreach (var p in right) cr.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
            contours.Add(cl); contours.Add(cr);
            _rc.Fill(contours, true, col);
            float wd = (float)Math.Max(0.05, 0.1 * PngK);
            _rc.StrokePolyline(cl, true, wd, col);
            _rc.StrokePolyline(cr, true, wd, col);
        }
        else
        {
            // 开放：left 正向 + right 反向，两端闭合填充（带/楔形）
            var c = new List<RasterCanvas.P>(2 * m);
            foreach (var p in left) c.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
            for (int i = m - 1; i >= 0; i--) c.Add(new RasterCanvas.P((float)PngSx(right[i].X), (float)PngSy(right[i].Y)));
            contours.Add(c);
            _rc.Fill(contours, false, col);
            float wd = (float)Math.Max(0.05, 0.1 * PngK);
            _rc.StrokePolyline(c, true, wd, col);
        }
    }

    #endregion
}
