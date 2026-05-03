using CSUtilities;
using System.IO;

namespace ACadSharp.Formats.Tests;

public static class TestVariables
{
	public static string OutputPdfFolder { get { return EnvironmentVars.Get<string>("OUTPUT_PDF"); } }

	public static string OutputSamplesFolder { get { return EnvironmentVars.Get<string>("OUTPUT_SAMPLES_FOLDER"); } }

	public static string OutputSvgFolder { get { return EnvironmentVars.Get<string>("OUTPUT_SVG"); } }

	public static string SamplesFolder { get { return EnvironmentVars.Get<string>("SAMPLES_FOLDER"); } }

	static TestVariables()
	{
		EnvironmentVars.SetIfNull("SAMPLES_FOLDER", "../../../../../samples/");
		EnvironmentVars.SetIfNull("OUTPUT_SAMPLES_FOLDER", "../../../../../samples/out");
		EnvironmentVars.SetIfNull("OUTPUT_SVG", "../../../../../samples/out/svg");
		EnvironmentVars.SetIfNull("OUTPUT_PDF", "../../../../../samples/out/pdf");
	}

	public static void CreateOutputFolders()
	{
		string outputSamplesFolder = OutputSamplesFolder;
		string outputSvgFolder = OutputSvgFolder;
		string outputPdfFolder = OutputPdfFolder;

#if NETFRAMEWORK
			string curr = System.AppDomain.CurrentDomain.BaseDirectory;
			outputSamplesFolder = Path.GetFullPath(Path.Combine(curr, OutputSamplesFolder));
			outputSvgFolder = Path.GetFullPath(Path.Combine(curr, OutputSvgFolder));
			outputPdfFolder = Path.GetFullPath(Path.Combine(curr, OutputPdfFolder));
#endif

		craateFolderIfDoesNotExist(outputSamplesFolder);
		craateFolderIfDoesNotExist(outputSvgFolder);
		craateFolderIfDoesNotExist(outputPdfFolder);
	}

	private static void craateFolderIfDoesNotExist(string path)
	{
		if (!Directory.Exists(path))
		{
			Directory.CreateDirectory(path);
		}
	}
}