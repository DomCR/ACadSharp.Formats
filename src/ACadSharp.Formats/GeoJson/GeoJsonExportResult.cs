using System.Collections.Generic;

namespace ACadSharp.Formats.GeoJson;

/// <summary>
/// GeoJSON 导出结果（与 <see cref="Image.ImageExportResult"/> 平级）。
/// DataX0/X1/Y0/Y1 = 导出几何的模型坐标范围（Y 向上，无 2% 渲染边距），
/// 供下游生成「GeoJSON 坐标 ↔ PNG 像素」换算关系。
/// </summary>
public sealed class GeoJsonExportResult
{
	/// <summary>是否成功。</summary>
	public bool Success { get; set; }

	/// <summary>输出文件路径（outPath 为 null 的统计模式 = null）。</summary>
	public string OutputPath { get; set; }

	/// <summary>输出字节数。</summary>
	public long OutputBytes { get; set; }

	/// <summary>Feature 总数（含块展开后的块内实体）。</summary>
	public int FeatureCount { get; set; }

	/// <summary>带 xdata 属性的 Feature 数。</summary>
	public int XDataFeatureCount { get; set; }

	/// <summary>是否取得有效范围。</summary>
	public bool HasExtents { get; set; }

	/// <summary>范围 X 下界（模型坐标）。</summary>
	public double DataX0 { get; set; }

	/// <summary>范围 X 上界（模型坐标）。</summary>
	public double DataX1 { get; set; }

	/// <summary>范围 Y 下界（模型坐标）。</summary>
	public double DataY0 { get; set; }

	/// <summary>范围 Y 上界（模型坐标）。</summary>
	public double DataY1 { get; set; }

	/// <summary>未转换实体类型 → 次数（SHAPE/代理实体等）。</summary>
	public IReadOnlyDictionary<string, int> Skipped { get; set; } = new System.Collections.Generic.Dictionary<string, int>();

	/// <summary>未转换实体总数。</summary>
	public int SkippedTotal { get; set; }

	/// <summary>模型空间顶层实体类型 → 次数（统计用，不含块内实体）。</summary>
	public IReadOnlyDictionary<string, long> TypeCounts { get; set; } = new System.Collections.Generic.Dictionary<string, long>();
}
