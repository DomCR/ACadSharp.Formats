using System;

namespace ACadSharp.Formats.Image;

/// <summary>
/// 图像导出配置（SVG/PDF 共用，T4）。
/// 画布/颜色/线型语义与 GDB2CAD.Core.DxfImageExporter（netDxf 输入版）严格一致，
/// 本导出器把输入换成 ACadSharp 文档（见 执行状态_全量ACadSharp.md T4）。
/// </summary>
public class ImageConfiguration
{
	/// <summary>
	/// 白色背景（默认 false = 黑底）。白底时 ACI 7（白/黑）显示为黑色（与 AutoCAD 一致），点标记反色。
	/// </summary>
	public bool WhiteBackground { get; set; }

	/// <summary>
	/// 透明背景（仅 PNG 光栅输出端生效；默认 false）。
	/// true = 画布不绘制背景色（保留位图 alpha 通道为全透明），适合把位图叠加到其他底图/网页；
	/// ACI 7（白/黑）仍按黑底语义渲染为白色（透明背景下白色最可读），点标记不再绘制底色光晕环。
	/// 与 <see cref="WhiteBackground"/> 互斥：同时为 true 时透明优先（透明背景没有"底色"可言）。
	/// SVG/PDF 输出端忽略此项（矢量格式背景由 <see cref="WhiteBackground"/> 控制，保持原有行为不变）。
	/// </summary>
	public bool TransparentBackground { get; set; }

	/// <summary>
	/// 输出放大倍数：内部画板 1:1（1 绘图单位 = 1 像素），最终画布整体放大该倍数
	/// （SVG width/height、PDF CTM+MediaBox、PNG 每数据单位像素数）。默认 10（2026-09-23 用户要求）。
	/// 可经 GDB2CAD 的 appsettings.json（ImageZoom）或 CLI（--zoom）覆盖，优先级：CLI &gt; 配置 &gt; 默认 10。
	/// </summary>
	public double OutputZoom { get; set; } = 10.0d;

	/// <summary>
	/// WMTS 缩放级别 z（仅 PNG 分块输出端生效；SVG/PDF 忽略）。
	/// 多级缩放导出时由宿主程序逐级设置为【0-based 级别索引】（第 1 级→0、第 2 级→1…），
	/// 使各级别瓦片落入统一 tiles/{z}/{row}/{col}.png 树，而非按 OutputZoom 四舍五入的缩放倍数。
	/// null（默认）= 单级导出，z 回退为 OutputZoom 四舍五入（与旧行为一致）。
	/// </summary>
	public int? WmtsZ { get; set; }

	/// <summary>
	/// 强制单图也按 WMTS 网格输出（仅 PNG 端、且 WmtsZ 非 null 时生效）。
	/// true = 整幅画布未超像素上限时，仍输出为 tiles/{z}/0/0.png（1×1 网格），
	///   保证多级导出各级别（无论单图还是分块）都落入统一的 tiles 树，便于下游按 WMTS 协议统一取图。
	/// false（默认）= 未超限时直接输出单张 PNG 文件（原路径，行为与单级导出一致）。
	/// </summary>
	public bool PngForceTiles { get; set; } = false;

	/// <summary>
	/// PNG 是否「合并为一张图」（仅 PNG 光栅端生效；SVG/PDF 忽略）。
	/// true  = 当整幅 1:1×OutputZoom 画布超出位图像素上限时，按瓦片网格行优先流式合并为 1 张 PNG：
	///          默认（PngMergeScaleToCap=false）走【原分辨率行流合并】——合并图尺寸 = 整幅原分辨率
	///          （可超过单图像素上限，不缩小、不丢清晰度，内存 O(行)）；
	///          PngMergeScaleToCap=true 时回退旧行为：等比缩小到上限内合并为 1 张。
	/// false（默认）= 超出上限时按 <see cref="PngTileSize"/> 切分为多个 PNG 文件分块输出（每张 ≤ 单侧/总像素上限，不缩小、不丢清晰度）。
	/// </summary>
	public bool PngMergeToSingle { get; set; } = false;

	/// <summary>
	/// PNG 超限合并时是否「等比缩小到上限内」（仅 PNG 光栅端、且 PngMergeToSingle=true 且超限时生效）。
	/// false（默认，P6 起）= 原分辨率行流合并：合并图尺寸 = 整幅原分辨率（可超过 16384/边、32M 像素上限，
	///   逐行流式写盘，内存 O(行)，不缩小、不丢清晰度）；
	/// true = 旧行为：统一比例等比缩小到上限内合并为 1 张（清晰度低于 OutputZoom）。
	/// </summary>
	public bool PngMergeScaleToCap { get; set; } = false;

	/// <summary>
	/// PNG 分块瓦片像素边长上限（仅 PNG 光栅端、且 PngMergeToSingle=false 时生效）。
	/// 每个瓦片画布边长不超过该值（同时受单侧 16384 / 总像素 3200 万上限约束），
	/// 整幅画布按该网格切分为 N×M 个瓦片文件。默认 4096。
	/// </summary>
	public int PngTileSize { get; set; } = 4096;

	/// <summary>
	/// PNG 分块触发阈值（像素，仅 PNG 光栅端生效；2026-11-02 cad2png 需求）。
	/// 整幅 1:1×OutputZoom 画布任一维 &gt; 该值时，即使未达 16384 单侧 / 3200 万总像素位图上限，
	/// 也按 <see cref="PngTileSize"/> 网格分块流式输出（不缩小、不丢清晰度）。
	/// 0（默认）= 保持既有语义（仅超位图上限才分块，GDB2CAD 宿主行为不变）；
	/// cad2png CLI 默认传 4096（「默认 4096×4096，大于这个尺寸的文件自动分片」）。
	/// 注意：仅触发分块，不改变合并路径（合并仍仅由位图上限触发，见 <see cref="PngMergeToSingle"/>）。
	/// </summary>
	public int PngTileThreshold { get; set; } = 0;

	/// <summary>
	/// PNG 分块时是否启用「点精灵缓存」（仅 PNG 光栅端、且 PngMergeToSingle=false 分块时生效）。
	/// ⚠️ 默认 false（实测结论）：点符号在瓦片内本就稀疏，精灵缓存把点按 4r 包围盒渲染成大张离屏精灵，
	///   blit 时按【精灵面积】逐像素遍历（99% 透明像素也要过一遍），开销远大于直接重画小字形。
	///   实测 800 点/36 瓦片：开缓存 24.4s vs 关缓存 6.4s（**慢 4 倍**），且引入亚像素相位差（约 0.07% 像素）。
	///   空间剔除（像素级精确，已默认开启）才是「PNG 太慢」的主解法（剔除 95%）。
	/// true = 相同点符号（同 $PDMODE/半径/线宽/颜色/背景）离屏渲染 1 次 + 整数平移 blit；仅当存在大量
	///   【大块/高密度】重复符号（渲染成本 ≫ blit 面积成本）时才有收益，一般点块场景请勿开启。
	/// </summary>
	public bool PngPointSpriteCache { get; set; } = false;

	/// <summary>
	/// 离群范围守护（画布范围稳健化，PNG/SVG/PDF 共用，2026-11-02）。
	/// 背景：源数据含远离主体的离群几何（如 DGN 转换引入的镜像脏点，距主体 8 万公里）时，
	/// 几何并集范围把画布撑到 8 万 km → 瓦片数从 15×10 爆炸到 237,509×10（约 240 万张，其中 99.99% 空白）。
	/// true（默认）= 当并集范围「绝对巨大且相对主体显著偏大」时，画布范围收缩到分位数主体范围+边距：
	///   主体 = 实体中心点 0.1%~99.9% 分位盒（≥50 个有效实体时启用分位，否则仅按绝对上限判断）；
	///   触发 = 某轴 并集 &gt; <see cref="ExtentOutlierCapMeters"/> 且 并集 &gt; <see cref="ExtentOutlierFactor"/>×主体；
	///   收缩 = 主体各轴外扩 5%（下限 250 m）作为新范围。主体外的实体仍参与渲染，
	///   其越界部分由画布裁剪（像素级安全，与空间剔除同口径）。
	/// false = 完全按几何并集定范围（旧行为；确有超大范围成图需求时关闭）。
	/// </summary>
	public bool ExtentOutlierGuard { get; set; } = true;

	/// <summary>
	/// 离群范围守护的「相对主体放大倍数」阈值（仅 <see cref="ExtentOutlierGuard"/>=true 时生效）。
	/// 某轴并集 &gt; 该倍数×主体宽度 才视为离群（默认 50；与绝对上限为 AND 关系）。
	/// </summary>
	public double ExtentOutlierFactor { get; set; } = 50.0;

	/// <summary>
	/// 离群范围守护的「绝对上限」（米，仅 <see cref="ExtentOutlierGuard"/>=true 时生效）。
	/// 某轴并集 &gt; 该值 且 &gt; 倍数×主体 才视为离群（默认 300000 = 300 km，
	/// 覆盖单幅大比例尺地形图/厂区图/管线图的常见量级；超大区域图可上调或关闭守护）。
	/// </summary>
	public double ExtentOutlierCapMeters { get; set; } = 300_000.0;

	/// <summary>
	/// PNG 分块时跳过「无数据」瓦片（仅 PNG 分块端 WritePngTiled 生效，2026-11-02）。
	/// true（默认）= 瓦片数据区域内无任何相交实体（空间剔除判定 0 像素贡献）时不写文件、
	///   也不进入结果瓦片列表（坐标 JSON 相应不列出该瓦片）——空白瓦片不占磁盘、不占时间。
	///   透明背景下缺瓦片 = 透明；白底背景下缺瓦片由下游按白底处理。
	/// false = 旧行为：网格内每个瓦片都写文件（含全空白瓦片）。
	/// </summary>
	public bool SkipEmptyTiles { get; set; } = true;

	/// <summary>
	/// 日志回调（进度/告警）。
	/// </summary>
	public Action<string> Log { get; set; }

	/// <summary>
	/// 转换进度回调（2026-11-02 cad2png/cad2svg 进度监控需求）。
	/// null（默认）= 禁用，既有宿主行为逐字节不变。
	/// phase 取值：extents（范围计算）/ render（单图逐实体渲染）/ tile（瓦片或条带渲染）/
	/// save（写盘）/ done（完成）；Percent 0.0~1.0 为整幅总体进度；Current/Total 为当前粒度计数
	/// （实体数或瓦片数）；Message 为人类可读说明（实体类型/瓦片坐标等）。
	/// ⚠️ 回调在导出线程（主线程）内联触发，应保持轻量（UI 宿主请自行 BeginInvoke 切主线程）。
	/// 与 <see cref="ACadSharp.Formats.GeoJson.GeoJsonConfiguration"/>.Progress 同一 <see cref="ImageProgress"/> 类型，
	/// PNG/SVG/PDF/GeoJSON 四种导出统一可监控。
	/// </summary>
	public Action<ImageProgress> Progress { get; set; }

	/// <summary>
	/// 支持文件名（如 "AAA.SHX"、"simhei.ttf"，大小写不敏感）→ 绝对路径 的解析器。
	/// 由调用方提供搜索目录（Support\fonts + 祖先目录等）；找不到返回 null（调用方按回退策略处理）。
	/// </summary>
	public Func<string, string> FileProvider { get; set; }

	/// <summary>
	/// 弧线/圆采样精度（每整圆的细分点数下限，复杂线型走查另按弧长自适应）。
	/// </summary>
	public int ArcPrecision { get; set; } = 256;

	/// <summary>
	/// 变宽折线（逐顶点宽度 + bulge 弧段）偏移轮廓构建器（契约 <see cref="ITaperOffsetBuilder"/>）。
	/// 默认 <c>new <see cref="TaperOffset"/>()</c>（本包内置实现，算法自 GDB2CAD.Core.TaperOffset
	/// 逐字节移植）：含 bulge 的锥形/云形折线按真实弧偏移渲染，宿主无需注入。
	/// 显式赋 <c>null</c> 回退逐顶点弦偏移（R10 前行为）。
	/// </summary>
	public ITaperOffsetBuilder TaperOffsetBuilder { get; set; } = new TaperOffset();

	internal void Notify(string message)
	{
		this.Log?.Invoke(message);
	}
}
