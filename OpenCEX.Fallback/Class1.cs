using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCEX{
	/// <summary>
	/// Default implementation of deposits and withdrawals
	/// </summary>
	public static class Fallback{
		private static volatile int status;
		private static Task<object> Err(object[] a, ulong b, WebSocketHelper c){
			UserError.Throw("Unknown coin", 20);
			return StaticUtils.doNothing3;
		}
		/// <summary>
		/// This must be called before defining the following request methods
		/// 1. getdepositaddress
		/// 2. withdraw
		/// 3. deposit
		/// </summary>
		public static void Fill(){
			if(Interlocked.Exchange(ref status, 1) == 0){
				StaticUtils.RegisterRequestMethod("getdepositaddress", Err, InterceptMode.NoIntercept);
				StaticUtils.RegisterRequestMethod("withdraw", Err, InterceptMode.NoIntercept);
				StaticUtils.RegisterRequestMethod("deposit", Err, InterceptMode.NoIntercept);
			}
		}
	}
}
