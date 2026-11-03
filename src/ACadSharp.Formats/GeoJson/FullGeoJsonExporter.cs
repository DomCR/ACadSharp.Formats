using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.XData;
using CSMath;

namespace ACadSharp.Formats.GeoJson;

/// <summary>
/// CAD → GeoJSON 导出器（FeatureCollection，2026-11-02 cad2geojson 需求）。
/// 遍历模型空间（INSERT 块展开 / DIMENSION 块展开 / 嵌套块循环引用截断），实体 → GeoJSON 几何：
///   LINE/LWPOLYLINE/POLYLINE3D/ARC/CIRCLE/ELLIPSE/SPLINE/SOLID/HATCH → 线/面（弧与样条按 ArcSampleStep/AngleSampleStepDeg 采样，与渲染引擎同口径）
///   POINT → Point，或按 $PDMODE/$PDSIZE 炸开成点/线/面组合（PointMode="glyph"）
///   TEXT/MTEXT/ATTRIB → 锚点 Point（TextMode="anchor"）或 包围盒面+基线线组合（TextMode="outline"）
///   INSERT（点块）→ 炸开块内容（ExplodeInserts=true 默认）或 插入点单个 Point（ExplodeInserts=false）
///   RASTERIMAGE → 四角 Polygon；WIPEOUT → 裁剪多边形
/// 扩展属性：feature 属性逐项开关（GeoJsonPropertyOptions：layer/color/entity/handle/linetype/linetype_scale/
///   line_weight/text/height/width_factor/text_style/text_font/halign/valign/rotation_deg/xdata，全部默认开）。
/// XData 序列化进 xdata 属性：[app → items[组码,值]] 结构（组码 1001/1002 省略；二进制 Base64；句柄十六进制）。
/// 流式导出：feature 逐个写入 FileStream（内存 O(1)，超大图纸不限），cad_meta（范围/计数）在文件尾写入；
///   outPath 为 null = 仅统计不写文件。
/// 进度：GeoJsonConfiguration.Progress（与 PNG/SVG/PDF 同一 ImageProgress 类型）。
/// 坐标 = 模型空间 XY（Y 向上，与 GeoJSON 惯例一致），Z 投影丢弃。
/// </summary>
public static class FullGeoJsonExporter
{
	/// <summary>导出 GeoJSON；outPath 为 null = 仅统计（范围/类型计数），不写文件。</summary>
	public static GeoJsonExportResult Export(CadDocument doc, string outPath, GeoJsonConfiguration options = null, string sourceFilePath = null)
	{
		options ??= new GeoJsonConfiguration();
		var r = new GeoJsonExportResult();
		bool write = !string.IsNullOrEmpty(outPath);
		if (doc == null || doc.ModelSpace == null || doc.ModelSpace.Entities.Count == 0)
		{
			r.Success = true;
			if (write)
			{
				EnsureDir(outPath);
				File.WriteAllText(outPath, EmptyCollectionJson(sourceFilePath), new UTF8Encoding(false));
				r.OutputPath = outPath;
				r.OutputBytes = new FileInfo(outPath).Length;
			}
			return r;
		}

		StreamWriter sw = null;
		try
		{
			if (write)
			{
				EnsureDir(outPath);
				// 流式写盘：1MB 缓冲 UTF-8（无 BOM）；feature 逐个写出，内存与图纸规模无关
				sw = new StreamWriter(new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None), new UTF8Encoding(false), 1 << 20);
				sw.Write("{\"type\":\"FeatureCollection\",\"features\":[");
			}
			var w = new Walker(doc, options, sw, r);
			w.WalkTopLevel();
			if (sw != null)
			{
				sw.Write("],\"cad_meta\":");
				w.WriteMeta(sw, sourceFilePath);
				sw.Write('}');
				sw.Flush();
			}
		}
		catch (Exception ex)
		{
			if (sw != null) { try { sw.Dispose(); } catch { } try { File.Delete(outPath); } catch { } sw = null; }
			r.Success = false;
			options.Log?.Invoke("GeoJSON 导出失败：" + ex.Message);
			return r;
		}
		finally
		{
			if (sw != null) { try { sw.Dispose(); } catch { } }
		}

		r.Success = true;
		if (write) { r.OutputPath = outPath; r.OutputBytes = new FileInfo(outPath).Length; }
		Report(options, "done", 1.0, r.FeatureCount, r.FeatureCount, outPath ?? "stats");
		return r;
	}

	private static void EnsureDir(string outPath)
	{
		var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
		if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
			Directory.CreateDirectory(dir);
	}

	private static string EmptyCollectionJson(string source)
		=> "{\"type\":\"FeatureCollection\",\"features\":[],\"cad_meta\":{\"source\":\"" + (source ?? "") + "\",\"feature_count\":0}}";

	private static void Report(GeoJsonConfiguration opt, string phase, double pct, int cur, int total, string msg)
	{
		var cb = opt?.Progress;
		if (cb == null) return;
		try { cb(new Image.ImageProgress { Phase = phase, Percent = pct, Current = cur, Total = total, Message = msg }); }
		catch { /* 进度回调异常不打断导出 */ }
	}

	// ============================== 遍历器（流式） ==============================

	private sealed class Walker
	{
		private readonly CadDocument _doc;
		private readonly GeoJsonConfiguration _opt;
		private readonly GeoJsonPropertyOptions _po;
		private readonly StreamWriter _sw;      // null = 统计模式（不写几何）
		private readonly GeoJsonExportResult _r;
		private readonly HashSet<BlockRecord> _ancestors = new(); // INSERT 循环防护
		internal readonly Dictionary<string, int> _skipped = new(StringComparer.OrdinalIgnoreCase);
		internal readonly Dictionary<string, long> _typeCounts = new(StringComparer.OrdinalIgnoreCase);
		internal int _skippedTotal, _xdataCount, _featureCount, _topTotal, _lastPct;
		private double _minX = double.MaxValue, _minY = double.MaxValue;
		private double _maxX = double.MinValue, _maxY = double.MinValue;
		private bool _hasExt;
		private int _rounding;
		private int _pdmode;
		private double _psize;

		public Walker(CadDocument doc, GeoJsonConfiguration opt, StreamWriter sw, GeoJsonExportResult r)
		{
			_doc = doc; _opt = opt; _po = opt.Properties ?? new GeoJsonPropertyOptions(); _sw = sw; _r = r;
			_rounding = Math.Clamp(opt.CoordinateRounding, 0, 12);
			try
			{
				if (doc.Header != null)
				{
					_pdmode = doc.Header.PointDisplayMode;   // $PDMODE
					_psize = doc.Header.PointDisplaySize;    // $PDSIZE
				}
			}
			catch { }
		}

		public void WalkTopLevel()
		{
			var ents = _doc.ModelSpace.Entities.ToList(); // 引用列表拷贝（进度按序号）
			_topTotal = ents.Count;
			int step = Math.Max(1, _topTotal / 200); // 约每 0.5% 报一次
			for (int i = 0; i < _topTotal; i++)
			{
				var ent = ents[i];
				string t = ent?.GetType().Name ?? "null";
				_typeCounts[t] = _typeCounts.TryGetValue(t, out var cc) ? cc + 1 : 1;
				int pct = (int)(100.0 * (i + 1) / Math.Max(1, _topTotal));
				if (i % step == 0 || i == _topTotal - 1 || pct != _lastPct)
				{
					_lastPct = pct;
					Report(_opt, "walk", 0.05 + 0.93 * (i + 1.0) / Math.Max(1, _topTotal), i + 1, _topTotal, t);
				}
				Emit(ent, null, 7);
			}
			_hasExt = _maxX >= _minX && _maxY >= _minY;
			// 收尾：计数回填结果
			_r.FeatureCount = _featureCount;
			_r.XDataFeatureCount = _xdataCount;
			_r.HasExtents = _hasExt;
			if (_hasExt) { _r.DataX0 = _minX; _r.DataX1 = _maxX; _r.DataY0 = _minY; _r.DataY1 = _maxY; }
			_r.Skipped = _skipped;
			_r.SkippedTotal = _skippedTotal;
			_r.TypeCounts = _typeCounts;
		}

		public void WriteMeta(StreamWriter sw, string source)
		{
			sw.Write('{');
			sw.Write("\"source\":" + JStr(source ?? "") + ",");
			sw.Write("\"generated\":" + JStr(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + ",");
			sw.Write("\"units\":\"model space (CAD units)\",");
			sw.Write("\"coordinate\":\"[x,y] = model XY, Y up, Z dropped\",");
			sw.Write("\"xdata_schema\":\"[{app, items:[[group_code, value],...]}]\",");
			if (_hasExt)
				sw.Write("\"extents\":{\"x0\":" + C(_minX) + ",\"x1\":" + C(_maxX) + ",\"y0\":" + C(_minY) + ",\"y1\":" + C(_maxY) + "},");
			sw.Write("\"feature_count\":" + _featureCount + ",");
			sw.Write("\"xdata_feature_count\":" + _xdataCount + ",");
			sw.Write("\"skipped_entity_count\":" + _skippedTotal);
			if (_skipped.Count > 0)
			{
				sw.Write(",\"skipped\":{");
				bool first = true;
				foreach (var kv in _skipped)
				{
					if (!first) sw.Write(",");
					first = false;
					sw.Write(JStr(kv.Key));
					sw.Write(":" + kv.Value);
				}
				sw.Write('}');
			}
			sw.Write('}');
		}

		// ==================== 实体分派 ====================

		private void Emit(Entity ent, T2D? tr, short byBlockAci)
		{
			if (ent == null) return;
			string type = ent.GetType().Name;

			// ---- 颜色/图层（与渲染引擎 EffectiveAci 语义一致）----
			string layerName = null;
			short layerAci = 7;
			try
			{
				if (ent.Layer != null)
				{
					layerName = ent.Layer.Name;
					var lc = ent.Layer.Color;
					layerAci = (short)(lc.IsTrueColor ? lc.GetApproxIndex() : (lc.Index >= 1 && lc.Index <= 255 ? lc.Index : 7));
				}
			}
			catch { }

			ACadSharp.Color ec = ent.Color;
			int color; string colorBy; int? trueColor = null;
			try
			{
				if (ec.IsTrueColor)
				{
					color = ec.GetApproxIndex(); colorBy = "truecolor"; trueColor = ec.TrueColor;
				}
				else if (ec.IsByLayer) { color = layerAci; colorBy = "layer"; }
				else if (ec.IsByBlock) { color = byBlockAci; colorBy = "byblock"; }
				else { color = ec.Index; colorBy = "entity"; }
			}
			catch { color = 7; colorBy = "entity"; }

			try
			{
				// INSERT（点块）：炸开 or 单点（2026-11-02 用户要求）
				if (ent is Insert ins)
				{
					if (_opt.ExplodeInserts && ins.Block != null) { EmitInsert(ins, tr, byBlockAci); return; }
					var q = P(ins.InsertPoint, tr);
					BBox(q);
					var p0 = NewProps(layerName, color, colorBy, trueColor, type, ent.Handle);
					p0.Add("point", "true");
					p0.Add("insert", "true");
					try { p0.Add("block", JStr(ins.Block?.Name ?? "")); } catch { }
					Finalize(p0, ent, "Point;" + Pt(q));
					return;
				}

				if (ent is Dimension dim) { if (_opt.IncludeDimensions) { EmitDimension(dim, byBlockAci); return; } Skip(type); return; }

				var p = NewProps(layerName, color, colorBy, trueColor, type, ent.Handle);

				if (ent is Line ln)
				{
					var a = P(ln.StartPoint, tr);
					var b = P(ln.EndPoint, tr);
					BBox(a); BBox(b);
					Finalize(p, ent, "LineString;" + Pt(a) + "|" + Pt(b));
				}
				else if (ent is LwPolyline lp)
				{
					var pts = SampleLwPolyline(lp, tr);
					if (pts.Count >= 2)
					{
						foreach (var q in pts) BBox(q);
						Finalize(p, ent, lp.IsClosed && pts.Count >= 4
							? "Polygon;" + Ring(pts)
							: "LineString;" + Line(pts));
					}
					else Skip(type);
				}
				else if (ent is Polyline3D p3)
				{
					var pts = new List<(double X, double Y)>(p3.Vertices.Count);
					foreach (var v in p3.Vertices)
					{
						if (v == null || v.Flags.HasFlag(VertexFlags.SplineFrameControlPoint)) continue;
						pts.Add(P(v.Location, tr));
					}
					if (p3.IsClosed && pts.Count >= 3) pts.Add(pts[0]);
					if (pts.Count >= 2)
					{
						foreach (var q in pts) BBox(q);
						Finalize(p, ent, p3.IsClosed && pts.Count >= 4
							? "Polygon;" + Ring(pts)
							: "LineString;" + Line(pts));
					}
					else Skip(type);
				}
				else if (ent is Arc a) // 必须先于 Circle（Arc : Circle）
				{
					var pts = SampleArc(a.Center, a.Radius, a.StartAngle, a.EndAngle, ccw: true, tr, full: false);
					if (pts.Count >= 2)
					{
						foreach (var q in pts) BBox(q);
						bool full = Math.Abs(SpanNorm(a.EndAngle - a.StartAngle) - 2 * Math.PI) < 1e-6;
						Finalize(p, ent, full && pts.Count >= 4
							? "Polygon;" + Ring(pts)
							: "LineString;" + Line(pts));
					}
					else Skip(type);
				}
				else if (ent is Circle c)
				{
					var pts = SampleArc(c.Center, c.Radius, 0, 2 * Math.PI, ccw: true, tr, full: true);
					if (pts.Count >= 3) { foreach (var q in pts) BBox(q); Finalize(p, ent, "Polygon;" + Ring(pts)); }
					else Skip(type);
				}
				else if (ent is Ellipse el)
				{
					var pts = SampleEllipseEntity(el, tr);
					if (pts.Count >= 3) { foreach (var q in pts) BBox(q); Finalize(p, ent, "Polygon;" + Ring(pts)); }
					else Skip(type);
				}
				else if (ent is Spline sp)
				{
					var pts = SampleSpline(sp, tr, 16);
					if (pts.Count >= 2)
					{
						foreach (var q in pts) BBox(q);
						Finalize(p, ent, sp.IsClosed && pts.Count >= 4
							? "Polygon;" + Ring(pts)
							: "LineString;" + Line(pts));
					}
					else Skip(type);
				}
				else if (ent is Solid sol)
				{
					var c1 = P(sol.FirstCorner, tr);
					var c2 = P(sol.SecondCorner, tr);
					var c3 = P(sol.ThirdCorner, tr);
					var c4 = P(sol.FourthCorner, tr);
					foreach (var q in new[] { c1, c2, c3, c4 }) BBox(q);
					var ring = new List<(double X, double Y)> { c1, c2, c3 };
					if (Math.Abs(c4.X - c1.X) > 1e-9 || Math.Abs(c4.Y - c1.Y) > 1e-9)
						if (Math.Abs(c4.X - c3.X) > 1e-9 || Math.Abs(c4.Y - c3.Y) > 1e-9) ring.Add(c4);
					ring.Add(c1);
					if (ring.Count >= 4) Finalize(p, ent, "Polygon;" + Ring(ring));
					else Skip(type);
				}
				else if (ent is Hatch ht)
				{
					if (!EmitHatch(ht, tr, p, ent)) Skip(type);
				}
				else if (ent is Point pt)
				{
					var q = P(pt.Location, tr);
					BBox(q);
					if (string.Equals(_opt.PointMode, "glyph", StringComparison.OrdinalIgnoreCase))
						EmitPointGlyph(pt, q, layerName, color, colorBy, trueColor, ent.Handle);
					else
					{
						p.Add("point", "true");
						Finalize(p, ent, "Point;" + Pt(q));
					}
				}
				else if (ent is MText mt)
				{
					EmitText(p, ent,
						anchor: (Math.Abs(mt.AlignmentPoint.X) > 1e-9 || Math.Abs(mt.AlignmentPoint.Y) > 1e-9 ? mt.AlignmentPoint : mt.InsertPoint),
						text: mt.PlainText,
						height: Math.Abs(mt.Height),
						wf: 1.0,
						rot: mt.Rotation,
						style: mt.Style,
						halign: mt.AttachmentPoint.ToString(), valign: null,
						tag: null, tr: tr);
				}
				else if (ent is AttributeEntity at)
				{
					EmitText(p, ent,
						anchor: at.InsertPoint,
						text: at.Value ?? "",
						height: Math.Abs(at.Height),
						wf: at.WidthFactor,
						rot: at.Rotation,
						style: at.Style,
						halign: at.HorizontalAlignment.ToString(), valign: at.VerticalAlignment.ToString(),
						tag: at.Tag, tr: tr);
				}
				else if (ent is TextEntity tx)
				{
					EmitText(p, ent,
						anchor: tx.InsertPoint,
						text: tx.Value ?? "",
						height: Math.Abs(tx.Height),
						wf: tx.WidthFactor,
						rot: tx.Rotation,
						style: tx.Style,
						halign: tx.HorizontalAlignment.ToString(), valign: tx.VerticalAlignment.ToString(),
						tag: null, tr: tr);
				}
				else if (ent is RasterImage ri)
				{
					// 四角 = InsertPoint + U·Size.X / V·Size.Y
					var a0 = P(ri.InsertPoint, tr);
					var a10 = P(new XYZ(ri.InsertPoint.X + ri.UVector.X * ri.Size.X, ri.InsertPoint.Y + ri.UVector.Y * ri.Size.X, 0), tr);
					var a01 = P(new XYZ(ri.InsertPoint.X + ri.VVector.X * ri.Size.Y, ri.InsertPoint.Y + ri.VVector.Y * ri.Size.Y, 0), tr);
					var a11 = (X: a10.X + (a01.X - a0.X), Y: a10.Y + (a01.Y - a0.Y));
					foreach (var q in new[] { a0, a10, a01, a11 }) BBox(q);
					p.Add("raster_image", "true");
					Finalize(p, ent, "Polygon;" + Pt(a0) + "|" + Pt(a10) + "|" + Pt(a11) + "|" + Pt(a01) + "|" + Pt(a0));
				}
				else if (ent is Wipeout wo)
				{
					if (wo.ClipBoundaryVertices == null || wo.ClipBoundaryVertices.Count < 3) { Skip(type); return; }
					// 与 fork GetBoundingBox 一致：world = InsertPoint + (x+0.5)·U + (0.5−y)·V（图像归一化坐标）
					var pts = new List<(double X, double Y)>(wo.ClipBoundaryVertices.Count);
					foreach (var bv in wo.ClipBoundaryVertices)
					{
						double wx = wo.InsertPoint.X + wo.UVector.X * (bv.X + 0.5) + wo.VVector.X * (0.5 - bv.Y);
						double wy = wo.InsertPoint.Y + wo.UVector.Y * (bv.X + 0.5) + wo.VVector.Y * (0.5 - bv.Y);
						pts.Add(P(new XYZ(wx, wy, 0), tr));
					}
					foreach (var q in pts) BBox(q);
					p.Add("wipeout", "true");
					Finalize(p, ent, "Polygon;" + Ring(pts));
				}
				else
				{
					Skip(type); // SHAPE/代理实体/未支持类型
				}
			}
			catch (Exception)
			{
				Skip(type + "(err)");
			}
		}

		// 文字实体统一出口：anchor 模式 = 锚点 Point；outline 模式 = 包围盒 Polygon + 基线 LineString（组合）
		private void EmitText(PropsBuilder p, Entity src, XYZ anchor, string text,
			double height, double wf, double rot, TextStyle style, string halign, string valign, string tag, T2D? tr)
		{
			var a = P(anchor, tr);
			BBox(a);
			if (_po.TextContent)
			{
				p.Add("text", JStr(text));
				if (tag != null) p.Add("tag", JStr(tag));
			}
			if (_po.TextSize)
			{
				p.Add("height", C(height));
				if (wf > 0 && Math.Abs(wf - 1.0) > 1e-9) p.Add("width_factor", C(wf));
			}
			if (_po.TextStyle)
			{
				try
				{
					if (style != null)
					{
						p.Add("text_style", JStr(style.Name));
						if (!string.IsNullOrEmpty(style.Filename)) p.Add("text_font", JStr(style.Filename));
					}
				}
				catch { }
			}
			if (_po.TextAlignment)
			{
				if (halign != null) p.Add("halign", JStr(halign));
				if (valign != null) p.Add("valign", JStr(valign));
			}
			if (_po.Rotation && Math.Abs(rot) > 1e-9)
				p.Add("rotation_deg", C(rot * 180.0 / Math.PI));

			if (string.Equals(_opt.TextMode, "outline", StringComparison.OrdinalIgnoreCase))
			{
				// 近似包围盒：宽 = 字高×宽度因子×0.6×字符数（无字体轮廓引擎；按水平对齐方向回缩锚点）
				string plain = text ?? "";
				double w = height * Math.Max(0.01, wf) * 0.6 * Math.Max(1, plain.Length);
				double dx = Math.Cos(rot), dy = Math.Sin(rot);
				double nx = -dy, ny = dx;
				double off = 0;
				if (halign != null)
				{
					if (halign.Contains("Right")) off = -w;
					else if (halign.Contains("Center") || halign.Contains("Middle") || halign.Contains("Aligned")) off = -w / 2;
				}
				var b1 = (X: a.X + off * dx, Y: a.Y + off * dy);
				var b2 = (X: b1.X + w * dx, Y: b1.Y + w * dy);
				var b3 = (X: b2.X + height * nx, Y: b2.Y + height * ny);
				var b4 = (X: b1.X + height * nx, Y: b1.Y + height * ny);
				BBox(b1); BBox(b2); BBox(b3); BBox(b4);
				p.Add("text_outline", "\"approx\"");
				// 面
				Finalize(p, src, "Polygon;" + Pt(b1) + "|" + Pt(b2) + "|" + Pt(b3) + "|" + Pt(b4) + "|" + Pt(b1));
				// 线（基线）
				Finalize(p, src, "LineString;" + Pt(b1) + "|" + Pt(b2));
				return;
			}

			Finalize(p, src, "Point;" + Pt(a));
		}

		// POINT 符号炸开（$PDMODE/$PDSIZE → GeoJSON 点/线/面组合）
		private void EmitPointGlyph(Point pt, (double X, double Y) c, string layerName, int color, string colorBy, int? trueColor, ulong handle)
		{
			double ps = _psize > 1e-9 ? _psize : 0.1; // PDSIZE≤0：AutoCAD 取视口高 1/15，导出端按 0.1 单位近似
			int shape = _pdmode & 7;
			bool frame = (_pdmode & 32) != 0;
			bool arms = (_pdmode & 64) != 0;
			double hl = arms ? ps : ps / 2; // 线型臂半长
			double d = hl * 0.70710678118654752; // 对角臂

			// 每个子 feature 独立属性（不累积）
			void EmitShape(string geomSpec, int? glyphId, bool frameFlag)
			{
				var pp = NewProps(layerName, color, colorBy, trueColor, pt.GetType().Name, handle);
				pp.Add("point", "true");
				if (glyphId.HasValue) pp.Add("point_glyph", glyphId.Value.ToString(CultureInfo.InvariantCulture));
				if (frameFlag) pp.Add("point_frame", "true");
				Finalize(pp, pt, geomSpec);
			}

			switch (shape)
			{
				case 1: // 点
					EmitShape("Point;" + Pt(c), shape, false);
					break;
				case 2: // 圆
				{
					var pts = SampleArc(new XYZ(c.X, c.Y, 0), ps, 0, 2 * Math.PI, true, null, true);
					EmitShape("Polygon;" + Ring(pts), shape, false);
					break;
				}
				case 3: // 方框
					EmitShape("Polygon;" + Box(c, ps), shape, false);
					break;
				case 5: // 斜线 /
					EmitShape("LineString;" + Pt((X: c.X - d, Y: c.Y - d)) + "|" + Pt((X: c.X + d, Y: c.Y + d)), shape, false);
					break;
				case 6: // X
					EmitShape("LineString;" + Pt((X: c.X - d, Y: c.Y - d)) + "|" + Pt((X: c.X + d, Y: c.Y + d)), shape, false);
					EmitShape("LineString;" + Pt((X: c.X + d, Y: c.Y - d)) + "|" + Pt((X: c.X - d, Y: c.Y + d)), shape, false);
					break;
				case 7: // 框 + X
					EmitShape("Polygon;" + Box(c, ps), shape, false);
					EmitShape("LineString;" + Pt((X: c.X - d, Y: c.Y - d)) + "|" + Pt((X: c.X + d, Y: c.Y + d)), shape, false);
					EmitShape("LineString;" + Pt((X: c.X + d, Y: c.Y - d)) + "|" + Pt((X: c.X - d, Y: c.Y + d)), shape, false);
					break;
				default: // 0/4 十字 +
					EmitShape("LineString;" + Pt((X: c.X - hl, Y: c.Y)) + "|" + Pt((X: c.X + hl, Y: c.Y)), shape, false);
					EmitShape("LineString;" + Pt((X: c.X, Y: c.Y - hl)) + "|" + Pt((X: c.X, Y: c.Y + hl)), shape, false);
					break;
			}
			if (frame) // 外框（PDMODE bit 32）
				EmitShape("Polygon;" + Box(c, ps), null, true);
		}

		private string Box((double X, double Y) c, double side)
		{
			double h = side / 2;
			return Pt((X: c.X - h, Y: c.Y - h)) + "|" + Pt((X: c.X + h, Y: c.Y - h)) + "|" + Pt((X: c.X + h, Y: c.Y + h)) + "|" + Pt((X: c.X - h, Y: c.Y + h)) + "|" + Pt((X: c.X - h, Y: c.Y - h));
		}

		private void EmitInsert(Insert ins, T2D? tr, short byBlockAci)
		{
			if (ins?.Block == null) { Skip("Insert(noblock)"); return; }
			if (!_ancestors.Add(ins.Block)) { Skip("Insert(cycle)"); return; }
			try
			{
				var tr2 = T2D.Compose(ins, tr);
				short insAci;
				try
				{
					var ic = ins.Color;
					insAci = (short)(ic.IsTrueColor ? ic.GetApproxIndex() : (ic.IsByLayer ? 7 : ic.Index));
					if (insAci == 256) insAci = 7;
					if (insAci == 0) insAci = byBlockAci;
				}
				catch { insAci = byBlockAci; }

				bool hasAtt = ins.Attributes != null && ins.Attributes.Count > 0;
				foreach (var e in ins.Block.Entities)
				{
					if (hasAtt && e is AttributeDefinition) continue; // 模板默认值不输出（真实值在 Attributes）
					Emit(e, tr2, insAci);
				}
				if (hasAtt)
					// ATTRIB 位置在父坐标系（AutoCAD 存档时已把 ATDEF 位置/字高预乘插入比例），用父变换
					foreach (var at in ins.Attributes)
						Emit(at, tr, byBlockAci);
			}
			finally
			{
				_ancestors.Remove(ins.Block);
			}
		}

		private void EmitDimension(Dimension dim, short byBlockAci)
		{
			// 与渲染引擎一致：DIMENSION 的 Block 内实体为世界坐标，直接展开；Defpoints 图层点不输出
			if (dim.Block == null || dim.Block.Entities.Count == 0) { Skip("Dimension"); return; }
			foreach (var be in dim.Block.Entities)
			{
				if (be is Point && be.Layer != null &&
					string.Equals(be.Layer.Name, "Defpoints", StringComparison.OrdinalIgnoreCase)) continue;
				Emit(be, null, byBlockAci);
			}
		}

		private bool EmitHatch(Hatch ht, T2D? tr, PropsBuilder p, Entity src)
		{
			var loops = BuildHatchLoops(ht, tr);
			if (loops.Count == 0) return false;
			foreach (var lp in loops) foreach (var q in lp) BBox(q);

			// 外环/孔判定：环 i 的首点落在环 j 内 → i 是 j 的孔
			int n = loops.Count;
			var isHole = new bool[n];
			for (int i = 0; i < n; i++)
				for (int j = 0; j < n; j++)
				{
					if (i == j) continue;
					if (ContainsPoint(loops[j], loops[i][0])) { isHole[i] = true; break; }
				}
			p.Add("hatch", "true");
			var polys = new List<List<List<(double X, double Y)>>>();
			for (int i = 0; i < n; i++)
			{
				if (isHole[i]) continue;
				var poly = new List<List<(double X, double Y)>> { loops[i] };
				for (int j = 0; j < n; j++)
					if (!isHole[j] && j != i && ContainsPoint(loops[i], loops[j][0]))
						poly.Add(loops[j]);
				polys.Add(poly);
			}
			if (polys.Count == 0) return false;
			// 分隔约定：',' 坐标分量；'|' 点；';' 环（孔）；':' 多边形（MultiPolygon 顶层）
			string geom = polys.Count == 1
				? "Polygon;" + string.Join(";", polys[0].Select(ring => Ring(ring)))
				: "MultiPolygon;" + string.Join(":", polys.Select(poly => string.Join(";", poly.Select(ring => Ring(ring)))));
			Finalize(p, src, geom);
			return true;
		}

		private void Skip(string type)
		{
			_skipped[type] = _skipped.TryGetValue(type, out var c) ? c + 1 : 1;
			_skippedTotal++;
		}

		private void BBox((double X, double Y) q)
		{
			if (!double.IsFinite(q.X) || !double.IsFinite(q.Y)) return;
			if (q.X < _minX) _minX = q.X; if (q.X > _maxX) _maxX = q.X;
			if (q.Y < _minY) _minY = q.Y; if (q.Y > _maxY) _maxY = q.Y;
		}

		private (double X, double Y) P(XYZ v, T2D? tr)
		{
			double x = v.X, y = v.Y;
			if (tr != null)
			{
				var a = tr.Value.Apply(x, y);
				x = a.X; y = a.Y;
			}
			return (x, y);
		}

		// ==================== 属性组装（逐项开关） ====================

		private PropsBuilder NewProps(string layerName, int color, string colorBy, int? trueColor, string type, ulong handle)
		{
			var p = new PropsBuilder();
			if (_po.Layer && layerName != null) p.Add("layer", JStr(layerName));
			if (_po.Color)
			{
				p.Add("color", color.ToString(CultureInfo.InvariantCulture));
				p.Add("color_by", "\"" + colorBy + "\"");
				if (trueColor.HasValue)
					p.Add("true_color", "\"" + trueColor.Value.ToString("X6", CultureInfo.InvariantCulture) + "\"");
			}
			if (_po.EntityType) p.Add("entity", "\"" + type + "\"");
			if (_po.Handle && handle != 0) p.Add("handle", "\"" + handle.ToString("X8", CultureInfo.InvariantCulture) + "\"");
			return p;
		}

		// 收尾：通用属性（线型/线宽）+ XData 扩展属性 + 流式写盘
		private void Finalize(PropsBuilder p, Entity ent, string geomSpec)
		{
			try
			{
				if (_po.Linetype)
				{
					string ltName = "ByLayer";
					try { ltName = ent.LineType?.Name ?? "ByLayer"; } catch { }
					p.Add("linetype", JStr(ltName));
				}
				if (_po.LinetypeScale && Math.Abs(ent.LineTypeScale - 1.0) > 1e-9)
					p.Add("linetype_scale", C(ent.LineTypeScale));
				if (_po.LineWeight)
					p.Add("line_weight", "\"" + ent.LineWeight.ToString() + "\"");
			}
			catch { }

			if (_po.XData)
			{
				string xd = SerializeXData(ent);
				if (xd != null) { p.Add("xdata", xd); _xdataCount++; }
			}

			_featureCount++;
			if (_sw == null) return; // 统计模式：只计数
			if (_featureCount > 1) _sw.Write(',');
			_sw.Write("{\"type\":\"Feature\",\"properties\":");
			_sw.Write(p.Build());
			_sw.Write(",\"geometry\":");
			_sw.Write(Geo(geomSpec));
			_sw.Write('}');
			if (_featureCount % 500 == 0) _sw.Flush(); // 定期落盘，内存 O(1)
		}

		// ==================== 几何采样（与渲染引擎同源算法） ====================

		private List<(double X, double Y)> SampleLwPolyline(LwPolyline pl, T2D? tr)
		{
			var res = new List<(double X, double Y)>();
			var verts = pl?.Vertices;
			if (verts == null) return res;
			int n = verts.Count;
			if (n < 2) return res;

			void Push(double x, double y)
			{
				if (!double.IsFinite(x) || !double.IsFinite(y)) return;
				if (tr == null) res.Add((x, y));
				else { var a = tr.Value.Apply(x, y); res.Add((a.X, a.Y)); }
			}

			Push(verts[0].Location.X, verts[0].Location.Y);
			int last = pl.IsClosed ? n : n - 1;
			for (int i = 0; i < last; i++)
			{
				int ni = (i + 1) % n;
				var v1 = verts[i].Location;
				var v2 = verts[ni].Location;
				double bulge = verts[i].Bulge;
				if (Math.Abs(bulge) > 1e-9)
				{
					var arc = ArcFromBulge(v1.X, v1.Y, v2.X, v2.Y, bulge);
					if (arc.R > 0 && res.Count > 0)
					{
						double theta = 4.0 * Math.Atan(bulge); // 带符号 sweep（弧度）
						int seg = Math.Max(8, (int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 180.0 * Math.Max(1.0, _opt.AngleSampleStepDeg))));
						res.RemoveAt(res.Count - 1); // 弧从 v1 重新采样
						for (int k = 0; k <= seg; k++)
						{
							double ang = arc.A1 + theta * k / seg;
							Push(arc.C.X + arc.R * Math.Cos(ang), arc.C.Y + arc.R * Math.Sin(ang));
						}
						continue;
					}
				}
				Push(v2.X, v2.Y);
			}
			return res;
		}

		private static (XYZ C, double R, double A1) ArcFromBulge(double x1, double y1, double x2, double y2, double bulge)
		{
			double dx = x2 - x1, dy = y2 - y1;
			double chord = Math.Sqrt(dx * dx + dy * dy);
			if (chord < 1e-12) return (new XYZ(x1, y1, 0), 0, 0);
			double theta = 4.0 * Math.Atan(Math.Abs(bulge));
			double r = chord / (2.0 * Math.Sin(theta / 2.0));
			if (!double.IsFinite(r) || r <= 0) return (new XYZ(x1, y1, 0), 0, 0);
			double gamma = (Math.PI - theta) / 2.0;
			double phi = Math.Atan2(dy, dx) + Math.Sign(bulge) * gamma;
			double cx = x1 + r * Math.Cos(phi);
			double cy = y1 + r * Math.Sin(phi);
			double a1 = Math.Atan2(y1 - cy, x1 - cx); // 弧度
			return (new XYZ(cx, cy, 0), r, a1);
		}

		private List<(double X, double Y)> SampleArc(XYZ center, double radius, double a1, double a2, bool ccw, T2D? tr, bool full)
		{
			var pts = new List<(double X, double Y)>();
			double r = Math.Abs(radius);
			if (r <= 0 || !double.IsFinite(r)) return pts;
			double span;
			if (full) span = 2 * Math.PI;
			else
			{
				span = SpanNorm(a2 - a1);
				if (span < 1e-6) span = 2 * Math.PI;
			}
			double stepLen = Math.Max(1e-6, _opt.ArcSampleStep);
			int m = (int)Math.Clamp(Math.Ceiling(r * span / stepLen), 32, 100000);
			for (int i = 0; i <= m; i++)
			{
				double ang = a1 + (ccw ? 1 : -1) * span * i / m;
				double x = center.X + r * Math.Cos(ang), y = center.Y + r * Math.Sin(ang);
				if (tr == null) pts.Add((x, y));
				else { var a = tr.Value.Apply(x, y); pts.Add((a.X, a.Y)); }
			}
			return pts;
		}

		private List<(double X, double Y)> SampleEllipseEntity(Ellipse el, T2D? tr)
		{
			var pts = new List<(double X, double Y)>();
			// fork MajorAxis/MinorAxis 为主/短轴全长(2a/2b)
			double rx = Math.Abs(el.MajorAxis) / 2.0, ry = Math.Abs(el.MinorAxis) / 2.0;
			if (rx <= 0 || ry <= 0 || !double.IsFinite(rx) || !double.IsFinite(ry)) return pts;
			double rot = el.Rotation + (tr != null ? Math.Atan2(tr.Value.Sa, tr.Value.Ca) : 0.0);
			double ca = Math.Cos(rot), sa = Math.Sin(rot);
			double stepLen = Math.Max(1e-6, _opt.ArcSampleStep);
			int m = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * Math.Max(rx, ry) / stepLen), 64, 100000);
			for (int i = 0; i <= m; i++)
			{
				double th = 2 * Math.PI * i / m;
				double ex = rx * Math.Cos(th), ey = ry * Math.Sin(th);
				double x = el.Center.X + ex * ca - ey * sa, y = el.Center.Y + ex * sa + ey * ca;
				if (tr == null) pts.Add((x, y));
				else { var a = tr.Value.Apply(x, y); pts.Add((a.X, a.Y)); }
			}
			return pts;
		}

		private List<(double X, double Y)> SampleSpline(Spline sp, T2D? tr, int samplesPerSpan)
		{
			var pts = new List<(double X, double Y)>();
			if (sp == null) return pts;
			var cp = sp.ControlPoints;
			if (cp != null && cp.Count >= 2)
			{
				int p = Math.Max(1, sp.Degree);
				int n = cp.Count;
				if (sp.Knots != null && sp.Knots.Count == n + p + 1)
				{
					var hx = new double[n]; var hy = new double[n]; var hw = new double[n];
					var wts = sp.Weights;
					for (int i = 0; i < n; i++)
					{
						double wi = (wts != null && i < wts.Count && Math.Abs(wts[i]) >= 1e-9) ? wts[i] : 1.0;
						hx[i] = cp[i].X * wi; hy[i] = cp[i].Y * wi; hw[i] = wi;
					}
					SampleNurbs(sp.Knots.ToArray(), hx, hy, hw, p, samplesPerSpan, tr, pts);
				}
				else
					foreach (var c in cp) AddTr(pts, c.X, c.Y, tr);
			}
			else if (sp.FitPoints != null)
				foreach (var fp in sp.FitPoints) AddTr(pts, fp.X, fp.Y, tr);
			if (sp.IsClosed && pts.Count >= 2) pts.Add(pts[0]);
			return pts;
		}

		private static void AddTr(List<(double X, double Y)> pts, double x, double y, T2D? tr)
		{
			if (!double.IsFinite(x) || !double.IsFinite(y)) return;
			if (tr == null) pts.Add((x, y));
			else { var a = tr.Value.Apply(x, y); pts.Add((a.X, a.Y)); }
		}

		private static void SampleNurbs(double[] knots, double[] hx, double[] hy, double[] hw, int p, int samplesPerSpan, T2D? tr, List<(double X, double Y)> pts)
		{
			int n = hx.Length;
			if (p < 1 || p >= n) return;
			for (int s = p; s <= n - 1; s++)
			{
				double k0 = knots[s], k1 = knots[s + 1];
				if (!(k1 > k0)) continue;
				int ns = Math.Max(2, samplesPerSpan);
				for (int j = 0; j < ns; j++)
				{
					double t = k0 + (k1 - k0) * (j + 0.5) / ns;
					DeBoor(hx, hy, hw, knots, p, s, t, out double x, out double y);
					AddTr(pts, x, y, tr);
				}
			}
		}

		private static void DeBoor(double[] hx, double[] hy, double[] hw, double[] knots, int p, int s, double t, out double x, out double y)
		{
			int n = hx.Length;
			var dx = new double[p + 1]; var dy = new double[p + 1]; var dw = new double[p + 1];
			for (int j = 0; j <= p; j++)
			{
				int idx = s - p + j;
				if (idx < 0) idx = 0; if (idx >= n) idx = n - 1;
				dx[j] = hx[idx]; dy[j] = hy[idx]; dw[j] = hw[idx];
			}
			for (int k = 1; k <= p; k++)
			{
				for (int j = s - p + k; j <= s; j++)
				{
					double n1 = knots[j], n2 = knots[j + p - k + 1];
					double d = n2 - n1;
					double b = d < 1e-12 ? 0 : (t - n1) / d;
					dx[j] = (1 - b) * dx[j - 1] + b * dx[j];
					dy[j] = (1 - b) * dy[j - 1] + b * dy[j];
					dw[j] = (1 - b) * dw[j - 1] + b * dw[j];
				}
			}
			int last = s;
			double w = dw[last];
			x = Math.Abs(w) < 1e-12 ? 0 : dx[last] / w;
			y = Math.Abs(w) < 1e-12 ? 0 : dy[last] / w;
		}

		private List<List<(double X, double Y)>> BuildHatchLoops(Hatch hatch, T2D? tr)
		{
			var result = new List<List<(double X, double Y)>>();
			if (hatch?.Paths == null) return result;
			double angStep = Math.PI / 180.0 * Math.Max(1.0, _opt.AngleSampleStepDeg);
			foreach (var path in hatch.Paths)
			{
				var pts = new List<(double X, double Y)>();
				if (path.Edges == null) continue;
				foreach (var edge in path.Edges)
				{
					switch (edge)
					{
						case Hatch.BoundaryPath.Line ln:
							AddTr(pts, ln.Start.X, ln.Start.Y, tr);
							AddTr(pts, ln.End.X, ln.End.Y, tr);
							break;
						case Hatch.BoundaryPath.Arc arc:
						{
							double span = SpanNorm(arc.EndAngle - arc.StartAngle);
							int seg = Math.Max(12, (int)Math.Ceiling(span / angStep));
							for (int i = 0; i <= seg; i++)
							{
								double ang = arc.StartAngle + (arc.CounterClockWise ? 1 : -1) * span * i / seg;
								AddTr(pts, arc.Center.X + arc.Radius * Math.Cos(ang), arc.Center.Y + arc.Radius * Math.Sin(ang), tr);
							}
							break;
						}
						case Hatch.BoundaryPath.Ellipse el:
						{
							double ax = el.MajorAxisEndPoint.X, ay = el.MajorAxisEndPoint.Y;
							double A = Math.Sqrt(ax * ax + ay * ay);
							if (A <= 0 || !double.IsFinite(A)) break;
							double ratio = el.RadiusRatio;
							if (!double.IsFinite(ratio) || ratio <= 0) ratio = 1.0;
							if (ratio > 1.0) ratio = 1.0 / ratio;
							double B = A * ratio;
							double ux = ax / A, uy = ay / A;
							double vx = -uy, vy = ux;
							double s = el.StartAngle, span = SpanNorm(el.EndAngle - s);
							double dir = el.CounterClockWise ? 1.0 : -1.0;
							int seg = Math.Max(12, (int)Math.Ceiling(span / angStep));
							for (int i = 0; i <= seg; i++)
							{
								double phi = s + dir * span * i / seg;
								AddTr(pts, el.Center.X + ux * A * Math.Cos(phi) + vx * B * Math.Sin(phi),
											el.Center.Y + uy * A * Math.Cos(phi) + vy * B * Math.Sin(phi), tr);
							}
							break;
						}
						case Hatch.BoundaryPath.Spline sp:
						{
							if (sp.ControlPoints != null && sp.ControlPoints.Count >= 2 && sp.Knots != null)
							{
								int np = sp.ControlPoints.Count;
								int pdeg = Math.Max(1, sp.Degree);
								if (sp.Knots.Count == np + pdeg + 1)
								{
									var hx = new double[np]; var hy = new double[np]; var hw = new double[np];
									for (int i = 0; i < np; i++)
									{
										double w = sp.ControlPoints[i].Z;
										if (!double.IsFinite(w) || Math.Abs(w) < 1e-9) w = 1.0;
										hx[i] = sp.ControlPoints[i].X * w; hy[i] = sp.ControlPoints[i].Y * w; hw[i] = w;
									}
									SampleNurbs(sp.Knots.ToArray(), hx, hy, hw, pdeg, 8, tr, pts);
									break;
								}
							}
							if (sp.ControlPoints != null)
								foreach (var c in sp.ControlPoints) AddTr(pts, c.X, c.Y, tr);
							break;
						}
						case Hatch.BoundaryPath.Polyline poly:
						{
							if (poly.Vertices == null || poly.Vertices.Count == 0) break;
							int nv = poly.Vertices.Count;
							for (int i = 0; i < nv; i++)
							{
								var v = poly.Vertices[i];
								AddTr(pts, v.X, v.Y, tr);
								var v2 = poly.Vertices[i == nv - 1 && poly.IsClosed ? 0 : i + 1];
								double dx = v2.X - v.X, dy = v2.Y - v.Y;
								if (dx * dx + dy * dy <= 1e-18) continue;
								if (Math.Abs(v.Z) < 1e-12) continue; // 直线段
								var arc = ArcFromBulge(v.X, v.Y, v2.X, v2.Y, v.Z);
								if (arc.R < 1e-12) continue;
								double theta = 4.0 * Math.Atan(v.Z);
								int seg = Math.Max(8, (int)Math.Ceiling(Math.Abs(theta) / angStep));
								for (int k = 1; k <= seg; k++)
								{
									double ang = arc.A1 + theta * k / seg;
									AddTr(pts, arc.C.X + arc.R * Math.Cos(ang), arc.C.Y + arc.R * Math.Sin(ang), tr);
								}
							}
							break;
						}
					}
				}
				if (pts.Count >= 2)
				{
					double dx = pts[^1].X - pts[0].X, dy = pts[^1].Y - pts[0].Y;
					if (dx * dx + dy * dy > 1e-12) pts.Add(pts[0]); // 显式闭合
				}
				if (pts.Count >= 3) result.Add(pts);
			}
			return result;
		}

		private static double SpanNorm(double a)
		{
			while (a < 0) a += 2 * Math.PI;
			while (a >= 2 * Math.PI) a -= 2 * Math.PI;
			return a;
		}

		private static bool ContainsPoint(List<(double X, double Y)> ring, (double X, double Y) p)
		{
			bool inP = false;
			int n = ring.Count;
			for (int i = 0, j = n - 1; i < n; j = i++)
			{
				double xi = ring[i].X, yi = ring[i].Y;
				double xj = ring[j].X, yj = ring[j].Y;
				if ((yi > p.Y) != (yj > p.Y) &&
					p.X < (xj - xi) * (p.Y - yi) / (yj - yi) + xi)
					inP = !inP;
			}
			return inP;
		}

		// ==================== XData（扩展属性）序列化 ====================

		private string SerializeXData(Entity ent)
		{
			try
			{
				var dict = ent.ExtendedData;
				if (dict == null) return null;
				var apps = new List<string>();
				foreach (var kv in dict)
				{
					string appName = null;
					try { appName = kv.Key?.Name; } catch { }
					if (string.IsNullOrEmpty(appName)) continue;
					var recs = kv.Value?.Records;
					if (recs == null || recs.Count == 0) continue;
					var items = new List<string>();
					foreach (var rec in recs)
					{
						if (rec == null) continue;
						int code = (int)rec.Code;
						// 组码 1001（应用名，与 app 字段重复）/ 1002（控制串 { }）无信息量，省略
						if (code == (int)DxfCode.ExtendedDataRegAppName || code == (int)DxfCode.ExtendedDataControlString) continue;
						items.Add("[" + code + "," + FormatXDataValue(rec.RawValue) + "]");
					}
					if (items.Count == 0) continue;
					apps.Add("{\"app\":" + JStr(appName) + ",\"items\":[" + string.Join(",", items) + "]}");
				}
				return apps.Count == 0 ? null : "[" + string.Join(",", apps) + "]";
			}
			catch
			{
				return null; // XData 异常不影响几何导出
			}
		}

		private string FormatXDataValue(object v)
		{
			switch (v)
			{
				case null: return "null";
				case string s: return JStr(s);
				case double d: return C(d);
				case float f: return C(f);
				case decimal m: return C((double)m);
				case bool b: return b ? "true" : "false";
				case byte[] bytes: return JStr(Convert.ToBase64String(bytes));
				case XYZ pt: return "[" + C(pt.X) + "," + C(pt.Y) + "," + C(pt.Z) + "]";
				case ulong ul: return JStr(ul.ToString("X8", CultureInfo.InvariantCulture)); // 句柄
				case int i: return i.ToString(CultureInfo.InvariantCulture);
				case short sh: return sh.ToString(CultureInfo.InvariantCulture);
				case long l: return l.ToString(CultureInfo.InvariantCulture);
				case uint ui: return ui.ToString(CultureInfo.InvariantCulture);
				case ushort ush: return ush.ToString(CultureInfo.InvariantCulture);
				case byte by: return by.ToString(CultureInfo.InvariantCulture);
				default:
					try { return JStr(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""); }
					catch { return "null"; }
			}
		}

		// ==================== JSON 组装（流式） ====================

		// spec 分隔约定：',' 坐标分量；'|' 点；';' 环（孔）；':' 多边形（MultiPolygon 顶层）
		//   "Point;x,y" | "LineString;pt|pt|..." | "Polygon;ring[;hole]" | "MultiPolygon;poly[:poly]"，ring = pt|pt
		private string Geo(string spec)
		{
			int sem = spec.IndexOf(';');
			string kind = sem >= 0 ? spec.Substring(0, sem) : spec;
			string body = sem >= 0 ? spec.Substring(sem + 1) : "";
			string coords;
			if (kind == "Point")
				coords = "[" + body + "]";
			else if (kind == "LineString")
				coords = "[" + WrapPoints(body) + "]";
			else if (kind == "Polygon")
				coords = "[" + WrapRings(body) + "]";
			else // MultiPolygon：body = poly:poly，poly = ring;ring，ring = pt|pt
				coords = "[" + string.Join(",", body.Split(':').Select(poly =>
					"[" + WrapRings(poly) + "]")) + "]";
			return "{\"type\":\"" + kind + "\",\"coordinates\":" + coords + "}";
		}

		private static string WrapPoint(string pt) => "[" + pt + "]";

		// ring = "x,y|x,y|..." → "[[x,y],[x,y],...]"
		private static string WrapRing(string ring)
			=> "[" + string.Join(",", ring.Split('|').Select(WrapPoint)) + "]";

		// rings（';' 分隔，孔） → "[ring,ring,...]"
		private static string WrapRings(string body)
			=> string.Join(",", body.Split(';').Select(WrapRing));

		// points（'|' 分隔） → "[pt,pt,...]"
		private static string WrapPoints(string body)
			=> string.Join(",", body.Split('|').Select(WrapPoint));

		private string Pt((double X, double Y) p) => C(p.X) + "," + C(p.Y);

		private string Line(List<(double X, double Y)> pts) => string.Join("|", pts.Select(Pt));

		private string Ring(List<(double X, double Y)> pts)
		{
			var sb = new StringBuilder();
			for (int i = 0; i < pts.Count; i++)
			{
				if (i > 0) sb.Append('|');
				sb.Append(Pt(pts[i]));
			}
			return sb.ToString();
		}

		private string C(double v)
		{
			if (!double.IsFinite(v)) return "0";
			v = Math.Round(v, _rounding);
			if (v == 0) v = 0; // 归一化 -0
			return v.ToString("R", CultureInfo.InvariantCulture);
		}

		private static string JStr(string s)
		{
			if (s == null) return "\"\"";
			var sb = new StringBuilder(s.Length + 8);
			sb.Append('"');
			foreach (char ch in s)
			{
				switch (ch)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
						else sb.Append(ch);
						break;
				}
			}
			sb.Append('"');
			return sb.ToString();
		}
	}

	/// <summary>feature 属性 JSON（保持插入顺序）。</summary>
	private sealed class PropsBuilder
	{
		private readonly List<string> _items = new(8);
		public void Add(string key, string jsonValue) => _items.Add("\"" + key + "\":" + jsonValue);
		public string Build() => "{" + string.Join(",", _items) + "}";
	}
}

/// <summary>2D 仿射变换（旋转+缩放+平移），与 ACadSharp.Formats 渲染引擎 Transform2D 同式。</summary>
public readonly struct T2D
{
	public readonly double Ca, Sa; // 旋转
	public readonly double Sx, Sy; // 缩放
	public readonly double Tx, Ty; // 平移

	public T2D(double ca, double sa, double sx, double sy, double tx, double ty)
	{ (Ca, Sa, Sx, Sy, Tx, Ty) = (ca, sa, sx, sy, tx, ty); }

	public (double X, double Y) Apply(double x, double y)
	{
		double rx = x * Sx, ry = y * Sy;
		return (Tx + rx * Ca - ry * Sa, Ty + rx * Sa + ry * Ca);
	}

	public static T2D? Compose(Insert ins, T2D? parent)
	{
		double rot = ins.Rotation; // fork 弧度
		double ca = Math.Cos(rot), sa = Math.Sin(rot);
		double sx = ins.XScale, sy = ins.YScale;
		double px = ins.InsertPoint.X, py = ins.InsertPoint.Y;
		if (parent == null) return new T2D(ca, sa, sx, sy, px, py);
		var p = parent.Value;
		return new T2D(
			p.Ca * ca - p.Sa * sa,
			p.Sa * ca + p.Ca * sa,
			p.Sx * sx,
			p.Sy * sy,
			p.Tx + (px * p.Sx) * p.Ca - (py * p.Sy) * p.Sa,
			p.Ty + (px * p.Sx) * p.Sa + (py * p.Sy) * p.Ca);
	}
}
