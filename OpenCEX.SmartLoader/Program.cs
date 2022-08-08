using System;
using System.Collections.Generic;

namespace OpenCEX
{
	/// <summary>
	/// A plugin that is inhibited on load
	/// </summary>
	public abstract class InhibitOnLoadPlugin : IPluginEntry
	{
		public void Init()
		{
			if(StaticUtils.InhibitPlugin(GetType())){
				InitImpl();
			}
		}

		protected abstract void InitImpl();
	}
}
