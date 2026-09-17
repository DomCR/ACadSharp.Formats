using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using System;
using System.IO;

namespace ACadSharp.Formats;

/// <summary>
/// Abstract base class for exporting CAD documents and layouts to various formats.
/// </summary>
public abstract class ExporterBase : IExporter
{
	/// <summary>
	/// Occurs when a notification is triggered during the export process.
	/// </summary>
	public event NotificationEventHandler OnNotification;

	/// <summary>
	/// The output stream for the exported data.
	/// </summary>
	protected readonly Stream stream;

	/// <summary>
	/// Initializes a new instance of the <see cref="ExporterBase"/> class.
	/// </summary>
	/// <param name="stream">The stream to write the exported data to.</param>
	protected ExporterBase(Stream stream)
	{
		this.stream = stream;
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		this.stream.Dispose();
	}

	/// <summary>
	/// Exports a specific block record.
	/// </summary>
	/// <param name="record">The block record to export.</param>
	public abstract void Export(BlockRecord record);

	/// <summary>
	/// Exports a complete CAD document.
	/// </summary>
	/// <param name="document">The CAD document to export.</param>
	public abstract void Export(CadDocument document);

	/// <summary>
	/// Exports a specific layout.
	/// </summary>
	/// <param name="layout">The layout to export.</param>
	public abstract void Export(Layout layout);

	protected void triggerNotification(string message, NotificationType notificationType, Exception ex = null)
	{
		this.triggerNotification(this, new NotificationEventArgs(message, notificationType, ex));
	}

	protected void triggerNotification(object sender, NotificationEventArgs e)
	{
		this.OnNotification?.Invoke(sender, e);
	}
}