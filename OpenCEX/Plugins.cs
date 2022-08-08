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

	/// <summary>
	/// NoIntercept = only add as base request method, throws if added as interceptor
	/// AllowIntercept = may add as base or intercept request method
	/// ForceIntercept = only add as interceptor, delay add until base added first
	/// </summary>
	public enum InterceptMode : byte{
		NoIntercept, AllowIntercept, ForceIntercept
	}
}
