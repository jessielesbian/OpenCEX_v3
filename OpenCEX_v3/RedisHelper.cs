using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCEX
{
	
	/// <summary>
	/// A Redis key-value reader with support for optimistic locking and caching
	/// </summary>
	public struct RedisKVHelper : IDisposable{
		private sealed class RedisKVCacheDescriptor{
			public readonly RedisKey key;
			public readonly RedisValue val;
			public RedisValue newval;
			public bool dirty;
			public bool docommit;

			public RedisKVCacheDescriptor(RedisKey key, RedisValue val)
			{
				this.key = key;
				this.val = val;
				newval = val;
				dirty = false;
			}
		}
		public readonly bool valid;
		private readonly Queue<RedisKVCacheDescriptor> flushingQueue;
		private readonly Dictionary<RedisKey, RedisKVCacheDescriptor> cache;
		private readonly SemaphoreSlim locker;

		/// <summary>
		/// Initializes the RedisKVHelper
		/// </summary>
		private RedisKVHelper(Queue<RedisKVCacheDescriptor> a, Dictionary<RedisKey, RedisKVCacheDescriptor> b, SemaphoreSlim c)
		{
			flushingQueue = a;
			cache = b;
			locker = c;
			valid = true;
		}

		public static RedisKVHelper Create(){
			return new RedisKVHelper(new Queue<RedisKVCacheDescriptor>(), new Dictionary<RedisKey, RedisKVCacheDescriptor>(), new SemaphoreSlim(1, 1));
		}
		
		/// <summary>
		/// Acquires an optimistic lock on key
		/// </summary>
		private async Task<RedisKVCacheDescriptor> AcquireOptimisticLock(RedisKey key)
		{
			if(cache.TryGetValue(key, out RedisKVCacheDescriptor desc)){
				return desc;
			} else{
				

				//Optimistic caching: optimistically presume that the cache is up-to-date, since this is checked during commit
				//Also, since the cache is locking, we fall back to redis if that's faster (e.g the server is under heavy loads, so the're are many threads waiting to use the cache)
				SemaphoreSlim semaphoreSlim = new SemaphoreSlim(0, 2);
				Task<RedisValue> readcache = StaticUtils.OptimisticRedisCache.Get(key, false);
				Action release = () => {
					semaphoreSlim.Release();
				};
				readcache.GetAwaiter().OnCompleted(release);
				Task<RedisValue> readredis = StaticUtils.redis.StringGetAsync(key);
				readredis.GetAwaiter().OnCompleted(release);
				await semaphoreSlim.WaitAsync();
				StaticUtils.WaitAndDisposeSemaphore(semaphoreSlim);

				RedisValue value;
				Task cachetsk = null;
				if (readcache.IsCompleted){
					try
					{
						value = await readcache;
					}
					catch (CacheMissException)
					{
						value = await readredis;

						//Optimistic caching is one of the smartest inventions made by this cute anime lesbian
						cachetsk = StaticUtils.OptimisticRedisCache.Set(key, value);
					}
				} else{
					value = await readredis;

					//Optimistic caching is one of the smartest inventions made by this cute anime lesbian
					cachetsk = StaticUtils.OptimisticRedisCache.Set(key, value);
				}
				

				desc = new RedisKVCacheDescriptor(key, value);

				cache.Add(key, desc);
				flushingQueue.Enqueue(desc);
				if(cachetsk is { }){
					await cachetsk;
				}
				desc.docommit = true;
				return desc;
			}
			
		}
		
		/// <summary>
		/// Optimistically locks and sets a key
		/// </summary>
		public async Task Set(RedisKey key, RedisValue value)
		{
			await locker.WaitAsync();
			try{
				RedisKVCacheDescriptor desc = await AcquireOptimisticLock(key);
				desc.dirty = true;
				desc.newval = value;
			} finally{
				locker.Release();
			}
		}

		/// <summary>
		/// Optimistically locks and gets a key
		/// </summary>
		public async Task<RedisValue> Get(RedisKey key)
		{
			await locker.WaitAsync();
			try
			{
				return (await AcquireOptimisticLock(key)).newval;
			}
			finally
			{
				locker.Release();
			}
		}

		/// <summary>
		/// Optimistically locks and gets multiple keys in parallel
		/// </summary>
		public async Task MultiGet(RedisKey[] keys, RedisValue[] output, int offset)
		{
			await locker.WaitAsync();
			try
			{
				int limit = keys.Length;
				Task<RedisKVCacheDescriptor>[] tasks = new Task<RedisKVCacheDescriptor>[limit];
				for(int i = 0; i < limit; ++i){
					tasks[i] = AcquireOptimisticLock(keys[i]);
				}
				RedisValue[] redisValues = new RedisValue[limit];
				for (int i = 0; i < limit; ++i)
				{
					output[i + offset] = redisValues[i] = (await tasks[i]).newval;
				}
			}
			finally
			{
				locker.Release();
			}
		}

		/// <summary>
		/// Release optimistic locks and cancel changes on the key
		/// </summary>
		public async Task Release(RedisKey key)
		{
			await locker.WaitAsync();
			try
			{
				//Reset key and mark as no-commit
				if(cache.TryGetValue(key, out RedisKVCacheDescriptor desc)){
					desc.docommit = false;
					desc.newval = desc.val;
					desc.dirty = false;
				}
			}
			finally
			{
				locker.Release();
			}
		}

		/// <summary>
		/// Flushes the RedisKVHelper to the underlying Redis database, and returns a task that represents optimistic cache update
		/// </summary>
		public async Task<Task> Flush(ITransaction tx){
			Queue<KeyValuePair<RedisKey, RedisValue>> flushingQueue2 = new Queue<KeyValuePair<RedisKey, RedisValue>>();
			await locker.WaitAsync();
			try
			{
				if(flushingQueue.Count == 0){
					return StaticUtils.DoNothing;
				}
				while(flushingQueue.TryDequeue(out RedisKVCacheDescriptor res)){
					if(res.docommit){
						//Optimistic locking + caching: conditional commit
						tx.AddCondition(Condition.StringEqual(res.key, res.val));

						//Enqueue dirty keys for flushing
						if (res.dirty)
						{
							flushingQueue2.Enqueue(new KeyValuePair<RedisKey, RedisValue>(res.key, res.newval));
						}
					}
				}

				KeyValuePair<RedisKey, RedisValue>[] flushingQueue3 = flushingQueue2.ToArray();

				//Update the optimistic caches in the background
				return StaticUtils.UpdateOptimisticRedisCache(tx.StringSetAsync(flushingQueue3), flushingQueue3);
			}
			finally
			{
				locker.Release();
				flushingQueue.Clear();
				cache.Clear();
			}
			
		}
		public void Dispose()
		{
			((IDisposable)locker).Dispose();
		}
	}

	
}
