using System;

namespace ACadSharp.Formats.GeoJson;

/// <summary>
/// GeoJSON 导出配置（2026-11-02 cad2geojson 需求）。
/// 与 <see cref="Image.ImageConfiguration"/> 平级：同一套进度/日志回调类型
/// （<see cref="Image.ImageProgress"/>），PNG/SVG/PDF/GeoJSON 四种导出统一可监控。
/// </summary>
public class GeoJsonConfiguration
{
	/// <summary>日志回调（告警/说明）。</summary>
	public Action<string> Log { get; set; }

	/// <summary>
	/// 转换进度回调（null = 禁用，零开销）。
	/// phase 取值：walk（实体遍历）/ write（写盘）/ done（完成）；
	/// Current/Total = 模型空间顶层实体序号；Percent 0.0~1.0 总体进度。
	/// ⚠️ 回调在导出线程内联触发，宿主应保持轻量（UI 请自行切主线程）。
	/// </summary>
	public Action<Image.ImageProgress> Progress { get; set; }

	/// <summary>feature 属性逐项开关（按需导出，2026-11-02 用户要求）。</summary>
	public GeoJsonPropertyOptions Properties { get; set; } = new();

	/// <summary>true（默认）= 处理 DIMENSION 的块内容（Defpoints 图层点跳过）。</summary>
	public bool IncludeDimensions { get; set; } = true;

	/// <summary>
	/// INSERT（含「点块」）是否炸开（2026-11-02 用户要求「点块可支持是否炸开」）。
	/// true（默认）= 展开块内容为独立 feature（线/面/点组合，嵌套块循环引用自动截断）；
	/// false = 整个 INSERT 表示为插入点处的单个 Point feature（properties.block = 块名）。
	/// </summary>
	public bool ExplodeInserts { get; set; } = true;

	/// <summary>
	/// POINT 实体的表示模式（2026-11-02 用户要求「点…可炸开成 GeoJSON 点线面组合」）。
	/// "point"（默认）= 位置处单个 Point feature；
	/// "glyph" = 按 $PDMODE/$PDSIZE 把点符号炸开成 GeoJSON 点/线/面组合：
	///   shape = PDMODE &amp; 7：0/4 十字(+)=2 条 LineString，1 点=Point，2 圆=采样 Polygon，
	///   3 方框=Polygon，5 斜线=LineString，6 X=2 条 LineString，7 框X=Polygon+2 条 LineString（多 feature）；
	///   PDMODE bit 32（外框）= 额外输出 2×PDSIZE 外框 Polygon；bit 64（加长臂）= 线型臂长 ×2。
	///   PDSIZE ≤ 0 时按 0.1 绘图单位近似（AutoCAD 此时取视口高 1/15，导出端无视口概念）。
	/// </summary>
	public string PointMode { get; set; } = "point";

	/// <summary>
	/// 文字实体（TEXT/MTEXT/ATTRIB）的表示模式（2026-11-02 用户要求「文字…可炸开成点线面组合」）。
	/// "anchor"（默认）= 锚点处单个 Point feature（properties 携带全部文字属性）；
	/// "outline" = 炸开为近似几何组合：文字包围盒 Polygon（面）+ 基线 LineString（线），
	///   宽度按 字高×宽度因子×0.6×字符数 近似（无字体轮廓引擎，非逐字形精确，
	///   properties.text_outline = "approx" 标记）。锚点取 AlignmentPoint（非零时）否则 InsertPoint。
	/// </summary>
	public string TextMode { get; set; } = "anchor";

	/// <summary>坐标小数位（默认 6，模型单位下约亚毫米精度）。</summary>
	public int CoordinateRounding { get; set; } = 6;

	/// <summary>弧/圆/椭圆采样弧长步长（绘图单位，默认 0.5，与渲染引擎同口径）。</summary>
	public double ArcSampleStep { get; set; } = 0.5;

	/// <summary>bulge/弧角度采样步长（度，默认 2°，与渲染引擎同口径）。</summary>
	public double AngleSampleStepDeg { get; set; } = 2.0;
}

/// <summary>
/// feature 属性逐项开关（2026-11-02 用户要求：图层名/颜色/类型/线型/旋转/文字样式大小对齐方式等按需导出）。
/// 全部默认 true = 完整导出；宿主按需关闭以减小文件体积。
/// </summary>
public class GeoJsonPropertyOptions
{
	/// <summary>layer：图层名。</summary>
	public bool Layer { get; set; } = true;

	/// <summary>color / color_by / true_color：ACI 颜色（含 ByLayer/ByBlock 解析与真彩）。</summary>
	public bool Color { get; set; } = true;

	/// <summary>entity：实体类型名（LINE/LWPOLYLINE/INSERT...）。</summary>
	public bool EntityType { get; set; } = true;

	/// <summary>handle：实体句柄（十六进制）。</summary>
	public bool Handle { get; set; } = true;

	/// <summary>linetype：线型名（ByLayer 时为字符串 "ByLayer"）。</summary>
	public bool Linetype { get; set; } = true;

	/// <summary>linetype_scale：实体线型比例。</summary>
	public bool LinetypeScale { get; set; } = true;

	/// <summary>line_weight：线宽（枚举名，如 W050/ByLayer）。</summary>
	public bool LineWeight { get; set; } = true;

	/// <summary>text / tag：文字内容与属性标签。</summary>
	public bool TextContent { get; set; } = true;

	/// <summary>height / width_factor：文字高度与宽度因子。</summary>
	public bool TextSize { get; set; } = true;

	/// <summary>text_style / text_font：文字样式名与字体文件名。</summary>
	public bool TextStyle { get; set; } = true;

	/// <summary>halign / valign：文字水平/垂直对齐方式。</summary>
	public bool TextAlignment { get; set; } = true;

	/// <summary>rotation_deg：旋转角（文字等）。</summary>
	public bool Rotation { get; set; } = true;

	/// <summary>
	/// xdata：实体扩展数据（XData，DXF 组码 1000-1071）。
	/// 序列化结构：<code>"xdata": [ { "app": "ACAD", "items": [ [1000, "文字"], [1040, 1.5], [1070, 3], [1010, [x,y,z]], [1005, "1F3A2"] ] } ]</code>
	/// items = [组码, 值]；组码 1001（应用名，与 app 重复）/1002（控制串）省略；
	/// 1004 二进制块 = Base64 字符串；1005 句柄 = 十六进制字符串。
	/// </summary>
	public bool XData { get; set; } = true;
}
