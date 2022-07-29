using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using StackExchange.Redis;

namespace OpenCEX
{
	public interface IPluginEntry {
		/// <summary>
		/// Initializes the OpenCEX plugin
		/// </summary>
		public void Init();
	}
}
