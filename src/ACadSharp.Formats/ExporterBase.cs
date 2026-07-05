using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using System;
using System.IO;

namespace ACadSharp.Formats;

public abstract class ExporterBase : IExporter
{
	public event NotificationEventHandler OnNotification;

	protected readonly Stream _stream;

	protected ExporterBase(Stream stream)
	{
		this._stream = stream;
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		this._stream.Dispose();
	}

	public abstract void Export(BlockRecord record);

	public abstract void Export(CadDocument document);

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