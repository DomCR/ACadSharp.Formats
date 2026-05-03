using ACadSharp.IO;
using ACadSharp.Tables;
using System;

namespace ACadSharp.Formats;

public interface IExporter : IDisposable
{
	public event NotificationEventHandler OnNotification;

	public void Export(BlockRecord record);

	public void Export(CadDocument document);
}
