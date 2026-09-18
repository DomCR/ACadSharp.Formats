using ACadSharp.IO;
using System;

namespace ACadSharp.Formats.Pdf;

/// <summary>
/// Configuration for the PDF export process.
/// </summary>
public class PdfConfiguration
{
	/// <summary>
	/// Notification event to get information about the export process.
	/// </summary>
	/// <remarks>
	/// The notification system informs about any issue or non critical errors during the export.
	/// </remarks>
	public event NotificationEventHandler OnNotification;

	/// <summary>
	/// Number of divisions performed in the arcs when drawing the shape.
	/// </summary>
	public ushort ArcPrecision { get; set; } = 256;

	/// <summary>
	/// Decimal format and precision set for the pdf file.
	/// </summary>
	public string DecimalFormat { get; set; } = "0.####";

	/// <summary>
	/// Set the dot size.
	/// </summary>
	/// <remarks>
	/// The units used to draw the points are the same as the paper.
	/// </remarks>
	public double DotSize { get; set; } = 0.01d;

	internal void Notify(string message, NotificationType notificationType, Exception ex = null)
	{
		this.OnNotification?.Invoke(this, new NotificationEventArgs(message, notificationType, ex));
	}
}