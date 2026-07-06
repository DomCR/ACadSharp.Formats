using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using System;

namespace ACadSharp.Formats;

/// <summary>
/// Defines the contract for exporting CAD entities and documents to a specific format.
/// </summary>
public interface IExporter : IDisposable
{
	/// <summary>
	/// Event triggered when a notification occurs during the export process.
	/// </summary>
	public event NotificationEventHandler OnNotification;

	/// <summary>
	/// Exports a specific block record.
	/// </summary>
	/// <param name="record">The block record to export.</param>
	public void Export(BlockRecord record);

	/// <summary>
	/// Exports an entire CAD document.
	/// </summary>
	/// <param name="document">The CAD document to export.</param>
	public void Export(CadDocument document);

	/// <summary>
	/// Exports a specific layout.
	/// </summary>
	/// <param name="layout">The layout to export.</param>
	public void Export(Layout layout);
}