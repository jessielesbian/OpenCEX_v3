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
	public static class AuthStaticUtils{
		private static void Main(){
			
		}
	}
}
