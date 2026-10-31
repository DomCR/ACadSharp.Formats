using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ACadSharp.Formats.Pdf;

namespace ACadSharp.Formats.Image;

/// <summary>图像引擎 PDF 部分（与 FullImageEngine.Svg.cs 中的 SVG 部分共享同一 partial 类）。</summary>
internal partial class ImageEngine
{
    // ===== PDF 状态 =====
    private PdfFontManager _pdfFonts;
    private Dictionary<int, (int ShadingType, string Coords, string C0, string C1)> _pdfShadings;
    private int _pdfShadingSeq;
    private const int MaxPdfShadings = 64;

    // ---- PDF 图片 / Form XObject 资源（内容流阶段登记，组装阶段写对象并注册到 Page /Resources /XObject）----
    private sealed class PdfXObjReg
    {
        public string Name;                                  // /Im0
        public Func<int, List<(int num, byte[] body)>> Build; // 输入此 XObject 基础对象号，返回所有待写对象（[0] 为 XObject 自身）
    }
    private List<PdfXObjReg> _pdfXObjRegs;
    private int _pdfXObjSeq;
    /// <summary>源文档（DWG/DXF）路径，用于解析 IMAGE/PDFUNDERLAY 的相对文件路径。</summary>
    public string SourceFilePath { get; set; }
    private string _srcDir;

    #region ===== 导出 PDF =====
    public ImageExportResult WritePdf(string outPath)
    {
        try
        {
            if (!ComputeExtents())
            {
                _log("⚠️  PDF 无可用实体，跳过");
                return new ImageExportResult { Success = false };
            }

            _scale = 1.0;
            double dataW = _maxX - _minX, dataH = _maxY - _minY;
            _margin = Math.Max(dataW, dataH) * 0.02 + 1.0;
            if (!double.IsFinite(_margin) || _margin <= 0) _margin = 2.0;
            _w = dataW + 2 * _margin;
            _h = dataH + 2 * _margin;
            ExpandViewForPointGlyphs(); // 点样式字形（r 可达视高 5%）四边扩界，避免边缘点被裁

            // 加载字体管理（默认 SimHei + 按样式/内联 \f 按需加载其他 TTF，分别内嵌）
            _pdfFonts = new PdfFontManager(_log);
            BuildLinetypeDash();

            // HATCH 渐变 shading 状态（对象号区从字体区之后顺排；内容流生成时逐个登记）
            _pdfShadings = new Dictionary<int, (int ShadingType, string Coords, string C0, string C1)>();
            _pdfShadingSeq = 0;
            _pdfXObjRegs = new List<PdfXObjReg>();
            _pdfXObjSeq = 0;
            _skippedCounts.Clear(); // P2：每格式单独计数，日志不跨格式累计
            _srcDir = string.IsNullOrEmpty(SourceFilePath) ? null : Path.GetDirectoryName(Path.GetFullPath(SourceFilePath));

            // 1) 构建内容流（字节级：文字为 2 字节 GID，可能含任意二进制字节）
            var cs = new MemoryStream();
            // 输出放大：内容流前置 CTM，几何/线宽/虚线/文字/HATCH 渐变统一 ×OutputZoom
            CsAscii(cs, F(_opt.OutputZoom) + " 0 0 " + F(_opt.OutputZoom) + " 0 0 cm\n");
            // 背景（默认黑色；WhiteBackground=true 时白色；TransparentBackground=true 时省略背景填充=透明页）
            if (!_transparentBg)
                CsAscii(cs, (_whiteBg ? "1 1 1" : "0 0 0") + " rg\n0 0 " + F(_w) + " " + F(_h) + " re f\n");
            foreach (var ent in AllEntities())
                EmitEntityPdf(cs, ent, null, 1.0, 7, null, null, _ltScale); // 顶层无 INSERT 上下文，ByBlock 兜底白/实线
            byte[] content = cs.ToArray();

            // R20b：字体对象/资源表必须在内容流生成【之后】定稿——字体（如宋体 SimSun）是内容流生成
            // 过程中经 GetSlot 按需加载的，提前按 _pdfFonts.Count 定稿会漏掉它们：内容流引用了 /F2，
            // 但 /Resources 与字体对象区只写了 /F1 → 查看器代用 Latin 字体逐字节渲染 SimSun GID
            // → 中文乱码（test1234.pdf 图框）。对象号分配规则不变：
            // 每个字体占 5 个对象（Type0/CIDFontType2/FontDescriptor/ToUnicode/FontFile2），
            // 槽位 i（1-based）→ 对象号 5 + (i-1)*5；shading 区紧随字体区之后。
            int numFonts = _pdfFonts.HasFont ? _pdfFonts.Count : 1;
            var fontRes = new StringBuilder();
            for (int i = 1; i <= numFonts; i++)
                fontRes.Append("/F" + i + " " + (5 + (i - 1) * 5) + " 0 R ");
            string fontResStr = fontRes.ToString().Trim();
            int shadingBase = _pdfFonts.HasFont ? (5 + numFonts * 5) : 6;

            // 2) 组装 PDF 文件（记录每个对象真实偏移，生成合法 xref/startxref）
            var file = new MemoryStream();
            var offsets = new Dictionary<int, long>();
            void PutAscii(string s)
            {
                var b = Encoding.ASCII.GetBytes(s);
                file.Write(b, 0, b.Length);
            }
            void PutObj(int num, byte[] body)
            {
                offsets[num] = file.Position;
                PutAscii(num + " 0 obj\n");
                file.Write(body, 0, body.Length);
                PutAscii("\nendobj\n");
            }

            PutAscii("%PDF-1.4\n");
            var binHeader = new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }; // 高字节标记（推荐，提示查看器内容为二进制）
            file.Write(binHeader, 0, binHeader.Length);

            // HATCH 渐变 shading 对象（在对象 3 之前写出，供 Page /Shading 资源引用）。
            // /Function 用【单个 1-in-3-out 的 Type 2 函数】：C0/C1 是 3 元素数组（每个颜色分量各一值，
            // ISO 32000-1 Table 11 "An array of numbers specifying the minimum/maximum values of the
            // components"）。这是最标准、兼容性最好的写法（iText 及本仓库最小基准 minimal.pdf 均如此）。
            // 曾误判为"必须 3 个标量函数数组"→ 改成 /Function [R G B 三个标量函数]，Chromium 系查看器
            // 不支持该多函数数组形式 → 函数求值失败回退黑色填充（格子全黑）。回退为单函数数组 C0/C1。
            // 每个 shading 占 2 个对象：1 shading + 1 函数。
            var shadingRes = new StringBuilder();
            foreach (var kv in _pdfShadings.OrderBy(p => p.Key))
            {
                int shObj = shadingBase + kv.Key * 2;
                int fnObj = shObj + 1;
                shadingRes.Append("/Sh" + kv.Key + " " + shObj + " 0 R ");
                PutObj(shObj, Encoding.ASCII.GetBytes("<< /Type /Shading /ShadingType " + kv.Value.ShadingType +
                                                    " /ColorSpace /DeviceRGB /Coords [" + kv.Value.Coords + "] /Domain [0 1] /Extend [true true] /Function " + fnObj + " 0 R >>"));
                PutObj(fnObj, Encoding.ASCII.GetBytes("<< /FunctionType 2 /Domain [0 1] /C0 [" + kv.Value.C0 + "] /C1 [" + kv.Value.C1 + "] >>"));
            }

            // 图片 / Form XObject 资源（在 Page 对象之前写出，供 /Resources /XObject 引用）
            var xobjRes = new StringBuilder();
            int xoBase = shadingBase + _pdfShadings.Count * 2;
            int xo = xoBase;
            foreach (var reg in _pdfXObjRegs)
            {
                var objs = reg.Build(xo);
                for (int k = 0; k < objs.Count; k++) PutObj(objs[k].num, objs[k].body);
                xobjRes.Append("/" + reg.Name + " " + xo + " 0 R ");
                xo = objs[objs.Count - 1].num + 1;
            }

            // 对象 1 Catalog / 2 Pages / 3 Page
            PutObj(1, Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"));
            PutObj(2, Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
            PutObj(3, Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + F(_w * _opt.OutputZoom) + " " + F(_h * _opt.OutputZoom) +
                                             "] /Resources << /Font << " + fontResStr + " >>" +
                                              (shadingRes.Length > 0 ? " /Shading << " + shadingRes.ToString().Trim() + " >>" : "") +
                                              (xobjRes.Length > 0 ? " /XObject << " + xobjRes.ToString().Trim() + " >>" : "") +
                                              " >> /Contents 4 0 R >>"));

            // 对象 4 内容流
            {
                var head = Encoding.ASCII.GetBytes("<< /Length " + content.Length + " >>\nstream\n");
                var tail = Encoding.ASCII.GetBytes("\nendstream");
                var body = new byte[head.Length + content.Length + tail.Length];
                Buffer.BlockCopy(head, 0, body, 0, head.Length);
                Buffer.BlockCopy(content, 0, body, head.Length, content.Length);
                Buffer.BlockCopy(tail, 0, body, head.Length + content.Length, tail.Length);
                PutObj(4, body);
            }

            if (_pdfFonts.HasFont)
            {
                for (int i = 1; i <= numFonts; i++)
                {
                    CjkFont f = _pdfFonts.FontAt(i);
                    int type0 = 5 + (i - 1) * 5;
                    int cid = type0 + 1;
                    int desc = type0 + 2;
                    int touni = type0 + 3;
                    int file2 = type0 + 4;
                    // Type0 字体（Identity-H）
                    PutObj(type0, Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type0 /BaseFont /" + f.FontName +
                                                          " /Encoding /Identity-H /DescendantFonts [" + cid + " 0 R] /ToUnicode " + touni + " 0 R >>"));
                    // CIDFontType2（/W 显式给出所用 GID 字宽，避免查看器使用字体文件内损坏的 hmtx）
                    PutObj(cid, Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /" + f.FontName +
                                                          " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>" +
                                                          " /W [" + f.WidthArray() + "]" +
                                                          " /FontDescriptor " + desc + " 0 R /DW 1000 >>"));
                    // FontDescriptor
                    PutObj(desc, Encoding.ASCII.GetBytes(f.DescriptorDict(file2)));
                    // ToUnicode CMap（GID → Unicode，供文本提取/搜索）。
                    // 必须是合法流对象（<< /Length N >> stream ... endstream），否则查看器
                    // 无法解析 CMap，PDF 文本复制/搜索全部失效（渲染不受影响）。
                    {
                        byte[] cmap = CjkFont.BuildCMapStream(f.UsedGidToUnicode);
                        var head = Encoding.ASCII.GetBytes("<< /Length " + cmap.Length + " >>\nstream\n");
                        var tail = Encoding.ASCII.GetBytes("\nendstream");
                        var body = new byte[head.Length + cmap.Length + tail.Length];
                        Buffer.BlockCopy(head, 0, body, 0, head.Length);
                        Buffer.BlockCopy(cmap, 0, body, head.Length, cmap.Length);
                        Buffer.BlockCopy(tail, 0, body, head.Length + cmap.Length, tail.Length);
                        PutObj(touni, body);
                    }
                    // FontFile2（TTF 原始字节）
                    {
                        var head = Encoding.ASCII.GetBytes("<< /Length " + f.TtfBytes.Length + " /Length1 " + f.TtfBytes.Length + " >>\nstream\n");
                        var tail = Encoding.ASCII.GetBytes("\nendstream");
                        var body = new byte[head.Length + f.TtfBytes.Length + tail.Length];
                        Buffer.BlockCopy(head, 0, body, 0, head.Length);
                        Buffer.BlockCopy(f.TtfBytes, 0, body, head.Length, f.TtfBytes.Length);
                        Buffer.BlockCopy(tail, 0, body, head.Length + f.TtfBytes.Length, tail.Length);
                        PutObj(file2, body);
                    }
                }
            }
            else
            {
                // 无 CJK 字体：回退 Helvetica（仅 Latin-1 可显示，中文已在内容流中替换为 "?"）
                PutObj(5, Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
            }

            // xref + trailer + startxref（每条 xref 记录必须恰好 20 字节）
            // 对象号：1 Catalog / 2 Pages / 3 Page / 4 内容流 / 5..(4+5*numFonts) 字体 / 之后 = HATCH 渐变 shading
            int size = offsets.Count == 0 ? 1 : offsets.Max(o => o.Key) + 1;
            long xrefPos = file.Position;
            PutAscii("xref\n0 " + size + "\n");
            PutAscii("0000000000 65535 f \n");
            for (int i = 1; i < size; i++)
            {
                long off;
                if (offsets.TryGetValue(i, out off))
                    PutAscii(off.ToString("D10") + " 00000 n \n");
                else
                    PutAscii("0000000000 00000 f \n"); // 预留未用对象号（如未用渐变 shading 槽位）
            }
            PutAscii("trailer\n<< /Size " + size + " /Root 1 0 R >>\nstartxref\n" + xrefPos + "\n%%EOF\n");

            var pdfDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(pdfDir) && !Directory.Exists(pdfDir))
                Directory.CreateDirectory(pdfDir);
            File.WriteAllBytes(outPath, file.ToArray());
            string fontNote = _pdfFonts.HasFont
                ? "已内嵌 " + _pdfFonts.Count + " 个中文字体（" + string.Join(", ", _pdfFonts.UsedFontNames()) + "）"
                : "无中文字体（中文显示为?）";
            _log($"✅  PDF 已生成：{outPath}（{(_transparentBg ? "透明背景" : (_whiteBg ? "白底" : "黑底"))}，页面 {F(_w * _opt.OutputZoom)}×{F(_h * _opt.OutputZoom)} pt，1:1 内容放大 {F(_opt.OutputZoom)} 倍，{fontNote}）");
            LogSkipped("PDF");
            return new ImageExportResult
            {
                Success = true,
                CanvasPixelsW = (int)Math.Ceiling(_w * _opt.OutputZoom),
                CanvasPixelsH = (int)Math.Ceiling(_h * _opt.OutputZoom),
                PixelsPerUnit = _opt.OutputZoom,
                DataX0 = _minX - _margin,
                DataX1 = _maxX + _margin,
                DataY0 = _minY - _margin,
                DataY1 = _maxY + _margin,
            };
        }
        catch (Exception ex)
        {
            _log($"❌  PDF 导出异常：{ex.Message}");
            return new ImageExportResult { Success = false };
        }
    }

    // 发射单个实体（PDF 内容流）。参数语义同 EmitEntitySvg。
    private void EmitEntityPdf(MemoryStream cs, Entity ent, Transform2D tr, double scaleMul, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul, short? byLayerOverride = null)
    {
        // 块内 ByLayer 图元随"插入点块所在图层"（byLayerOverride），顶层用自身图层色
        short layerAci;
        if (byLayerOverride.HasValue) layerAci = byLayerOverride.Value;
        else { layerAci = 7; try { layerAci = LayerAciIndex(ent.Layer); } catch { } }
        string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
        double[] dash = ResolveDash(ent, byBlockDash, ltMul);
        LtpPattern pat = ResolvePattern(ent, byBlockPat, ltMul); // 复杂线型（含 SHX 段）→ 沿路径真实渲染

        switch (ent)
        {
            case Insert ins:
                if (ins?.Block != null)
                {
                    var t = Transform2D.Compose(ins, tr);
                    // 块内容的"随块"颜色/线型 = 该 INSERT 的有效值（独立 > 图层；嵌套 ByBlock 继承上层）
                    short insAci = EffectiveAci(ins, byBlockAci);
                    double[] insDash = ResolveDash(ins, byBlockDash, ltMul);
                    LtpPattern insPat = ResolvePattern(ins, byBlockPat, ltMul);
                    // 块内"随图层"图元 = 该 INSERT 所在图层色（CAD 语义）
                    short insLayerAci = 7;
                    try { insLayerAci = LayerAciIndex(ins.Layer); } catch { }
                    // 块内实体的线型比例额外乘块插入比例（与 SVG 路径一致）
                    double childLtMul = ltMul * BlockScaleOf(ins);
                    // 块内实体。ATTDEF（属性定义/模板，默认值）不在此画——它只是属性模板；
                    // 实际显示用 INSERT 跟随的 ATTRIB（ins.Attributes，本次插入的真实属性值，如 "17"）。
                    // 否则块定义里的默认值（如 "1"）会被当成文字画出来，覆盖真实属性值。
                    bool hasAttribs = ins.Attributes != null && ins.Attributes.Count > 0;
                    // [PATCH] 按规范化文件序遍历（FileOrderEntities 优先），消除 HashSet 哈希序漂移
                    foreach (var e in BlockEntitiesInFileOrder(ins.Block))
                    {
                        if (hasAttribs && e is AttributeDefinition) continue; // 有实际属性值 → 跳过模板默认值
                        EmitEntityPdf(cs, e, t, t.Scale, insAci, insDash, insPat, childLtMul, insLayerAci);
                    }
                    // 属性实际值（ATTRIB）：InsertPoint（组10）与 Height（组40）同一坐标系（顶层=世界/嵌套=父块局部），
                    // AutoCAD 存档时已把字高预乘插入比例。位置只受父层累积变换（tr），字高也按该同一变换缩放
                    // （tr.Scale），不再乘本 INSERT 全变换 t 的 t.Scale（会把已含插入比例的字高再放大一次）。
                    // 治 sample.dwg gc200（scale=10）属性 "3.94"：字高 20 被双重缩放开成 200，GT AutoCAD 按 20 渲染。
                    if (hasAttribs)
                        foreach (var att in ins.Attributes)
                            EmitEntityPdf(cs, att, tr, tr?.Scale ?? 1.0, insAci, insDash, insPat, childLtMul, insLayerAci);
                }
                break;

            case Line ln:
                {
                    var (x1, y1) = tr == null ? (ln.StartPoint.X, ln.StartPoint.Y) : tr.Apply(ln.StartPoint.X, ln.StartPoint.Y);
                    var (x2, y2) = tr == null ? (ln.EndPoint.X, ln.EndPoint.Y) : tr.Apply(ln.EndPoint.X, ln.EndPoint.Y);
                    double w = StrokeWidth(ent, scaleMul);
                    if (!TryEmitComplexPdf(cs, ent, tr, pat, col, w))
                        PdfStroke(cs, col, w, dash, F(Sx(x1)) + " " + F(Py(y1)) + " m " + F(Sx(x2)) + " " + F(Py(y2)) + " l S");
                }
                break;

            case LwPolyline pl:
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    // 变宽判断看**任意**顶点 StartWidth(40) 或 ConstantWidth(43)（首顶点窄端 sw=0 的纺锤形不能漏判）；
                    // EndWidth(41) 不单独触发（闭合圆环全 sw=0/ew=常数 应走描边+bulge 弧，否则变宽路径退化成 0 宽消失）。
                    // 常数宽(43)：全顶点等宽归一形式 → 物化为逐顶点
                    // [PATCH] Option B：仅当 ConstantWidthSpecified（源文件确有 43）才物化/走变宽
                    // （与 SVG/PNG 同口径；GDB2CAD 合成的 ConstantWidth 未设 Specified，读回侧无 43）。
                    if (pl.ConstantWidthSpecified && pl.ConstantWidth > 0 && !pl.Vertices.Any(v => v.StartWidth > 0))
                        foreach (var v in pl.Vertices) { v.StartWidth = pl.ConstantWidth; v.EndWidth = pl.ConstantWidth; }
                    // 变宽：任意顶点 起宽(40)/终宽(41) 非零 或 常数宽(43) 非零 → 走填充变宽路径；逐顶点有效宽度
                    // 由 TaperedVertexWidths 计算（正确处理"每节起点/终点线宽不等"，与 SVG/PNG 一致）。
                    bool hasTaper = pl.Vertices.Any(v => v.StartWidth > 0 || v.EndWidth > 0) || (pl.ConstantWidthSpecified && pl.ConstantWidth > 0);
                    if (hasTaper && pl.Vertices.Count >= 2)
                    {
                        PdfTaperedPolyline(cs, pl, tr, col, dash);
                        break;
                    }
                    // 普通描边路径采样世界折线点（含 bulge 弧段）；变宽路径走 PdfTaperedPolyline（自有 bulge 逻辑）
                    var pts = LwPolylineSamplePoints(pl, tr);
                    var d = new StringBuilder();
                    for (int i = 0; i < pts.Count; i++) d.Append(F(Sx(pts[i].X)) + " " + F(Py(pts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                    if (pl.IsClosed) d.Append("h ");
                    PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash, d + "S");
                }
                break;

            case Polyline3D p3:
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = p3.Vertices.Where(v => IsDisplayVertex(v)) // 跳过样条框架控制点（不显示）
                        .Select(v => tr == null ? (v.Location.X, v.Location.Y) : tr.Apply(v.Location.X, v.Location.Y)).ToList();
                    if (pts.Count >= 2)
                    {
                        var d = new StringBuilder();
                        for (int i = 0; i < pts.Count; i++) d.Append(F(Sx(pts[i].X)) + " " + F(Py(pts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                        if (p3.IsClosed) d.Append("h "); // 闭合 3D 折线补闭合段
                        PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash, d + "S");
                    }
                }
                break;

            case Polyline2D p2d:
                {
                    // 2D 多段线（老式 POLYLINE+VERTEX）：顶点折线折线（bulge 弧段暂按直线弦）
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts2d = new List<(double X, double Y)>(p2d.Vertices.Count);
                    foreach (var v in p2d.Vertices)
                    {
                        if (!IsDisplayVertex(v)) continue; // 跳过样条框架控制点（不显示）
                        if (tr == null) pts2d.Add((v.Location.X, v.Location.Y));
                        else { var a = tr.Apply(v.Location.X, v.Location.Y); pts2d.Add((a.Item1, a.Item2)); }
                    }
                    if (pts2d.Count >= 2)
                    {
                        var d2d = new StringBuilder();
                        for (int i = 0; i < pts2d.Count; i++) d2d.Append(F(Sx(pts2d[i].X)) + " " + F(Py(pts2d[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                        if (p2d.IsClosed) d2d.Append("h ");
                        PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash, d2d + "S");
                    }
                }
                break;

            case Mesh mesh:
                {
                    // 3D 网格（现代 MESH）：等轴测 3D 着色实体渲染（Bug4 DOME）
                    // 有面 → 等轴测投影 + 背面剔除 + 法线灰度着色 + 画家算法排序 + 可见边实线/隐藏边虚线
                    // 无面 → 退化为顶视线框（与 SVG/PNG 端 MeshIsoRender 共用核心）
                    double w = StrokeWidth(ent, scaleMul);
                    var isoRes = MeshIsoRender(mesh);
                    if (isoRes.Faces.Count > 0)
                    {
                        // 先画面（远→近，画家算法），再画边
                        for (int fi = 0; fi < isoRes.Faces.Count; fi++)
                        {
                            int g = isoRes.Grays[fi];
                            PdfQuadFill(cs, $"#{g:X2}{g:X2}{g:X2}", isoRes.Faces[fi]);
                        }
                        double ew = Math.Max(0.4, w);
                        var dv = new StringBuilder();
                        foreach (var ed in isoRes.Vis)
                            dv.Append(F(Sx(ed.X1)) + " " + F(Py(ed.Y1)) + " m " + F(Sx(ed.X2)) + " " + F(Py(ed.Y2)) + " l ");
                        if (dv.Length > 0) PdfStroke(cs, "#000000", ew, null, dv + "S");
                        double[] hidDash = { 1.2, 0.8 };
                        var dh = new StringBuilder();
                        foreach (var ed in isoRes.Hid)
                            dh.Append(F(Sx(ed.X1)) + " " + F(Py(ed.Y1)) + " m " + F(Sx(ed.X2)) + " " + F(Py(ed.Y2)) + " l ");
                        if (dh.Length > 0) PdfStroke(cs, "#666666", Math.Max(0.25, ew * 0.6), hidDash, dh + "S");
                    }
                    else if (mesh.Edges != null && mesh.Vertices != null && mesh.Vertices.Count > 0)
                    {
                        // 无面退化：顶视线框
                        var d = new StringBuilder();
                        foreach (var e in mesh.Edges)
                        {
                            if (e.Start < 0 || e.End < 0 || e.Start >= mesh.Vertices.Count || e.End >= mesh.Vertices.Count) continue;
                            var va = mesh.Vertices[e.Start];
                            var vb = mesh.Vertices[e.End];
                            double ax, ay, bx, by;
                            if (tr == null) { ax = va.X; ay = va.Y; bx = vb.X; by = vb.Y; }
                            else { var a = tr.Apply(va.X, va.Y); var b = tr.Apply(vb.X, vb.Y); ax = a.Item1; ay = a.Item2; bx = b.Item1; by = b.Item2; }
                            d.Append(F(Sx(ax)) + " " + F(Py(ay)) + " m " + F(Sx(bx)) + " " + F(Py(by)) + " l ");
                        }
                        if (d.Length > 0) PdfStroke(cs, col, w, null, d + "S");
                    }
                }
                break;

            case PolyfaceMesh pf:
                {
                    // 旧式 3D 多面体网格：按 Face 索引连线成线框
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, w)) break;
                    var dpf = new StringBuilder();
                    foreach (var (a, b) in PolyfaceMeshWire(pf, tr))
                        dpf.Append(F(Sx(a.AX)) + " " + F(Py(a.AY)) + " m " + F(Sx(b.BX)) + " " + F(Py(b.BY)) + " l ");
                    if (dpf.Length > 0) PdfStroke(cs, col, w, null, dpf + "S");
                }
                break;

            case PolygonMesh pm:
                {
                    // M×N 多边形网格：网格线框
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, w)) break;
                    var dpm = new StringBuilder();
                    foreach (var (a, b) in PolygonMeshWire(pm, tr))
                        dpm.Append(F(Sx(a.AX)) + " " + F(Py(a.AY)) + " m " + F(Sx(b.BX)) + " " + F(Py(b.BY)) + " l ");
                    if (dpm.Length > 0) PdfStroke(cs, col, w, null, dpm + "S");
                }
                break;

            case Wipeout wo:
                {
                    // WIPEOUT 属性渲染（三引擎共享 WipeoutFillHex/WipeoutFrame）：填充=42(0/缺省=背景擦除,透明底不画)；
                    // 边框=文件全局 DisplayImageFrame(WIPEOUTVARIABLES 组码70)=1 或 41>0，色=图元/图层色（有图/无图均适用）。
                    // 边框为真实颜色（图元/图层色），透明底也绘制；仅填充(背景擦除)在透明底跳过。
                    var wpoly = WipeoutWorldPoly(wo, tr);
                    if (wpoly.Count >= 3)
                    {
                        if (!_transparentBg)
                        {
                            string fill = WipeoutFillHex(wo.FillColor, _whiteBg);
                            PdfQuadFill(cs, fill, wpoly);
                        }
                        var frame = WipeoutFrame(wo, layerAci, byBlockAci, scaleMul);
                        if (frame.Show)
                        {
                            var fd = new StringBuilder();
                            for (int i = 0; i < wpoly.Count; i++) fd.Append(F(Sx(wpoly[i].X)) + " " + F(Py(wpoly[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                            fd.Append("h ");
                            PdfStroke(cs, frame.Col, Math.Max(0.1, frame.W), null, fd + "S");
                        }
                    }
                }
                break;

            case RasterImage ri:
                EmitRasterImagePdf(cs, ri, tr, scaleMul);
                break;

            case Ole2Frame olef5:
                EmitOle2FramePdf(cs, olef5, tr, scaleMul);
                break;

            case PdfUnderlay pu:
                EmitPdfUnderlayPdf(cs, pu, tr, scaleMul);
                break;

            case Arc a: // 必须先于 Circle（fork 继承关系）
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (a.Center.X, a.Center.Y) : tr.Apply(a.Center.X, a.Center.Y);
                    double r = a.Radius * (tr?.Scale ?? 1.0);
                    // fork 角度为弧度 → 转度（参考实现入参为度）
                    PdfArc(cs, col, StrokeWidth(ent, scaleMul), dash, Sx(cx), Py(cy), r, a.StartAngle * 180.0 / Math.PI, a.EndAngle * 180.0 / Math.PI);
                }
                break;

            case Circle c:
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (c.Center.X, c.Center.Y) : tr.Apply(c.Center.X, c.Center.Y);
                    double r = c.Radius * (tr?.Scale ?? 1.0);
                    // 整圆：三次贝塞尔逼近（PDF 无 arc 操作符，旧写法被合规查看器忽略，
                    // GC117 等块内的圆圈因此整体不显示）
                    var dCircle = new StringBuilder();
                    AppendCircleBezier(dCircle, Sx(cx), Py(cy), r);
                    PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash, dCircle.ToString() + "S");
                }
                break;

            case Ellipse el:
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (el.Center.X, el.Center.Y) : tr.Apply(el.Center.X, el.Center.Y);
                    // fork MajorAxis/MinorAxis 为主/短轴全长(2a/2b)，PdfEllipse 用半轴 → 各除以 2
                    double rx = el.MajorAxis / 2.0 * (tr?.Scale ?? 1.0), ry = el.MinorAxis / 2.0 * (tr?.Scale ?? 1.0);
                    double rot = el.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度 → 度
                    PdfEllipse(cs, col, StrokeWidth(ent, scaleMul), dash, Sx(cx), Py(cy), rx, ry, rot);
                }
                break;

            case Point pt:
                {
                    // 2026-10-26 用户裁定：$PDSIZE=0 不再抑制点——中心点恒按基础 1 像素（1pt）绘制（见 EmitPointPdf），
                    // 外框/臂按设置比例（PointGlyphRadius）绘制；PDMODE=1 且无外框 → 不绘制。
                    var (cx, cy) = tr == null ? (pt.Location.X, pt.Location.Y) : tr.Apply(pt.Location.X, pt.Location.Y);
                    EmitPointPdf(cs, cx, cy, col, StrokeWidth(pt, scaleMul));
                }
                break;

            case TextEntity t:
                {
                    // SHX 样式：真实 SHX 笔迹路径（与 AutoCAD 一致字宽）；非 SHX/字体缺失 → 回退原系统字体路径
                    var shx = TryBuildShxTextWorld(t, tr, scaleMul);
                    if (shx != null)
                    {
                        var d = new StringBuilder();
                        double curX = 0, curY = 0; bool haveCur = false;
                        for (int k = 0; k < shx.Value.Type.Count; k++)
                        {
                            var p = shx.Value.Pts[k];
                            // PDF y 向上（Py），无二次贝塞尔 → 转三次
                            switch (shx.Value.Type[k])
                            {
                                case 0: d.Append(F(Sx(p[0]))).Append(' ').Append(F(Py(p[1]))).Append(" m "); curX = Sx(p[0]); curY = Py(p[1]); haveCur = true; break;
                                case 1: d.Append(F(Sx(p[0]))).Append(' ').Append(F(Py(p[1]))).Append(" l "); curX = Sx(p[0]); curY = Py(p[1]); haveCur = true; break;
                                case 2:
                                {
                                    double p0x = haveCur ? curX : Sx(p[0]);
                                    double p0y = haveCur ? curY : Py(p[1]);
                                    double cx = Sx(p[0]), cy = Py(p[1]), ex = Sx(p[2]), ey = Py(p[3]);
                                    double c1x = p0x + 2.0 / 3.0 * (cx - p0x), c1y = p0y + 2.0 / 3.0 * (cy - p0y);
                                    double c2x = ex + 2.0 / 3.0 * (cx - ex), c2y = ey + 2.0 / 3.0 * (cy - ey);
                                    d.Append(F(c1x)).Append(' ').Append(F(c1y)).Append(' ').Append(F(c2x)).Append(' ').Append(F(c2y)).Append(' ').Append(F(ex)).Append(' ').Append(F(ey)).Append(" c ");
                                    curX = ex; curY = ey; haveCur = true; break;
                                }
                                case 3: d.Append(F(Sx(p[0]))).Append(' ').Append(F(Py(p[1]))).Append(' ').Append(F(Sx(p[2]))).Append(' ').Append(F(Py(p[3]))).Append(' ').Append(F(Sx(p[4]))).Append(' ').Append(F(Py(p[5]))).Append(" c "); curX = Sx(p[4]); curY = Py(p[5]); haveCur = true; break;
                            }
                        }
                        PdfStroke(cs, col, shx.Value.PenWidth, null, d + "S");
                        break;
                    }
                    // TTF 样式：cap-height 字号映射（AutoCAD 把 H 当 cap height，非 em，见 TtfCapScale）
                    var (bx, by, brot, bscale) = TextBaseline(t, tr);
                    double fs = t.Height * scaleMul * bscale * TtfCapScale(ResolveFontFamily(GetStyleFont(t.Style)));
                    int slot = _pdfFonts.GetSlot(ResolveFontFamily(GetStyleFont(t.Style)));
                    PdfText(cs, col, fs, Sx(bx), Py(by), brot, t.Value ?? "", slot, t.WidthFactor);
                }
                break;

            case MText mt:
                // 多行文字：解析格式码（\P 换行/\H 分段字号/\f 字体）+ 参考列宽换行 + 附着点定位
                EmitMTextPdf(cs, mt, tr, scaleMul, layerAci, byBlockAci, GetStyleFont(mt.Style));
                break;

            case Solid so:
                PdfQuadFill(cs, col, SolidQuadPts(so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner, tr));
                break;
            case Face3D fd:
                PdfQuadFill(cs, col, QuadPts(fd.FirstCorner, fd.SecondCorner, fd.ThirdCorner, fd.FourthCorner, tr));
                break;

            case Hatch ht:
                EmitHatchPdf(cs, ht, tr, scaleMul, layerAci, byBlockAci);
                break;
            case Spline sp:
                if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(sp, scaleMul))) break;
                EmitSplinePdf(cs, sp, tr, scaleMul, col, dash);
                break;

            case XLine xl:
                {
                    var (px, py) = tr == null ? (xl.FirstPoint.X, xl.FirstPoint.Y) : tr.Apply(xl.FirstPoint.X, xl.FirstPoint.Y);
                    var (dx, dy) = TransformDirection(xl.Direction.X, xl.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, true, out var x1, out var y1, out var x2, out var y2))
                        PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash,
                            F(Sx(x1)) + " " + F(Py(y1)) + " m " + F(Sx(x2)) + " " + F(Py(y2)) + " l S");
                }
                break;

            case Ray ray:
                {
                    var (px, py) = tr == null ? (ray.StartPoint.X, ray.StartPoint.Y) : tr.Apply(ray.StartPoint.X, ray.StartPoint.Y);
                    var (dx, dy) = TransformDirection(ray.Direction.X, ray.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, false, out var x1, out var y1, out var x2, out var y2))
                        PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash,
                            F(Sx(x1)) + " " + F(Py(y1)) + " m " + F(Sx(x2)) + " " + F(Py(y2)) + " l S");
                }
                break;

            case MLine ml:
                {
                    // 多线：按 DXF 41 参数化提取各子线的实际画线段（含画/断模式与开口长短），
                    // 逐段描边细线。MLINE 正确语义=一组平行细线（AutoCAD 2D 渲染永不填充）。
                    double w = StrokeWidth(ent, scaleMul);
                    foreach (var el in MLineDrawnElements(ml, tr))
                    {
                        string ecol = ColorOf(el.Aci, layerAci, byBlockAci);
                        foreach (var s in el.Segs)
                        {
                            var d = new StringBuilder();
                            d.Append(F(Sx(s.X1)) + " " + F(Py(s.Y1)) + " m "
                                 + F(Sx(s.X2)) + " " + F(Py(s.Y2)) + " l ");
                            PdfStroke(cs, ecol, w, dash, d + "S");
                        }
                    }
                }
                break;

            case Leader ld:
                {
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
                        var linePts = TrimPolylineStart(
                            (ld.PathType == LeaderPathType.Spline)
                                ? LeaderSplineFlatten(pts, 24)
                                : pts,
                            trim);
                        var d = new StringBuilder();
                        for (int i = 0; i < linePts.Count; i++)
                            d.Append(F(Sx(linePts[i].X)) + " " + F(Py(linePts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                        PdfStroke(cs, col, w, dash, d + "S");
                        // 箭头（无箭头 71=0 / XData 空块时不画）：
                        //  · 块箭头（非 _Closed）→ 按块几何忠实渲染（空心描边/常宽闭合填充，_ClosedBlank 空心、_Dot 实心圆点等）。
                        //  · _Closed 块或无块（系统默认闭合箭头）→ 实心三角，按解析尺寸 asz（trA 已缩放）。
                        if (arrow && trA != null)
                        {
                            if (aStrokes != null)
                            {
                                foreach (var s in aStrokes)
                                {
                                    if (s.Count < 2) continue;
                                    var ds = new StringBuilder();
                                    for (int i = 0; i < s.Count; i++)
                                        ds.Append(F(Sx(s[i].X)) + " " + F(Py(s[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                                    PdfStroke(cs, col, w, dash, ds + "S");
                                }
                                foreach (var f in aFills) if (f.Count >= 3) PdfQuadFill(cs, col, f);
                            }
                            else if (aTri != null)
                            {
                                PdfQuadFill(cs, col, aTri);
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
                            EmitEntityPdf(cs, be, null, 1.0, dimAci, null, null, ltMul, dimLayerAci);
                        }
                    }
                    else
                        RecordSkip(ent.GetType().Name);
                }
                break;

            case Shape shp:
                {
                    // SHX 形状实体：用 shape 字体库真实字形笔迹渲染（BuildShapePath 共享，PDF 用 Sx/Py 不翻转 Y）
                    string shpCol = ColorOf(EntityAciIndex(shp), layerAci, byBlockAci);
                    string shpPath = BuildShapePath(shp, tr, Sx, Py, pdf: true);
                    if (shpPath.Length > 0)
                        PdfStroke(cs, shpCol, StrokeWidth(shp, scaleMul), null, shpPath + " S");
                }
                break;

            default:
                RecordSkip(ent.GetType().Name);
                break;
        }
    }

    // 复杂线型（PDF）：沿路径走查模式 → 内容流路径串 + S（虚线状态显式复位为实线）
    private bool TryEmitComplexPdf(MemoryStream cs, Entity ent, Transform2D tr, LtpPattern pat, string col, double w)
    {
        if (pat == null) return false;
        if (!TryGetPathPoints(ent, tr, out var pts) || pts.Count < 2) return false;
        var cum = BuildCum(pts);
        if (cum[cum.Length - 1] <= 1e-9) return false;
        var d = new StringBuilder();
        var texts = new List<LtpTextPlace>();
        WalkPattern(d, pts, cum, pat, Sx, Py, true, texts);
        if (d.Length == 0 && texts.Count == 0) return false;
        if (d.Length > 0)
            PdfStroke(cs, col, w, null, d + "S");
        // 文字段：原生 PDF 文字（坐标需 Sx/Py 映射，rotDeg 数据系角）
        foreach (var p in texts)
        {
            int slot = _pdfFonts != null ? _pdfFonts.GetSlot(ResolveFontFamily(p.Font)) : 0;
            PdfText(cs, col, p.Height, Sx(p.X), Py(p.Y), p.RotDeg, p.Text, slot, p.WidthFactor);
        }
        return true;
    }

    // 多行文字（PDF）：与 EmitMTextSvg 同一布局，逐 run 用 Tm 6 参数文本矩阵 + 2 字节 GID 串
    private void EmitMTextPdf(MemoryStream cs, MText mt, Transform2D tr, double scaleMul, short layerAci, short byBlockAci, string styleFont)
    {
        string baseCol = ColorOf(EntityAciIndex(mt), layerAci, byBlockAci);
        string styleBig = GetStyleBigFont(mt.Style);
        // \pxsm 段落行距倍数同样走扩展字典缓存提取（与 SVG 端一致；组码 44 常保持 1.0）：
        // 此前 PDF/PNG 未传 paraProps → 多倍行距丢失、整块偏矮（AutoCAD 图明显行距更大）。
        var layout = WrapMText(ParseMText(mt.Value ?? "", mt.Height, styleFont, styleBig, MTextCacheParaProps(mt)), mt.RectangleWidth, styleFont, styleBig);
        if (layout.Lines.Count == 0) return;
        var (baselines, anchorX, anchorY) = MTextLocalLayout(layout, mt, styleFont, styleBig);

        double rot = mt.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度（只读，源自 AlignmentPoint）→ 度
        double rad = rot * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        double px = mt.InsertPoint.X, py = mt.InsertPoint.Y;
        if (tr != null) { var ap = tr.Apply(px, py); px = ap.Item1; py = ap.Item2; }

        void ToWorld(double lx, double ly, out double wx, out double wy)
        {
            double dx = (lx - anchorX) * scaleMul, dy = (ly - anchorY) * scaleMul;
            wx = px + dx * ca - dy * sa;
            wy = py + dx * sa + dy * ca;
        }

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
                    // \S<num>/<den> 分数：分子/分母上下分层（不同基线的 BT/ET）+ 中间分数线（与 SVG/PNG 端一致）
                    string col = run.Color.HasValue ? AciColor(run.Color.Value) : baseCol;
                    string fracFont = !string.IsNullOrEmpty(run.Font) ? run.Font : styleFont;
                    double fracCap = TtfCapScale(ResolveFontFamily(fracFont));
                    double fs = Math.Max(run.Height * scaleMul * fracCap, 0.2);
                    int slot = _pdfFonts.GetSlot(ResolveFontFamily(fracFont));
                    CjkFont font = _pdfFonts.FontAt(slot);
                    int sl = run.Text.IndexOf('/');
                    string num = run.Text.Substring(0, sl);
                    string den = sl + 1 < run.Text.Length ? run.Text.Substring(sl + 1) : "";
                    double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;
                    double numW = EstimateTextWidth(num, run.Height) * wf * scaleMul * fracCap;
                    double denW = EstimateTextWidth(den, run.Height) * wf * scaleMul * fracCap;
                    double fracW = Math.Max(numW, denW);
                    // 分数堆叠几何（CAD y 向上，相对主基线；与 SVG/PNG 端同一套 AutoCAD 实测值）
                    double H = run.Height;
                    double numOff = 0.72 * H;   // 分子基线：主基线上方
                    double denOff = 0.30 * H;   // 分母基线：主基线下方
                    double barOff = 0.58 * H;   // 分数线：主基线上方
                    double tk = Math.Tan(EffOblique(run) * Math.PI / 180.0); // 斜体剪切（Tm 的 c/d 列）
                    double numX = x + (fracW - numW) / 2.0;
                    double denX = x + (fracW - denW) / 2.0;
                    void EmitStackText(double lx, double ly, string s)
                    {
                        if (string.IsNullOrEmpty(s)) return;
                        ToWorld(lx, ly, out double wx, out double wy);
                        byte[] data = font != null ? font.Encode(s) : PdfFallbackTextBytes(s);
                        if (data.Length == 0) return;
                        // Tm = R·Skew·Scale：a=ca·wf, b=sa·wf, c=ca·tk−sa, d=sa·tk+ca（tk=0 时退化为旋转）
                        CsAscii(cs, "BT\n/F" + slot + " " + F(fs) + " Tf\n" + HexToRgbPdf(col) + " rg\n" +
                                   F(ca * wf) + " " + F(sa * wf) + " " + F(ca * tk - sa) + " " + F(sa * tk + ca) + " " + F(Sx(wx)) + " " + F(Py(wy)) + " Tm\n");
                        CsString(cs, data);
                        CsAscii(cs, " Tj\nET\n");
                    }
                    EmitStackText(numX, baselines[li] + numOff, num);
                    EmitStackText(denX, baselines[li] - denOff, den);
                    ToWorld(x, baselines[li] + barOff, out double f1x, out double f1y);
                    ToWorld(x + fracW, baselines[li] + barOff, out double f2x, out double f2y);
                    PdfStroke(cs, col, Math.Max(0.05, fs * 0.04), null, F(Sx(f1x)) + " " + F(Py(f1y)) + " m " + F(Sx(f2x)) + " " + F(Py(f2y)) + " l S");
                }
                else if (!string.IsNullOrEmpty(run.Text))
                {
                    string col = run.Color.HasValue ? AciColor(run.Color.Value) : baseCol;
                    double fs = Math.Max(run.Height * scaleMul, 0.2);
                    string effFont = !string.IsNullOrEmpty(run.Font) ? run.Font : styleFont;
                    string effBig = string.IsNullOrEmpty(run.Font) ? styleBig : null;
                    double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;
                    double tk = Math.Tan(EffOblique(run) * Math.PI / 180.0);
                    ToWorld(x, baselines[li], out double wx, out double wy);
                    // SHX 笔迹优先（simplex.shx 等）：真实 SHX 字形描边，字高随 em 自然（不再经 SimSun 压缩→偏小）
                    if (TryBuildMTextShxLocal(effFont, effBig, run.Text, run.Height, wf, out List<int> st, out List<double[]> sp, out _))
                    {
                        EmitShxRunPdf(cs, wx, wy, st, sp, run.Height, wf, fs, ca, sa, tk, col);
                    }
                    else
                    {
                        // 回退 TTF（含 CJK 无 bigfont / 内联 TTF / 字形缺失）→ 原 BT/Tj 文本
                        int slot = _pdfFonts.GetSlot(ResolveFontFamily(effFont));
                        CjkFont font = _pdfFonts.FontAt(slot);
                        byte[] data = font != null ? font.Encode(run.Text) : PdfFallbackTextBytes(run.Text);
                        if (data.Length > 0)
                        {
                            // TTF cap-height 字号映射（Phase 2）：AutoCAD 把 H 当 cap height，非 em。
                            // 与单行 TTF（line 504）及宽度估算 TtfMTextWidth 一致——此前 MText TTF 未套
                            // cap-scale，字形比单行/AutoCAD 小 ~28%（"多行富文本字体不一致"）。SHX 分支不受影响。
                            double ttfFs = Math.Max(run.Height * scaleMul * TtfCapScale(ResolveFontFamily(effFont)), 0.2);
                            // \T 字距：PDF Tc 的单位是【文本空间单位】(unscaled text space units, PDF 32000 §9.3.2)，
                            // 直接加在每个字形的 advance 上（且跨 BT/ET 持久！）——不是 1/1000 em。
                            // 旧版误按 1/1000 em 发 "−25 Tc" → 每字 advance = w + (−25) 为负 → 整行从右往左倒序散射
                            // （这也是后续不设 Tc 的文字块被泄漏污染而乱序的根因）。
                            // 正确换算：期望额外间距 = (因子−1)×0.1em，1em = fs（文本空间字号）→ Tc = (T−1)×0.1×fs ÷ wf
                            //（÷wf 抵消 Tm 的横向 scale，使字距视觉量不受 \W 影响）。
                            double tc = (run.Tracking - 1.0) * 0.1 * ttfFs / (wf > 1e-9 ? wf : 1.0);
                            // Tc 是文本状态操作符，只能在 BT..ET 内设置；且跨 BT/ET 持久 → 每块显式写（含 0 Tc 复位）
                            string tcLine = Math.Abs(tc) > 1e-9 ? F(tc) + " Tc\n" : "0 Tc\n";
                            CsAscii(cs, "BT\n/F" + slot + " " + F(ttfFs) + " Tf\n" + HexToRgbPdf(col) + " rg\n" + tcLine +
                                       F(ca * wf) + " " + F(sa * wf) + " " + F(ca * tk - sa) + " " + F(sa * tk + ca) + " " + F(Sx(wx)) + " " + F(Py(wy)) + " Tm\n");
                            CsString(cs, data);
                            CsAscii(cs, " Tj\nET\n");
                        }
                    }
                    if (run.Underline)
                    {
                        ToWorld(x, baselines[li] - 0.08 * run.Height, out double u1x, out double u1y);
                        ToWorld(x + rw, baselines[li] - 0.08 * run.Height, out double u2x, out double u2y);
                        PdfStroke(cs, col, Math.Max(0.05, fs * 0.04), null, F(Sx(u1x)) + " " + F(Py(u1y)) + " m " + F(Sx(u2x)) + " " + F(Py(u2y)) + " l S");
                    }
                }
                x += rw;
            }
        }
    }

    // PDF 样条（描边折线逼近）
    private void EmitSplinePdf(MemoryStream cs, Spline sp, Transform2D tr, double scaleMul, string col, double[] dash)
    {
        var pts = SplineSample(sp, tr, 16);
        if (pts.Count < 2) { RecordSkip("SPLINE(空)"); return; }
        var d = new StringBuilder();
        for (int i = 0; i < pts.Count; i++)
            d.Append(F(Sx(pts[i].X)) + " " + F(Py(pts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
        PdfStroke(cs, col, StrokeWidth(sp, scaleMul), dash, d + "S");
    }

    // SHX 笔迹 → PDF 描边（与 EmitMTextPdf 文本 Tm 同一变换：R·Skew(tk)·Scale(wf)，y 向上）。
    // types/pts = TryBuildMTextShxLocal 输出（基线原点、数据单位、y 上，不含 wf）；H=字高（数据单位，1 em）。
    // 用于 MTEXT simplex.shx 等 SHX 基础样式：真实 SHX 字形描边，字高随 em 自然（不再经 SimSun 压缩→偏小）。
    private void EmitShxRunPdf(MemoryStream cs, double ox, double oy, List<int> types, List<double[]> pts, double H, double wf,
        double fs, double ca, double sa, double tk, string col)
    {
        if (pts.Count == 0) return;
        double Tx = Sx(ox), Ty = Py(oy);
        double penW = Math.Max(0.05, fs * 0.04); // 笔画宽（用户单位）

        // 局部点（基线原点 / 数据单位 / y 上）→ PDF 用户坐标（与文本 Tm 同：em=gx/H，先 Scale(wf) 再 Skew(tk) 再 R）。
        // 关键：em 位移须【乘回字号 fs（PDF 单位）】——局部点经 /H 归一化到 em(cap=1)后，若直接加到 PDF
        // 坐标只得到 ~1 PDF 单位/em（偏小 fs≈H 倍）；乘 fs=H·scaleMul 后与 TTF 路径（Tf fs）及 PNG 端
        // DrawShxRunPng（Lx=(lx/H)*fs*wf、Ly=-(ly/H)*fs）一致。
        void ToUser(double lx, double ly, out double UX, out double UY)
        {
            double gx = lx / H, gy = ly / H;
            double ex = gx * wf + tk * gy;
            double ey = gy;
            UX = Tx + (ca * ex - sa * ey) * fs;
            UY = Ty + (sa * ex + ca * ey) * fs;
        }

        var cur = new List<(double X, double Y)>();
        var d = new StringBuilder();
        void StrokeCur()
        {
            if (cur.Count < 2) { cur.Clear(); return; }
            d.Clear();
            d.Append(F(cur[0].X) + " " + F(cur[0].Y) + " m ");
            for (int i = 1; i < cur.Count; i++) d.Append(F(cur[i].X) + " " + F(cur[i].Y) + " l ");
            PdfStroke(cs, col, penW, null, d + "S");
            cur.Clear();
        }
        void FlatQ((double X, double Y) s, double cx, double cy, double ex, double ey)
        {
            int n = 12;
            for (int i = 1; i <= n; i++)
            {
                double t = i / (double)n, u = 1 - t;
                cur.Add((u * u * s.X + 2 * u * t * cx + t * t * ex, u * u * s.Y + 2 * u * t * cy + t * t * ey));
            }
        }
        void FlatC((double X, double Y) s, double c1x, double c1y, double c2x, double c2y, double ex, double ey)
        {
            int n = 16;
            for (int i = 1; i <= n; i++)
            {
                double t = i / (double)n, u = 1 - t;
                cur.Add((u * u * u * s.X + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * ex,
                         u * u * u * s.Y + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * ey));
            }
        }

        for (int k = 0;k < types.Count; k++)
        {
            var p = pts[k];
            switch (types[k])
            {
                case 0: // Move：抬笔 → 新子路径
                    StrokeCur();
                    ToUser(p[0], p[1], out double mx, out double my);
                    cur.Add((mx, my));
                    break;
                case 1: // Line
                    ToUser(p[0], p[1], out double lx, out double ly);
                    cur.Add((lx, ly));
                    break;
                case 2: // Quad（控制, 终点）
                    if (cur.Count > 0)
                    {
                        var s = cur[cur.Count - 1];
                        ToUser(p[0], p[1], out double qcx, out double qcy);
                        ToUser(p[2], p[3], out double qex, out double qey);
                        FlatQ(s, qcx, qcy, qex, qey);
                    }
                    break;
                case 3: // Cubic（控制1, 控制2, 终点）
                    if (cur.Count > 0)
                    {
                        var s = cur[cur.Count - 1];
                        ToUser(p[0], p[1], out double c1x, out double c1y);
                        ToUser(p[2], p[3], out double c2x, out double c2y);
                        ToUser(p[4], p[5], out double e1x, out double e1y);
                        FlatC(s, c1x, c1y, c2x, c2y, e1x, e1y);
                    }
                    break;
            }
        }
        StrokeCur();
    }

    // 解析 IMAGE/UNDERLAY 的相对文件路径（DWG 目录优先、绝对路径、仅文件名兜底）。
    // _srcDir 惰性初始化：三引擎（SVG/PNG/PDF）共用本方法，此前仅 WritePdf 设置 _srcDir，
    // 导致 SVG/PNG 先于 PDF 渲染时 IMAGE 相对路径只按 cwd 解析而"文件缺失"（2026-10-24 修复）。
    private string ResolveAssetPath(string rel)
    {
        if (string.IsNullOrEmpty(rel)) return null;
        if (Path.IsPathRooted(rel) && File.Exists(rel)) return rel;
        if (_srcDir == null && !string.IsNullOrEmpty(SourceFilePath))
            _srcDir = Path.GetDirectoryName(Path.GetFullPath(SourceFilePath));
        var cands = new List<string>();
        if (_srcDir != null)
        {
            cands.Add(Path.Combine(_srcDir, rel));
            cands.Add(Path.Combine(_srcDir, Path.GetFileName(rel)));
        }
        cands.Add(rel);
        cands.Add(Path.GetFileName(rel));
        foreach (var c in cands)
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return Path.GetFullPath(c);
        return null;
    }

    // 读外部 PDF 页面 MediaBox（稳健版：字节→ASCII 近似，找 /MediaBox [x0 y0 x1 y1]）。
    private static double[] TryReadMediaBox(string path)
    {
        try
        {
            byte[] d = File.ReadAllBytes(path);
            var sb = new StringBuilder(d.Length);
            foreach (var b in d) sb.Append(b >= 32 && b < 127 ? (char)b : ' ');
            string s = sb.ToString();
            int i = s.IndexOf("/MediaBox");
            if (i < 0) return null;
            var m = System.Text.RegularExpressions.Regex.Match(s.Substring(i), @"\[([-\d.eE]+)\s+([-\d.eE]+)\s+([-\d.eE]+)\s+([-\d.eE]+)\]");
            if (!m.Success) return null;
            return new[] { double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                           double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                           double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                           double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) };
        }
        catch { return null; }
    }

    // 画四边形边框 + 两条对角线（图片/底图缺失或降级时的占位，灰色）。
    private void PdfQuadOutline(MemoryStream cs, double ex, double ey, double a, double b, double c, double d, string col)
    {
        double x00 = ex, y00 = ey, x10 = ex + a, y10 = ey + b, x01 = ex + c, y01 = ey + d, x11 = ex + a + c, y11 = ey + b + d;
        string Seg(double X1, double Y1, double X2, double Y2) => F(X1) + " " + F(Y1) + " m " + F(X2) + " " + F(Y2) + " l ";
        // 占位框颜色传入的是 SVG hex（#888888/#6699cc），必须转 PDF RGB 分量，否则 "#6699cc RG" 是非法 token 致解析器报错。
        CsAscii(cs, HexToRgbPdf(col) + " RG 0.5 w\n" + Seg(x00, y00, x10, y10) + Seg(x10, y10, x11, y11) + Seg(x11, y11, x01, y01) + Seg(x01, y01, x00, y00) + Seg(x00, y00, x11, y11) + Seg(x10, y10, x01, y01) + "S\n");
    }

    // PDF 光栅图片（IMAGE / RasterImage）：外部图片嵌入为 /Subtype /Image XObject。
    // JPEG → /DCTDecode 直嵌（无损、无需解码）；PNG → 解码为 RGB + /FlateDecode（PNG Up 预测器）；
    // 其它格式/文件缺失 → 降级为矩形边框 + 对角线（不再空白缺失）。
    private void EmitRasterImagePdf(MemoryStream cs, RasterImage ri, Transform2D tr, double scaleMul)
    {
        // 图像四边形（世界四角）：a0=左下(InsertPoint)，a10=右，a01=上，a11=右上；u/v 为单像素向量，乘以像素数得总偏移。
        var ip = ri.InsertPoint;
        double W = ri.Size.X, H = ri.Size.Y;
        var u = ri.UVector; var v = ri.VVector;
        (double X, double Y) P(double x, double y)
        {
            if (tr == null) return (x, y);
            var p = tr.Apply(x, y); return (p.Item1, p.Item2);
        }
        var a0 = P(ip.X, ip.Y);
        var a10 = P(ip.X + u.X * W, ip.Y + u.Y * W);
        var a01 = P(ip.X + v.X * H, ip.Y + v.Y * H);
        double ex = Sx(a0.X), ey = Py(a0.Y);
        double cmA = Sx(a10.X) - ex, cmB = Py(a10.Y) - ey;
        double cmC = Sx(a01.X) - ex, cmD = Py(a01.Y) - ey;

        string path = ResolveAssetPath(ri.Definition?.FileName);
        if (string.IsNullOrEmpty(path))
        {
            PdfQuadOutline(cs, ex, ey, cmA, cmB, cmC, cmD, "#888888");
            RecordSkip("IMAGE(文件缺失)");
            _log("⚠️  IMAGE 图片文件缺失，降级为边框: " + (ri.Definition?.FileName ?? "?"));
            return;
        }
        byte[] bytes = File.ReadAllBytes(path);
        string name = "Im" + _pdfXObjSeq++;
        _pdfXObjRegs.Add(new PdfXObjReg
        {
            Name = name,
            Build = (baseNum) =>
            {
                var objs = new List<(int, byte[])>();
                var jpg = RasterHelpers.ProbeJpeg(bytes);
                if (jpg != null)
                {
                    string csName = jpg.Components == 1 ? "/DeviceGray" : (jpg.Components == 3 ? "/DeviceRGB" : "/DeviceCMYK");
                    var head = "<< /Type /XObject /Subtype /Image /Width " + jpg.Width + " /Height " + jpg.Height +
                               " /ColorSpace " + csName + " /BitsPerComponent 8 /Filter /DCTDecode /Length " + bytes.Length + " >>\nstream\n";
                    var body = new byte[head.Length + bytes.Length + "\nendstream".Length];
                    var hb = Encoding.ASCII.GetBytes(head);
                    Buffer.BlockCopy(hb, 0, body, 0, hb.Length);
                    Buffer.BlockCopy(bytes, 0, body, hb.Length, bytes.Length);
                    var tb = Encoding.ASCII.GetBytes("\nendstream");
                    Buffer.BlockCopy(tb, 0, body, hb.Length + bytes.Length, tb.Length);
                    objs.Add((baseNum, body));
                }
                else
                {
                    try
                    {
                        var (w, h, ch, pix) = RasterHelpers.DecodePngToRgb(bytes);
                        var raw = new byte[w * h * 3 + h]; // 每行 1 filter(0) + 3*w
                        int o = 0;
                        for (int y = 0;y < h; y++)
                        {
                            raw[o++] = 0;
                            for (int x = 0;x < w; x++)
                            {
                                int src = (y * w + x) * ch;
                                raw[o++] = pix[src]; raw[o++] = pix[src + 1]; raw[o++] = pix[src + 2];
                            }
                        }
                        byte[] comp;
                        using (var ms = new MemoryStream()) { using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(raw, 0, raw.Length); comp = ms.ToArray(); } // ZLibStream=RFC1950(zlib 头 0x78+adler32)，/FlateDecode 规范要求；DeflateStream 产裸 DEFLATE(RFC1951 无头)→查看器无法解码
                        var head = "<< /Type /XObject /Subtype /Image /Width " + w + " /Height " + h +
                                   " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors 3 /BitsPerComponent 8 /Columns " + w + " >> /Length " + comp.Length + " >>\nstream\n";
                        var body = new byte[head.Length + comp.Length + "\nendstream".Length];
                        var hb = Encoding.ASCII.GetBytes(head);
                        Buffer.BlockCopy(hb, 0, body, 0, hb.Length);
                        Buffer.BlockCopy(comp, 0, body, hb.Length, comp.Length);
                        var tb = Encoding.ASCII.GetBytes("\nendstream");
                        Buffer.BlockCopy(tb, 0, body, hb.Length + comp.Length, tb.Length);
                        objs.Add((baseNum, body));
                    }
                    catch
                    {
                        objs.Add((baseNum, Encoding.ASCII.GetBytes("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\n\xff\xff\xff\nendstream")));
                    }
                }
                return objs;
            }
        });
        CsAscii(cs, "q\n" + F(cmA) + " " + F(cmB) + " " + F(cmC) + " " + F(cmD) + " " + F(ex) + " " + F(ey) + " cm\n/" + name + " Do\nQ\n");
    }

    // PDF OLE2 嵌入对象（Ole2Frame，组码310 内嵌 OLE CFB 容器）：
    // 提取内嵌 BMP（OLE 图片对象的 DIB 预览，24/32 位）→ 解码 RGBA → RGB 逐行 + /FlateDecode(Predictor 15)
    // 嵌入为 /Subtype /Image XObject，按 UL/LR 两角矩形 cm 贴图（a0=左下、a10=右下、a01=左上）。
    // 非图片类 OLE/提取/解码失败 → 矩形边框 + 对角线降级（与 IMAGE 一致）。
    private void EmitOle2FramePdf(MemoryStream cs, Ole2Frame ole, Transform2D tr, double scaleMul)
    {
        double xL = Math.Min(ole.UpperLeftCorner.X, ole.LowerRightCorner.X);
        double xR = Math.Max(ole.UpperLeftCorner.X, ole.LowerRightCorner.X);
        double yB = Math.Min(ole.UpperLeftCorner.Y, ole.LowerRightCorner.Y);
        double yT = Math.Max(ole.UpperLeftCorner.Y, ole.LowerRightCorner.Y);
        (double X, double Y) P(double x, double y) { if (tr == null) return (x, y); var p = tr.Apply(x, y); return (p.Item1, p.Item2); }
        var a0 = P(xL, yB);   // 左下
        var a10 = P(xR, yB);  // 右下
        var a01 = P(xL, yT);  // 左上
        double ex = Sx(a0.X), ey = Py(a0.Y);
        double cmA = Sx(a10.X) - ex, cmB = Py(a10.Y) - ey;
        double cmC = Sx(a01.X) - ex, cmD = Py(a01.Y) - ey;
        if (!Ole2Frame.TryExtractBmp(ole.BinaryData, out byte[] bmpBytes))
        {
            PdfQuadOutline(cs, ex, ey, cmA, cmB, cmC, cmD, "#888888");
            RecordSkip("OLE2FRAME(无内嵌BMP)");
            _log("⚠️  OLE2FRAME 未提取到内嵌 BMP（类型=" + ole.OleObjectType + "），降级为边框");
            return;
        }
        int w = 0, h = 0; byte[] rgba = null;
        try { var d = RasterHelpers.DecodeBmpToRgba(bmpBytes); w = d.width; h = d.height; rgba = d.rgba; }
        catch (Exception ex2) { _log("⚠️  OLE2FRAME 内嵌 BMP 解码失败: " + ex2.Message); }
        if (w <= 0 || h <= 0 || rgba == null)
        {
            PdfQuadOutline(cs, ex, ey, cmA, cmB, cmC, cmD, "#888888");
            RecordSkip("OLE2FRAME(BMP解码失败)");
            return;
        }
        string name = "Im" + _pdfXObjSeq++;
        _pdfXObjRegs.Add(new PdfXObjReg
        {
            Name = name,
            Build = (baseNum) =>
            {
                var objs = new List<(int, byte[])>();
                try
                {
                    var raw = new byte[w * h * 3 + h]; // 每行前置 filter 字节 0
                    int o = 0;
                    for (int y = 0; y < h; y++)
                    {
                        raw[o++] = 0;
                        for (int x = 0; x < w; x++)
                        {
                            int src = (y * w + x) * 4;
                            raw[o++] = rgba[src]; raw[o++] = rgba[src + 1]; raw[o++] = rgba[src + 2];
                        }
                    }
                    byte[] comp;
                    using (var ms = new MemoryStream()) { using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(raw, 0, raw.Length); comp = ms.ToArray(); }
                    var head = "<< /Type /XObject /Subtype /Image /Width " + w + " /Height " + h +
                               " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors 3 /BitsPerComponent 8 /Columns " + w + " >> /Length " + comp.Length + " >>\nstream\n";
                    var body = new byte[head.Length + comp.Length + "\nendstream".Length];
                    var hb = Encoding.ASCII.GetBytes(head);
                    Buffer.BlockCopy(hb, 0, body, 0, hb.Length);
                    Buffer.BlockCopy(comp, 0, body, hb.Length, comp.Length);
                    var tb = Encoding.ASCII.GetBytes("\nendstream");
                    Buffer.BlockCopy(tb, 0, body, hb.Length + comp.Length, tb.Length);
                    objs.Add((baseNum, body));
                }
                catch
                {
                    objs.Add((baseNum, Encoding.ASCII.GetBytes("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\n\xff\xff\xff\nendstream")));
                }
                return objs;
            }
        });
        CsAscii(cs, "q\n" + F(cmA) + " " + F(cmB) + " " + F(cmC) + " " + F(cmD) + " " + F(ex) + " " + F(ey) + " cm\n/" + name + " Do\nQ\n");
    }

    // PDF 底图引用（PDFUNDERLAY / PdfUnderlay）：将外部 PDF 页面按其正确尺寸绘制为引用占位框。
    // 矢量嵌入（Form XObject）需递归资源导入，留待后续增强；此处先保证"显示引用位置/尺寸"，不再空白缺失。
    private void EmitPdfUnderlayPdf(MemoryStream cs, PdfUnderlay pu, Transform2D tr, double scaleMul)
    {
        double pw = 595.276, ph = 841.89; // 默认 A4（pt）
        string fpath = ResolveAssetPath(pu.Definition?.File);
        if (!string.IsNullOrEmpty(fpath))
        {
            var mb = TryReadMediaBox(fpath);
            if (mb != null) { pw = mb[2] - mb[0]; ph = mb[3] - mb[1]; }
        }
        // AutoCAD 将 PDF 页面 1pt 视为 1/72 inch 放入图纸（单位=inch 时）。
        double sx = pu.XScale * (1.0 / 72.0), sy = pu.YScale * (1.0 / 72.0);
        var ip = pu.InsertPoint;
        (double X, double Y) P(double x, double y)
        {
            if (tr == null) return (x, y);
            var p = tr.Apply(x, y); return (p.Item1, p.Item2);
        }
        var a0 = P(ip.X, ip.Y);
        var a10 = P(ip.X + pw * sx, ip.Y);
        var a01 = P(ip.X, ip.Y + ph * sy);
        double ex = Sx(a0.X), ey = Py(a0.Y);
        double cmA = Sx(a10.X) - ex, cmB = Py(a10.Y) - ey;
        double cmC = Sx(a01.X) - ex, cmD = Py(a01.Y) - ey;
        PdfQuadOutline(cs, ex, ey, cmA, cmB, cmC, cmD, "#6699cc");
        RecordSkip("PDFUNDERLAY(降级边框)");
        _log("ℹ️  PDF 底图引用已渲染为占位框: " + (pu.Definition?.File ?? "?") + " P" + (pu.Definition?.Page ?? ""));
    }

    // PDF HATCH（实心=rg f/f*；渐变=Type2 轴向 / Type3 径向 shading，/Sh<n> sh）
    private void EmitHatchPdf(MemoryStream cs, Hatch ht, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var loops = BuildHatchLoops(ht, tr);
        if (loops.Count == 0) { RecordSkip("HATCH(空)"); return; }
        var gp = ht.GradientColor;
        bool gradient = gp != null && gp.Enabled;
        if (!gradient && !IsSolidPattern(ht)) { EmitHatchPatternPdf(cs, ht, loops, tr, scaleMul, layerAci, byBlockAci); return; }
        string rule = loops.Count > 1 ? "f*" : "f";
        string d = BuildPathDPdf(loops, x => Sx(x), y => Py(y));
        if (gradient)
        {
            var gradSpec = MakeHatchGradSpec(gp);
            // 所有渐变（含 LINEAR）统一走光栅化（RasterCanvas.FillGradientProfile + 33 点 LUT 剖面，
            // 嵌入为"路径裁剪的图像 XObject"），三引擎像素级一致。原因：
            //  ① 非线性（SPHERICAL/HEMISPHERICAL/CYLINDER/CURVED，含 INV）的 LUT 钟形/峰谷剖面，
            //     FunctionType 2 只能画双端线性插值，无法表达；
            //  ② LINEAR 曾走 Type2 矢量 shading（shading 对象定义完全符合规范、Page /Shading 资源引用
            //     也正确），但 PDFium（Chrome/Edge 内置 PDF 阅读器）对 `W n` 裁剪 + `sh` 的组合渲染
            //     不可靠——实测 4 个 LINEAR 块在浏览器 PDF 阅读器中完全不显示（用户反馈"pdf 有个
            //     属性的填充未渲染"）。图像 XObject 是 PDF 最基础特性、所有阅读器兼容，故统一走图像。
            EmitHatchGradientRasterPdf(cs, d, loops, gp, gradSpec);
        }
        else
        {
            CsAscii(cs, HexToRgbPdf(HatchSolidFillHex(ht, layerAci, byBlockAci)) + " rg\n" + d + rule + "\n");
        }
    }

    // 非线性渐变 HATCH（PDF）：用与 PNG 完全一致的 RasterCanvas.FillGradientProfile + 33 点 LUT 剖面
    // 光栅化为 RGBA，嵌入为"路径裁剪的图像 XObject"（q → 路径 W n 裁剪 → cm 定位图像 → /ImN Do → Q）。
    // 图像像素分辨率 = 渐变 bbox 尺寸 × OutputZoom（与整页渲染密度一致，1:1 像素对齐）。
    private void EmitHatchGradientRasterPdf(MemoryStream cs, string d, List<List<XY>> loops, HatchGradientPattern gp, HatchGradSpec gradSpec)
    {
        double zoom = _opt.OutputZoom > 0 ? _opt.OutputZoom : 1.0;
        // 渐变 bbox（PDF 画布系，Py y 向上不翻转）
        double cMinX = double.MaxValue, cMaxX = double.MinValue, cMinY = double.MaxValue, cMaxY = double.MinValue;
        foreach (var loop in loops)
            foreach (var p in loop)
            {
                double px = Sx(p.X), py = Py(p.Y);
                if (px < cMinX) cMinX = px; if (px > cMaxX) cMaxX = px;
                if (py < cMinY) cMinY = py; if (py > cMaxY) cMaxY = py;
            }
        int w = Math.Max(1, (int)Math.Round((cMaxX - cMinX) * zoom));
        int h = Math.Max(1, (int)Math.Round((cMaxY - cMinY) * zoom));
        // 渐变光栅图分辨率上限：渐变色块本身平滑，无需 1:1 画布像素（PDF 会以平滑插值放大显示）。
        // 旧值 4096 使 100 单位的渐变块生成 1000×1000 图、41 个渐变 → PDF 11MB；
        // 浏览器 PDF 查看器（Chrome/Edge）渲染大量大尺寸裁剪图像时负担重，
        // 部分块延迟/不绘制（用户反馈"pdf 缺了个样式 兼容看不到"）。降到 256 后文件约 1MB、兼容性大幅提升。
        const int MaxGradSide = 256;
        if (w > MaxGradSide || h > MaxGradSide || (long)w * h > 16_000_000L)
        {
            double f = Math.Min(Math.Min(MaxGradSide / (double)w, MaxGradSide / (double)h),
                                (long)w * h > 16_000_000L ? Math.Sqrt(16_000_000L / ((double)w * h)) : 1.0);
            w = Math.Max(1, (int)(w * f)); h = Math.Max(1, (int)(h * f));
        }
        var rc = new RasterCanvas(w, h);
        rc.Clear(0, 0, 0, 0); // 背景透明（裁剪外区域不绘制）
        // 轮廓 → 像素：局部原点=渐变 bbox 左下(PDF)，行 0=顶(PDF y 向上→像素 y 向下)
        double kw = w / Math.Max(1e-9, (cMaxX - cMinX));
        double kh = h / Math.Max(1e-9, (cMaxY - cMinY));
        var contours = new List<List<RasterCanvas.P>>();
        bool evenOdd = loops.Count > 1; // 多环用奇偶规则（与 PNG/SVG 一致）
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            var c = new List<RasterCanvas.P>(loop.Count);
            foreach (var p in loop)
            {
                double px = Sx(p.X), py = Py(p.Y);
                float lx = (float)((px - cMinX) * kw);          // 局部 x（0..w）
                float ly = (float)((cMaxY - py) * kh);          // 局部 y（0..h，顶=0）
                c.Add(new RasterCanvas.P(lx, ly));
            }
            contours.Add(c);
        }
        if (contours.Count == 0) { RecordSkip("HATCH(渐变光栅空)"); return; }
        // 渐变几何（PDF 画布系，Py y 向上→yDown=false，与 MakePdfShading 一致）
        double ang = gp.Angle;
        double ux = Math.Cos(ang), uy = Math.Sin(ang);
        var geom = ComputeHatchGradGeom(gradSpec, cMinX, cMinY, cMaxX, cMaxY, ux, uy, false);
        // 几何（画布系）→ 局部像素系：x=(X-cMinX)*kw，y=(cMaxY-Y)*kh
        (float lx, float ly) Loc(double X, double Y) => ((float)((X - cMinX) * kw), (float)((cMaxY - Y) * kh));
        var c1 = HexToRgb(StopHex(gradSpec.C1));
        var c2 = HexToRgb(StopHex(gradSpec.C2));
        var prof = new Func<float, float>(u => (float)HatchGradProfileLut(gradSpec, u));
        if (geom.Radial)
        {
            var (fx, fy) = Loc(geom.Fx, geom.Fy);
            float r = (float)(geom.R * kh); // 半径：y 像素/k 单位；x/y 像素密度相同(方形 bbox 假设)→用 kh
            rc.FillGradientProfile(contours, evenOdd, new RasterCanvas.P(0, 0), new RasterCanvas.P(0, 0),
                                   c1, c2, true, fx, fy, r, prof);
        }
        else
        {
            var (ax, ay) = Loc(geom.Px0, geom.Py0);
            var (bx, by) = Loc(geom.Px1, geom.Py1);
            rc.FillGradientProfile(contours, evenOdd, new RasterCanvas.P(ax, ay), new RasterCanvas.P(bx, by),
                                   c1, c2, false, 0, 0, 0, prof);
        }
        // 打包为 RGB（丢弃 alpha，裁剪外=透明，由路径裁剪决定可见区）
        var rgba = rc.ToRgbaBuffer();
        var raw = new byte[w * h * 3 + h];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // 每行 PNG Up 预测器 filter byte = 0
            int baseIdx = (y * w) * 4;
            for (int x = 0; x < w; x++)
            {
                int si = baseIdx + x * 4;
                raw[o++] = rgba[si]; raw[o++] = rgba[si + 1]; raw[o++] = rgba[si + 2];
            }
        }
        byte[] comp;
        using (var ms = new MemoryStream()) { using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(raw, 0, raw.Length); comp = ms.ToArray(); } // ZLibStream=RFC1950(zlib 头 0x78+adler32)，/FlateDecode 规范要求；DeflateStream 产裸 DEFLATE(RFC1951 无头)→查看器无法解码
        string name = "GIm" + _pdfXObjSeq++;
        _pdfXObjRegs.Add(new PdfXObjReg
        {
            Name = name,
            Build = (baseNum) =>
            {
                var head = "<< /Type /XObject /Subtype /Image /Width " + w + " /Height " + h +
                           " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
                           "/DecodeParms << /Predictor 15 /Colors 3 /BitsPerComponent 8 /Columns " + w + " >> /Length " + comp.Length + " >>\nstream\n";
                var body = new byte[head.Length + comp.Length + "\nendstream".Length];
                var hb = Encoding.ASCII.GetBytes(head);
                Buffer.BlockCopy(hb, 0, body, 0, hb.Length);
                Buffer.BlockCopy(comp, 0, body, hb.Length, comp.Length);
                var tb = Encoding.ASCII.GetBytes("\nendstream");
                Buffer.BlockCopy(tb, 0, body, hb.Length + comp.Length, tb.Length);
                return new List<(int, byte[])> { (baseNum, body) };
            }
        });
        // 内容流：路径裁剪 → 图像平移到 bbox 左下、缩放到 bbox 尺寸 → Do
        CsAscii(cs, "q\n" + d + "W n\n"
                   + F(cMaxX - cMinX) + " 0 0 " + F(cMaxY - cMinY) + " " + F(cMinX) + " " + F(cMinY) + " cm\n"
                   + "/" + name + " Do\nQ\n");
        _log("ℹ️  PDF 非线性渐变光栅化嵌入: " + (gp?.Name ?? "?") + " " + w + "×" + h);
    }

    // 计算 PDF shading 参数（坐标为 PDF 用户空间，y 向上）。
    // 接入共享 9 类型模型：径向(SPHERICAL/INVSPHERICAL/HEMISPHERICAL/INVHEMISPHERICAL)→Type3(焦点由居中决定)；
    // 轴向(LINEAR/CYLINDER/INVCYLINDER/CURVED/INVCURVED)→Type2(沿 460 角度，居中偏移)。
    // PDF 矢量 shading 仅支持双端线性插值(FunctionType 2)，剖面曲线(圆柱/球/抛物)无法表达→用端点近似。
    private (int ShadingType, string Coords, string C0, string C1) MakePdfShading(List<List<XY>> loops, HatchGradientPattern gp)
    {
        var spec = MakeHatchGradSpec(gp);
        // PDF 画布系 bbox（Py y 向上不翻转）
        double cMinX = double.MaxValue, cMaxX = double.MinValue, cMinY = double.MaxValue, cMaxY = double.MinValue;
        foreach (var loop in loops)
            foreach (var p in loop)
            {
                double px = Sx(p.X), py = Py(p.Y);
                if (px < cMinX) cMinX = px; if (px > cMaxX) cMaxX = px;
                if (py < cMinY) cMinY = py; if (py > cMaxY) cMaxY = py;
            }
        // PDF 画布系方向（y 向上：uy = +sin，yDown=false）
        double ang = gp.Angle;
        double ux = Math.Cos(ang), uy = Math.Sin(ang);
        var geom = ComputeHatchGradGeom(spec, cMinX, cMinY, cMaxX, cMaxY, ux, uy, false);
        string c1 = HexToRgbPdf(StopHex(spec.C1));
        string c2 = HexToRgbPdf(StopHex(spec.C2));
        if (geom.Radial)
            // Type3 径向：/Coords 必须 6 个数 [x0 y0 r0 x1 y1 r1]（起始圆→结束圆）。
            // 同心圆：起始=焦点处点(r0=0, t=0=C0)，结束=半径R同心圆(t=1=C1)，t 沿半径线性，
            // 与 PNG/SVG 的 t=dist(focus)/R 一致。旧实现写 8 个数(Fx Fy Fx Fy 0 0 R 0)违反规范
            // → 查看器解析 shading 失败、渐变不显示。
            return (3, $"{F(geom.Fx)} {F(geom.Fy)} 0 {F(geom.Fx)} {F(geom.Fy)} {F(geom.R)}", c1, c2);
        // Type2 轴向：/Coords 4 个数 [x0 y0 x1 y1]，端点 (Px0,Py0)→(Px1,Py1)
        return (2, $"{F(geom.Px0)} {F(geom.Py0)} {F(geom.Px1)} {F(geom.Py1)}", c1, c2);
    }

    // 子路径集合 → PDF 路径串（m/l/h）
    private static string BuildPathDPdf(List<List<XY>> loops, Func<double, double> fx, Func<double, double> fy)
    {
        var d = new StringBuilder();
        foreach (var loop in loops)
        {
            if (loop.Count == 0) continue;
            for (int i = 0; i < loop.Count; i++)
                d.Append($"{F(fx(loop[i].X))} {F(fy(loop[i].Y))} {(i == 0 ? "m" : "l")} ");
            d.Append("h ");
        }
        return d.ToString();
    }

    // 图案 HATCH（PDF）：裁剪线段描边
    private void EmitHatchPatternPdf(MemoryStream cs, Hatch ht, List<List<XY>> loops, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var segs = BuildPatternSegments(loops, ht, tr, MaxPatternLinesPerHatch);
        if (segs.Count == 0) { RecordSkip("HATCH(图案)"); return; }
        // 注意：PdfStroke 的参数契约是【hex】（内部会调 HexToRgbPdf 转换），
        // 所有其他调用者（ColorOf→AciColor）均传 hex。此处若预先转成 RGB 字符串
        // （"1 0.749 0"）再传入，PdfStroke 会二次 HexToRgbPdf → 非 #RRGGBB → 兜底白(1 1 1)
        // → 白底 PDF 上图案线不可见。必须传 hex（HatchSolidFillHex 返回 #RRGGBB）。
        string col = HatchSolidFillHex(ht, layerAci, byBlockAci);
        // 图案线宽为屏幕空间常量：不乘块缩放 scaleMul（与显示线宽同语义，2026-10-29 用户裁定）
        double w = 0.1;
        var d = new StringBuilder();
        foreach (var s in segs)
            d.Append(F(Sx(s.X1))).Append(' ').Append(F(Py(s.Y1))).Append(" m ")
             .Append(F(Sx(s.X2))).Append(' ').Append(F(Py(s.Y2))).Append(" l ");
        PdfStroke(cs, col, w, null, d.ToString() + "S");
    }

    // PDF 变宽折线：沿两侧按逐顶点宽度偏移生成外轮廓，闭合后填充（f 操作符）。
    // 含 bulge（弧段）时走 ImageEngine.TryBuildTaperOffset 弧采样偏移（与 SVG 同语义，T5.4 GC200 修复）。
    private void PdfTaperedPolyline(MemoryStream cs, LwPolyline pl, Transform2D tr, string hex, double[] dash)
    {
        var verts = pl.Vertices;
        int n = verts.Count;
        if (n < 2) return;
        // 逐顶点有效宽度：无 bulge 弦偏移路径用（顶点 i 宽 = 起宽40，为0时继承入段终宽41）
        double[] w = TaperedVertexWidths(pl);
        // 逐段起/终点宽：含 bulge 弧偏移路径用（段 i 从 40_i 渐变到 41_i，段间顶点收尖）
        var (sw, ew) = TaperedSegmentWidths(pl);

        List<(double X, double Y)> left, right;
        var taper = TryBuildTaperOffset(pl, sw, ew, tr, _opt.TaperOffsetBuilder);
        if (taper.Ok)
        {
            // 含 bulge：弧采样偏移轮廓
            left = taper.Left; right = taper.Right;
        }
        else
        {
            // 无 bulge：原逐顶点弦偏移路径（输出与旧实现逐字节一致）
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

        var d = new StringBuilder();
        int m = left.Count;
        if (pl.IsClosed)
        {
            // 圆盘特例（宽度 ≥ 弧半径，一偏移环退化为单点——典型 GC200 点块）：
            // 单条三次贝塞尔整圆 + 非零填充（f）——与 netDxf 路由 DxfImageExporter.AppendCircleBezier
            // 同法（样条曲线替换圆形）：PDF 无 arc 操作符，且 evenodd + 退化子路径组合在部分合规
            // 查看器中整条被忽略（圆盘不显示），贝塞尔整圆是最稳妥的标准写法。
            var disc = _opt.TaperOffsetBuilder?.TryCircleDisc(left, right);
            if (disc != null)
            {
                double cx = Sx(disc.Value.Cx), cy = Py(disc.Value.Cy), R = disc.Value.R;
                double k = 0.5522847498307936 * R; // κ = 4/3·tan(π/8)
                var dd = new StringBuilder();
                dd.Append(F(cx + R) + " " + F(cy) + " m ");
                dd.Append(F(cx + R) + " " + F(cy + k) + " " + F(cx + k) + " " + F(cy + R) + " " + F(cx) + " " + F(cy + R) + " c ");
                dd.Append(F(cx - k) + " " + F(cy + R) + " " + F(cx - R) + " " + F(cy + k) + " " + F(cx - R) + " " + F(cy) + " c ");
                dd.Append(F(cx - R) + " " + F(cy - k) + " " + F(cx - k) + " " + F(cy - R) + " " + F(cx) + " " + F(cy - R) + " c ");
                dd.Append(F(cx + k) + " " + F(cy - R) + " " + F(cx + R) + " " + F(cy - k) + " " + F(cx + R) + " " + F(cy) + " c h f");
                CsAscii(cs, HexToRgbPdf(hex) + " rg\n" + dd + "\n");
                return;
            }

            // 闭合折线：left/right 各一条封闭偏移轮廓（内/外环），
            // 两个子路径 + even-odd 填充（f*）得到"环"。
            for (int i = 0; i < m; i++) d.Append(F(Sx(left[i].X)) + " " + F(Py(left[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
            d.Append("h ");
            for (int i = 0; i < m; i++) d.Append(F(Sx(right[i].X)) + " " + F(Py(right[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
            d.Append("h f*");
        }
        else
        {
            // 开放折线：left 正向 + right 反向，两端闭合填充
            for (int i = 0; i < m; i++) d.Append(F(Sx(left[i].X)) + " " + F(Py(left[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
            for (int i = m - 1; i >= 0; i--) d.Append(F(Sx(right[i].X)) + " " + F(Py(right[i].Y)) + " l ");
            d.Append("h f");
        }
        CsAscii(cs, HexToRgbPdf(hex) + " rg\n" + d + "\n");
    }

    // ===== 内容流字节级写入辅助 =====
    private static void CsAscii(MemoryStream cs, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        cs.Write(b, 0, b.Length);
    }

    // PDF 字符串字面量（支持任意二进制字节 —— 2 字节 GID 文本）。
    // 必须转义：\ ( ) 以及 CR/LF（PDF 规范：字符串字面量中的换行序列会被查看器忽略，
    // 而 GID 字节可能恰好是 0x0D/0x0A，不转义会丢字节导致文字错位）。
    private static void CsString(MemoryStream cs, byte[] data)
    {
        CsAscii(cs, "(");
        foreach (byte b in data)
        {
            if (b == 0x28) CsAscii(cs, "\\(");
            else if (b == 0x29) CsAscii(cs, "\\)");
            else if (b == 0x5C) CsAscii(cs, "\\\\");
            else if (b == 0x0A) CsAscii(cs, "\\012");
            else if (b == 0x0D) CsAscii(cs, "\\015");
            else cs.WriteByte(b);
        }
        CsAscii(cs, ")");
    }

    // dash 为虚线模式（数据单位）；null/空 = 实线。PDF 用 d 操作符设置虚线（[数组] 相位 d）。
    // 注意：d 状态在内容流中持久，实线必须显式写 [] 0 d 复位，
    // 否则会继承前一条虚线的 dash 参数（旧版实线漏复位 → 虚线之后的实线被画成虚线）。
    private static string DashOp(double[] dash)
    {
        if (dash == null || dash.Length == 0) return "[] 0 d\n";
        var sb = new StringBuilder("[");
        for (int i = 0; i < dash.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(F(dash[i]));
        }
        sb.Append("] 0 d\n");
        return sb.ToString();
    }

    private static void PdfStroke(MemoryStream cs, string hex, double w, double[] dash, string op)
    {
        CsAscii(cs, HexToRgbPdf(hex) + " RG\n" + F(w) + " w\n" + DashOp(dash) + op + "\n");
    }

    // 整圆/整椭圆：三次贝塞尔逼近，全部使用 PDF 标准路径操作符（m/l/c/h）。
    // 注意：PDF 规范里没有 arc/circle 路径操作符——旧版写的 "x y r 0 360 arc"
    // 不是合法操作符，Acrobat / Chrome(PDFium) 等合规查看器直接忽略，
    // 这是 GC117 等点块的圆圈和点在 PDF 中整体不显示的根因。
    private static void AppendCircleBezier(StringBuilder d, double cx, double cy, double r)
    {
        double k = 0.5522847498307936 * r;
        d.Append(F(cx + r) + " " + F(cy) + " m ");
        d.Append(F(cx + r) + " " + F(cy + k) + " " + F(cx + k) + " " + F(cy + r) + " " + F(cx) + " " + F(cy + r) + " c ");
        d.Append(F(cx - k) + " " + F(cy + r) + " " + F(cx - r) + " " + F(cy + k) + " " + F(cx - r) + " " + F(cy) + " c ");
        d.Append(F(cx - r) + " " + F(cy - k) + " " + F(cx - k) + " " + F(cy - r) + " " + F(cx) + " " + F(cy - r) + " c ");
        d.Append(F(cx + k) + " " + F(cy - r) + " " + F(cx + r) + " " + F(cy - k) + " " + F(cx + r) + " " + F(cy) + " c h ");
    }

    // 点样式（$PDMODE/$PDSIZE）→ PDF 内容流，与 SVG 端 EmitPoint 逐样式一致（2026-10-16 定稿标准 + 10-17/10-26 两次各缩小一半）：
    //   ① 笔画与直线一致（lw，1 像素不加粗）；② 框半径 = 点大小值 × 0.25（数据单位，2026-10-26 用户“边框再减半”）
    //   ③ 方框半宽/圆框半径 = 同一半径；④ 加号半伸出（沿轴）= 2r；X 臂长（沿 45° 对角）= 2r（尖在 ±r√2，伸出圆外一 r）；竖线总长 = r（第 5 列）
    //   位0-2：0=点(实心小圆) 1=不显示 2=加号 3=X 4=竖线；位5(32)=外加圆框；位6(64)=外加方框
    // 填充色必须显式 rg（PdfStroke 只设 RG 描边色，填充沿用上一条残留状态会致颜色不确定）；
    // 描边一律用合法路径操作符（"0 360 arc" 非 PDF 标准，合规查看器直接忽略导致点/框消失）。
    private void EmitPointPdf(MemoryStream cs, double cx, double cy, string color, double lw)
    {
        // 2026-10-26 用户裁定：$PDSIZE=0 不再抑制点——中心点恒按基础 1 像素（0.5pt 半径，1px≙1pt）绘制，
        // 不受 PDSIZE/缩放影响；外框/臂按设置比例（PointGlyphRadius）绘制；PDMODE=1 且无外框 → 不绘制。
        int mode = _pdMode;
        int shape = mode & 7;
        bool hasFrame = (mode & 96) != 0;
        if (shape == 1 && !hasFrame) return;       // PDMODE=1：无中心图形也无外框 → 完全无显示
                                                      // 位0=1 但带外框（33/65/97）= 只画外框、无中心图形
        double r = PointGlyphRadius();                // 框半径（数据单位）= 点大小值
        if (!double.IsFinite(r) || r <= 0) return;
        double w = lw;                                // 笔画与直线一致，不加粗
        double arm = r * 2.0;                         // 加号半伸出（沿轴）= 2r（伸出框外一 r）
        double xarm = r * Math.Sqrt(2.0);             // X 臂尖坐标偏移 = 2r/√2：沿 45° 对角臂长恰为 2r，伸出圆外恰一 r（2026-10-26 用户修正）
        double scx = Sx(cx), scy = Py(cy);
        double x0 = Sx(cx - r), x1 = Sx(cx + r), y0 = Py(cy - r), y1 = Py(cy + r);
        double ax0 = Sx(cx - arm), ax1 = Sx(cx + arm), ay0 = Py(cy - arm), ay1 = Py(cy + arm);
        double xax0 = Sx(cx - xarm), xax1 = Sx(cx + xarm), xay0 = Py(cy - xarm), xay1 = Py(cy + xarm);
        string rgb = HexToRgbPdf(color);
        // 外加圆框（位5=32；对话框第 2 行）：贝塞尔整圆描边，半径 = 点大小值
        if ((mode & 32) != 0)
        {
            var d = new StringBuilder();
            AppendCircleBezier(d, scx, scy, Math.Abs(x1 - scx));
            PdfStroke(cs, color, w, null, d + "S");
        }
        // 外加方框（位6=64；对话框第 3 行）：矩形闭合路径描边
        if ((mode & 64) != 0)
        {
            var d = new StringBuilder();
            d.Append(F(x0)).Append(' ').Append(F(y0)).Append(" m ")
             .Append(F(x0)).Append(' ').Append(F(y1)).Append(" l ")
             .Append(F(x1)).Append(' ').Append(F(y1)).Append(" l ")
             .Append(F(x1)).Append(' ').Append(F(y0)).Append(" l h ");
            PdfStroke(cs, color, w, null, d + "S");
        }
        // 中心图形
        switch (shape)
        {
            case 0: // 点：基础 1 像素小实心圆（2026-10-26 用户裁定：中心点恒 1px 屏幕直径 = 0.5pt 半径，
            // 不随 PDSIZE/缩放变化，纯填充无背景光晕；仅外框/臂随 PointGlyphRadius 比例缩放）
            {
                var d = new StringBuilder();
                AppendCircleBezier(d, scx, scy, 0.5);
                CsAscii(cs, rgb + " rg\n" + d + "f\n");
                break;
            }
            case 2: // 加号：横竖两笔（±2r，伸出框外一 r）
            {
                var d = new StringBuilder();
                d.Append(F(ax0)).Append(' ').Append(F(scy)).Append(" m ").Append(F(ax1)).Append(' ').Append(F(scy)).Append(" l ")
                 .Append(F(scx)).Append(' ').Append(F(ay0)).Append(" m ").Append(F(scx)).Append(' ').Append(F(ay1)).Append(" l ");
                PdfStroke(cs, color, w, null, d + "S");
                break;
            }
            case 3: // X：两条对角线（臂长沿 45° 对角 = 2r，尖在 ±r√2，伸出圆外一 r）
            {
                var d = new StringBuilder();
                d.Append(F(xax0)).Append(' ').Append(F(xay0)).Append(" m ").Append(F(xax1)).Append(' ').Append(F(xay1)).Append(" l ")
                 .Append(F(xax0)).Append(' ').Append(F(xay1)).Append(" m ").Append(F(xax1)).Append(' ').Append(F(xay0)).Append(" l ");
                PdfStroke(cs, color, w, null, d + "S");
                break;
            }
            case 4: // 竖线（总长 = r；2026-10-16 用户定稿：第 5 列竖线长度为 r（点大小值）；10-17 缩小一半后 = 点大小值/2）
            {
                double vh = r * 0.5;
                var d = new StringBuilder();
                d.Append(F(scx)).Append(' ').Append(F(Py(cy - vh))).Append(" m ").Append(F(scx)).Append(' ').Append(F(Py(cy + vh))).Append(" l ");
                PdfStroke(cs, color, w, null, d + "S");
                break;
            }
        }
    }

    // 椭圆：8 段（每段 45°）三次贝塞尔逼近（y 向上坐标系，旋转角与 CAD 同向，直接取正）。
    // 控制点偏移 = k·r(t)·单位切向量，r(t)=√(a²sin²t+b²cos²t) 为该参数处的"局部半轴"
    //（圆时退化为半径，k=4/3·tan(Δ/4)，Δ 为段角）。
    private static void PdfEllipse(MemoryStream cs, string hex, double w, double[] dash, double cx, double cy, double rx, double ry, double rotDeg)
    {
        CsAscii(cs, HexToRgbPdf(hex) + " RG\n" + F(w) + " w\n" + DashOp(dash));
        if (rx <= 0 || ry <= 0) return;
        double a = rotDeg * Math.PI / 180.0;
        double ca = Math.Cos(a), sa = Math.Sin(a);
        void P(double t, out double x, out double y)
        {
            double ex = rx * Math.Cos(t), ey = ry * Math.Sin(t);
            x = cx + ex * ca - ey * sa;
            y = cy + ex * sa + ey * ca;
        }
        void T(double t, out double tx, out double ty)
        {
            double vx = -rx * Math.Sin(t), vy = ry * Math.Cos(t);
            double len = Math.Sqrt(vx * vx + vy * vy);
            if (len <= 0) { tx = 0; ty = 0; return; }
            tx = (vx * ca - vy * sa) / len;
            ty = (vx * sa + vy * ca) / len;
        }
        int segs = 8;
        double da = 2 * Math.PI / segs;
        double k = 4.0 / 3.0 * Math.Tan(da / 4.0);
        var d = new StringBuilder();
        P(0, out double x0, out double y0);
        d.Append(F(x0) + " " + F(y0) + " m ");
        for (int i = 0; i < segs; i++)
        {
            double t1 = da * (i + 1);
            P(t1, out double x1, out double y1);
            double r0 = Math.Sqrt(Math.Pow(rx * Math.Sin(da * i), 2) + Math.Pow(ry * Math.Cos(da * i), 2));
            double r1 = Math.Sqrt(Math.Pow(rx * Math.Sin(t1), 2) + Math.Pow(ry * Math.Cos(t1), 2));
            T(da * i, out double t0x, out double t0y);
            T(t1, out double t1x, out double t1y);
            double c1x = x0 + k * r0 * t0x, c1y = y0 + k * r0 * t0y;
            double c2x = x1 - k * r1 * t1x, c2y = y1 - k * r1 * t1y;
            d.Append(F(c1x) + " " + F(c1y) + " " + F(c2x) + " " + F(c2y) + " " + F(x1) + " " + F(y1) + " c ");
            x0 = x1; y0 = y1;
        }
        CsAscii(cs, d + "h S\n");
    }

    // 弧：CAD 弧恒为从起始角到终止角逆时针（end<start 时跨过 360°）。
    // 用三次贝塞尔逼近（每段 ≤90°）——arcto 只接受 5 个操作数（x y r start end），
    // 且只能逆时针；旧版多写了一个 sweep 操作数，残留操作数污染其后所有指令。
    private static void PdfArc(MemoryStream cs, string hex, double w, double[] dash, double cx, double cy, double r, double startDeg, double endDeg)
    {
        CsAscii(cs, HexToRgbPdf(hex) + " RG\n" + F(w) + " w\n" + DashOp(dash));
        if (r <= 0) return;
        double s = startDeg * Math.PI / 180.0;
        double e = endDeg * Math.PI / 180.0;
        double span = e - s;
        while (span < 0) span += 2 * Math.PI;
        while (span >= 2 * Math.PI) span -= 2 * Math.PI;
        if (span < 1e-6)
        {
            // start==end 视为整圆：贝塞尔逼近（PDF 无 arc 操作符，旧写法被合规查看器忽略）
            var dFull = new StringBuilder();
            AppendCircleBezier(dFull, cx, cy, r);
            CsAscii(cs, dFull + "S\n");
            return;
        }
        int segs = Math.Max(1, (int)Math.Ceiling(span / (Math.PI / 2))); // 每段 ≤90°
        double a0 = s;
        double x0 = cx + r * Math.Cos(a0), y0 = cy + r * Math.Sin(a0);
        var d = new StringBuilder();
        d.Append(F(x0) + " " + F(y0) + " m ");
        for (int i = 0; i < segs; i++)
        {
            double a1 = s + span * (i + 1) / segs;
            double x1 = cx + r * Math.Cos(a1), y1 = cy + r * Math.Sin(a1);
            double da = span / segs;
            double k = 4.0 / 3.0 * Math.Tan(da / 4.0);
            // 逆时针单位切向量
            double t0x = -Math.Sin(a0), t0y = Math.Cos(a0);
            double t1x = -Math.Sin(a1), t1y = Math.Cos(a1);
            double c1x = x0 + k * r * t0x, c1y = y0 + k * r * t0y;
            double c2x = x1 - k * r * t1x, c2y = y1 - k * r * t1y;
            d.Append(F(c1x) + " " + F(c1y) + " " + F(c2x) + " " + F(c2y) + " " + F(x1) + " " + F(y1) + " c ");
            a0 = a1; x0 = x1; y0 = y1;
        }
        CsAscii(cs, d + "S\n");
    }

    // 文字：Tm 必须给完整 6 参数文本矩阵 [cos sin -sin cos x y]（旧版只写 3 个参数，
    // 同样污染操作数栈）。文字串为 2 字节 GID（Identity-H）或回退 ASCII。
    // R18：宽度因子（DXF 41）→ Tm = [cr·WF, sr·WF, −sr, cr, x, y]（文字局部系先水平压缩再旋转；WF=1 逐字节不变）。
    private void PdfText(MemoryStream cs, string hex, double fs, double x, double y, double rotDeg, string text, int slot, double widthFactor = 1.0)
    {
        if (string.IsNullOrEmpty(text)) return;
        CjkFont font = _pdfFonts != null ? _pdfFonts.FontAt(slot) : null;
        byte[] data = font != null ? font.Encode(text) : PdfFallbackTextBytes(text);
        if (data.Length == 0) return;
        double r = rotDeg * Math.PI / 180.0;
        double cr = Math.Cos(r), sr = Math.Sin(r);
        CsAscii(cs, "BT\n/F" + slot + " " + F(fs) + " Tf\n" + HexToRgbPdf(hex) + " rg\n" +
                   F(cr * widthFactor) + " " + F(sr * widthFactor) + " " + F(-sr) + " " + F(cr) + " " + F(x) + " " + F(y) + " Tm\n");
        CsString(cs, data);
        CsAscii(cs, " Tj\nET\n");
    }

    // 无 CJK 字体时的回退：保留可打印 ASCII，其余替换为 "?"
    private static byte[] PdfFallbackTextBytes(string text)
    {
        var b = new byte[text.Length];
        int n = 0;
        foreach (char ch in text) b[n++] = (byte)(ch >= 32 && ch < 127 ? ch : '?');
        return b;
    }

    private void PdfQuadFill(MemoryStream cs, string hex, List<(double X, double Y)> pts)
    {
        if (pts.Count < 3) return;
        var d = new StringBuilder();
        for (int i = 0; i < pts.Count; i++) d.Append(F(Sx(pts[i].X)) + " " + F(Py(pts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
        d.Append("h ");
        CsAscii(cs, HexToRgbPdf(hex) + " rg\n" + d + "f\n");
    }

    // SVG hex "#RRGGBB" → PDF RGB 分量串 "r g b"（0..1）
    private static string HexToRgbPdf(string hex)
    {
        if (hex == null || hex.Length != 7 || hex[0] != '#') return "1 1 1";
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte b = Convert.ToByte(hex.Substring(5, 2), 16);
        // 与参考实现 DxfImageExporter.HexToRgbPdf 逐字节一致：最多 3 位小数、去尾零（0.###），
        // 不是 6 位小数（PDF 颜色行 0.647 0 0.161 vs 0.647059 0 0.160784 的 11 万行差异根因）。
        return (r / 255.0).ToString("0.###", CultureInfo.InvariantCulture) + " "
             + (g / 255.0).ToString("0.###", CultureInfo.InvariantCulture) + " "
             + (b / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
    }
    #endregion
}
