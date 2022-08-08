using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace OpenCEX
{
	public sealed class UserError : Exception{
		public readonly int code;
		private UserError(string reason, int code) : base(reason)
		{
			this.code = code;
		}
		//maxcode: 21
		public static void Throw(string reason, int code){
			throw new UserError(reason, code);
		}
	}

	/// <summary>
	/// Thrown by plugin to indicate lack of intention to intercept a particular request method
	/// </summary>
	public sealed class NotInterceptedException : Exception{
		private NotInterceptedException(){
			
		}
		public static void Throw(){
			throw new NotInterceptedException();
		}
	}

	public sealed class OptimisticRepeatException : Exception{
		private readonly Task cleanUp;

		private OptimisticRepeatException(Task cleanUp)
		{
			this.cleanUp = cleanUp;
		}

		public async Task WaitCleanUp(){
			if(cleanUp is { }){
				await cleanUp;
			}
		}

		public static void Throw(Task cleanUp){
			throw new OptimisticRepeatException(cleanUp);
		}
	}

	public sealed class CacheMissException : Exception
	{
		private CacheMissException(){
			
		}

		public static void Throw(){
			throw new CacheMissException();
		}
	}
}
