using System;
using System.Collections.Generic;
using System.Text;

namespace OpenCEX
{
	public sealed class BlockchainFactory{
		
	}
	public static partial class StaticUtils{
		private static readonly Dictionary<string, BlockchainFactory> blockchainFactories = new Dictionary<string, BlockchainFactory>();
		private static readonly object blockchainFactoryLocker = new object();
		public static void RegisterBlockchainFactory(){
			
		}
	}
}
