using System;
using ACadSharp.Entities;

namespace ACadSharp.Formats.Image;

internal partial class ImageEngine
{
	// ===== 转换进度通知（2026-11-02 cad2png/cad2svg 进度监控需求）=====
	// 统一出口：null 安全（_opt.Progress == null 时零开销返回，既有宿主行为不变）、
	// 回调内任何异常都不允许打断导出（try/catch 吞掉）。
	// 进度语义（PNG/SVG/PDF 一致）：
	//   extents  0.00~0.08  范围计算（ComputeExtents + 画布尺寸）
	//   render   0.10~0.90  单图逐实体光栅化/矢量生成（Current/Total = 实体序号）
	//   tile     0.10~0.97  分块逐瓦片 / 合并逐条带（Current/Total = 瓦片或条带序号）
	//   save     0.92~0.99  写盘
	//   done     1.00       完成
	private void ReportProgress(string phase, double percent, int current, int total, string message)
	{
		var cb = _opt?.Progress;
		if (cb == null) return;
		try
		{
			if (double.IsNaN(percent)) percent = 0;
			if (percent < 0) percent = 0; else if (percent > 1) percent = 1;
			cb(new ImageProgress { Phase = phase, Percent = percent, Current = current, Total = total, Message = message });
		}
		catch
		{
			// 进度回调异常不得打断导出
		}
	}

	// 实体渲染循环进度节流：每 100 个实体或首尾必报（宿主刷新频率 ≈ 每 1% 一次，足够监控且无开销）。
	private void ReportEntityProgress(string phase, int index, int total, Entity ent)
	{
		if (_opt?.Progress == null) return;
		if (index != 0 && index != total - 1 && index % 100 != 0) return;
		string t = null;
		try { t = ent?.GetType().Name ?? null; } catch { }
		ReportProgress(phase, 0.10 + 0.80 * (index + 1.0) / Math.Max(1, total), index + 1, total, t);
	}
}
