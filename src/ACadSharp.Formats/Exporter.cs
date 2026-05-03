using ACadSharp.IO;
using ACadSharp.Tables;
using System;
using System.IO;

namespace ACadSharp.Formats;

public abstract class Exporter : IExporter
{
	public event NotificationEventHandler OnNotification;

	protected readonly Stream _stream;

	protected Exporter(Stream stream)
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

	protected void triggerNotification(string message, NotificationType notificationType, Exception ex = null)
	{
		this.triggerNotification(this, new NotificationEventArgs(message, notificationType, ex));
	}

	protected void triggerNotification(object sender, NotificationEventArgs e)
	{
		this.OnNotification?.Invoke(sender, e);
	}
}