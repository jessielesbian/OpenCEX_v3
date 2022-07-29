using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Globalization;
using StackExchange.Redis;

namespace OpenCEX
{
	/// <summary>
	/// Exposes shit for multithreaded crediting and debiting
	/// </summary>
	public struct BalancesManager
	{
		private readonly ConcurrentDictionary<string, BigInteger> dict;
		private readonly RedisKVHelper redisKVHelper;
		public readonly bool valid;
		public BalancesManager(RedisKVHelper redisKVHelper)
		{
			dict = new ConcurrentDictionary<string, BigInteger>();
			this.redisKVHelper = redisKVHelper;
			valid = true;
		}

		/// <summary>
		/// Credits or debits client funds
		/// </summary>
		public void CreditOrDebit(string key, BigInteger val){
			//NOTE: We are performing optimistic debiting, meaning that negative balances are allowed in the middle of a transaction, but are never allowed to be flushed to the database. This minimizes the chance of we having to re-run a transaction.
			dict.AddOrUpdate(key, val, (string key2, BigInteger old) => {
				return old + val;
			});
		}


		/// <summary>
		/// Reads the shadow balance of an account
		/// </summary>
		/// <returns></returns>
		public async Task<BigInteger> ShadowBalanceOf(string key){
			RedisKey[] redisKeys = new RedisKey[16];
			string key2 = key + '_';
			for (int i = 0; i < 16; ++i)
			{
				redisKeys[i] = key2 + i;
			}
			RedisValue[] redisValues = new RedisValue[16];
			await redisKVHelper.MultiGet(redisKeys, redisValues, 0);
			Queue<Task> releaseTasks = new Queue<Task>();
			BigInteger effectiveBalance = BigInteger.Zero;
			for (int i = 0; i < 16; ++i)
			{
				RedisValue redisValue = redisValues[i];
				if(redisValue.IsNull){
					releaseTasks.Enqueue(redisKVHelper.Release(redisKeys[i]));
				} else{
					effectiveBalance += BigInteger.Parse(redisValue, NumberStyles.None);
				}
			}
			if(dict.TryGetValue(key, out BigInteger delta)){
				BigInteger shadow = effectiveBalance + delta;
				if(shadow.Sign < 0){
					UserError.Throw("Negative shadow balance", 11);
				}
				while(releaseTasks.TryDequeue(out Task tsk)){
					await tsk;
				}
				return shadow;
			} else{
				while (releaseTasks.TryDequeue(out Task tsk))
				{
					await tsk;
				}
				return effectiveBalance;
			}
		}

		/// <summary>
		/// Flushes the balances to underlying storage (the balances manager should not be touched during this operation)
		/// </summary>
		public async Task Flush(){
			try{
				foreach (KeyValuePair<string, BigInteger> kvp in dict)
				{
					string key = kvp.Key;
					BigInteger val = kvp.Value;
					int sign = val.Sign;
					if(sign == 1){
						key += '_' + RandomNumberGenerator.GetInt32(0, 16).ToString();
						//Update balance in database
						string res = await redisKVHelper.Get(key);
						Console.WriteLine(res);
						await redisKVHelper.Set(key, (res is null ? val : (BigInteger.Parse(res, NumberStyles.None) + val)).ToString());
					} else if(sign == -1){
						foreach (int index in Enumerable.Range(0, 16).OrderBy(_ => RandomNumberGenerator.GetInt32(0, int.MaxValue))){
							string key2 = key + '_' + index.ToString();
							string res = await redisKVHelper.Get(key2);
							if (res is null){
								//Release optimistic lock on this pie
								await redisKVHelper.Release(key2);
							} else{
								val += BigInteger.Parse(res, NumberStyles.None);
								sign = val.Sign;
								if (sign > -1)
								{
									if (sign == 0)
									{
										await redisKVHelper.Set(key2, (string)null);
									}
									else
									{
										await redisKVHelper.Set(key2, val.ToString());
									}
									goto debited;
								}
								else
								{
									await redisKVHelper.Set(key2, (string)null);
								}
							}
						}
						UserError.Throw("Debit exceeds account balance", 10);
						debited:;
					}
				}
			} finally{
				dict.Clear();
			}
			
		}

	}
}
