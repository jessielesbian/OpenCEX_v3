using System;

namespace OpenCEX
{
	public sealed class TestPlugin : IPluginEntry
	{
		public void Init()
		{
			Console.WriteLine("Authentication plugin loaded");
		}
	}
}
