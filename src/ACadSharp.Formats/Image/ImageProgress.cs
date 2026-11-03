namespace ACadSharp.Formats.Image;

/// <summary>
/// 转换进度通知（2026-11-02 cad2png/cad2svg/cad2geojson 进度监控需求）。
/// 宿主经 <see cref="ImageConfiguration.Progress"/> 或
/// <see cref="ACadSharp.Formats.GeoJson.GeoJsonConfiguration.Progress"/> 接收；null = 禁用。
/// 回调在导出线程（主线程）内联触发，应保持轻量。
/// </summary>
public sealed class ImageProgress
{
	/// <summary>
	/// 阶段：extents（范围计算）/ render（单图逐实体渲染）/ tile（瓦片或条带渲染）/
	/// save（写盘）/ done（完成）。
	/// </summary>
	public string Phase { get; set; }

	/// <summary>总体进度 0.0 ~ 1.0。</summary>
	public double Percent { get; set; }

	/// <summary>当前粒度计数（实体序号或瓦片/条带序号，1-based）。</summary>
	public int Current { get; set; }

	/// <summary>总数（0 = 未知）。</summary>
	public int Total { get; set; }

	/// <summary>人类可读说明（实体类型名、瓦片坐标等）。</summary>
	public string Message { get; set; }

	public override string ToString() =>
		$"{Phase} {Percent * 100.0:F1}% {Current}/{Total} {Message}".Trim();
}
