using CSUtilities;
using System.IO;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: TestFramework("ACadSharp.Formats.Tests.TestSetup", "ACadSharp.Formats.Tests")]

namespace ACadSharp.Formats.Tests;

public sealed class TestSetup : XunitTestFramework
{
	public TestSetup(IMessageSink messageSink)
	  : base(messageSink)
	{
		this.init();
	}

	private void init()
	{
		TestVariables.CreateOutputFolders();
	}
}
