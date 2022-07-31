using NUnit.Framework;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using System.Collections.Generic;
using System.Numerics;

namespace OpenCEX
{
	public sealed class Tests
	{
		[SetUp]
		public static Task Setup()
		{
			return StaticUtils.WipeDatabase();
		}
		
		[Test]
		public async Task Redis()
		{
		start:
			using (RedisKVHelper redisKVHelper = RedisKVHelper.Create())
			{
				await redisKVHelper.Set("jessielesbian.islesbian", false);
				ITransaction tx = StaticUtils.redis.CreateTransaction();
				Task flushcache = await redisKVHelper.Flush(tx, true);
				if (!await tx.ExecuteAsync())
				{
					await flushcache;
					goto start;
				}
			}

			using (RedisKVHelper redisKVHelper = RedisKVHelper.Create())
			{
				Assert.IsFalse((bool)await redisKVHelper.Get("jessielesbian.islesbian"));
				await redisKVHelper.Set("jessielesbian.islesbian", true);
				ITransaction tx = StaticUtils.redis.CreateTransaction();
				Task flushcache = await redisKVHelper.Flush(tx, true);
				if (!await tx.ExecuteAsync())
				{
					await flushcache;
					goto start;
				}
			}

			Assert.IsTrue((bool)await StaticUtils.redis.StringGetAsync("jessielesbian.islesbian"));

		}

		[Test]
		public async Task JsonRpc()
		{
			//Test batch request
			Assert.IsNull(await StaticUtils.HandleJsonRequest("[]", 0, null));

			//Test invalid json
			Assert.AreEqual("{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": -32700, \"message\": \"Parse error\"}}", await StaticUtils.HandleJsonRequest("Invalid Json", 0, null));

			//Test missing request method
			Assert.AreEqual("{\"error\":{\"code\":-32601,\"message\":\"Method not found\"},\"jsonrpc\":\"2.0\",\"id\":\"\"}", await StaticUtils.HandleJsonRequest("{\"jsonrpc\": \"2.0\", \"id\": \"\", \"method\": \"undefined\", \"params\": []}", 0, null));

			//Test missing notification method
			Assert.IsNull(await StaticUtils.HandleJsonRequest("{\"jsonrpc\": \"2.0\", \"method\": \"undefined\", \"params\": []}", 0, null));

			//Test missing notification method (batched)
			Assert.IsNull(await StaticUtils.HandleJsonRequest("[{\"jsonrpc\": \"2.0\", \"method\": \"undefined\", \"params\": []}]", 0, null));

			//Test missing request method and valid request method (batched)
			Assert.AreEqual("[{\"error\":{\"code\":-32601,\"message\":\"Method not found\"},\"jsonrpc\":\"2.0\",\"id\":\"a\"},{\"result\":[],\"jsonrpc\":\"2.0\",\"id\":\"b\"}]", await StaticUtils.HandleJsonRequest("[{\"jsonrpc\": \"2.0\", \"id\": \"a\", \"method\": \"undefined\", \"params\": []}, {\"jsonrpc\": \"2.0\", \"id\": \"b\", \"method\": \"doNothing\", \"params\": []}]", 0, null));

			//Test valid request method
			Assert.AreEqual("{\"result\":[],\"jsonrpc\":\"2.0\",\"id\":\"\"}", await StaticUtils.HandleJsonRequest("{\"jsonrpc\": \"2.0\", \"id\": \"\", \"method\": \"doNothing\", \"params\": []}", 0, null));
		}

		[Test]
		public async Task BalancesManager()
		{
		start:
			ITransaction tx = StaticUtils.redis.CreateTransaction();
			using (RedisKVHelper redisKVHelper = RedisKVHelper.Create())
			{
				BalancesManager balancesManager = new BalancesManager(redisKVHelper);
				balancesManager.CreditOrDebit("jessielesbian.bitcoins", 10);
				await balancesManager.Flush();
				Task flushcache = await redisKVHelper.Flush(tx, true);

				//Optimistic locking
				if (!await tx.ExecuteAsync())
				{
					await flushcache;
					goto start;
				}
			}
			tx = StaticUtils.redis.CreateTransaction();
			using (RedisKVHelper redisKVHelper = RedisKVHelper.Create())
			{
				BalancesManager balancesManager = new BalancesManager(redisKVHelper);
				balancesManager.CreditOrDebit("jessielesbian.bitcoins", -9);
				await balancesManager.Flush();
				Task flushcache = await redisKVHelper.Flush(tx, true);

				//Optimistic locking
				if (!await tx.ExecuteAsync())
				{
					await flushcache;
					goto start;
				}
			}
			using (RedisKVHelper redisKVHelper = RedisKVHelper.Create())
			{
				BalancesManager balancesManager = new BalancesManager(redisKVHelper);
				balancesManager.CreditOrDebit("jessielesbian.bitcoins", -2);
				try
				{
					await balancesManager.Flush();
				}
				catch (UserError)
				{
					goto pass2;
				}
				Assert.Fail("Negative balance allowed");
			}
		pass2:
			using (RedisKVHelper redisKVHelper = RedisKVHelper.Create())
			{
				BalancesManager balancesManager = new BalancesManager(redisKVHelper);
				
				Assert.AreEqual(BigInteger.One, await balancesManager.ShadowBalanceOf("jessielesbian.bitcoins"));
				balancesManager.CreditOrDebit("jessielesbian.bitcoins", 4);
				Assert.AreEqual((BigInteger)5, await balancesManager.ShadowBalanceOf("jessielesbian.bitcoins"));
				balancesManager.CreditOrDebit("jessielesbian.bitcoins", -10);
				try{
					await balancesManager.ShadowBalanceOf("jessielesbian.bitcoins");
				} catch (UserError){
					return;
				}
				Assert.Fail("Negative shadow balance allowed");
			}
		}
	}
}