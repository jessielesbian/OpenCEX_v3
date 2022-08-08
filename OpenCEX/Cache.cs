using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCEX
{
	/// <summary>
	/// A strong reference that is converted to a weak reference after a set amount of time
	/// </summary>
	public sealed class SoftReference<T> where T : class
	{
		private readonly int expiry;
		private volatile int weakeners;
		public SoftReference(T obj, int time){
			if(time < 0){
				throw new ArgumentOutOfRangeException("Negative soft reference lifetime");
			}
			t = obj ?? throw new ArgumentNullException(nameof(obj));
			wr = new WeakReference<T>(obj);
			expiry = time;
			TryRenew();
		}

		/// <summary>
		/// Attempts to renew the soft reference
		/// </summary>
		public async void TryRenew(){
			//Optimistically assume that we won't get renewed
			int id = Interlocked.Increment(ref weakeners);

			//Renew-after-expiry
			if(t is null){
				if(wr.TryGetTarget(out T temp)){
					t = temp; //We can be reinstated
				} else{
					return; //We are already dead
				}
			}

			await Task.Delay(expiry);

			//Check if we have been renewed
			if (weakeners == id)
			{
				t = null;
			}
		}

		public bool TryGetTarget(out T res){
			res = t;
			return res is null ? wr.TryGetTarget(out res) : true;
		}

		private readonly WeakReference<T> wr;
		private volatile T t;
	}

	/// <summary>
	/// A LRU cache
	/// </summary>
	public sealed class SharedLruCache<K, V>{
		private sealed class Wrapper{
			public readonly K key;
			public V value;
			public readonly WeakReference<ConcurrentDictionary<K, SoftReference<Wrapper>>> stored;
			public volatile bool active; //Marks us as in-use
			public volatile bool invalid; //Marks us as invalid

			public Wrapper(K key, WeakReference<ConcurrentDictionary<K, SoftReference<Wrapper>>> stored)
			{
				this.key = key;
				this.stored = stored;
			}

			~Wrapper(){
				invalid = true;
				if (active){
					if (stored.TryGetTarget(out ConcurrentDictionary<K, SoftReference<Wrapper>> dict))
					{
						dict.TryRemove(key, out _);
					}
				}
			}
		}
		private readonly ConcurrentDictionary<K, SoftReference<Wrapper>> keyValuePairs = new ConcurrentDictionary<K, SoftReference<Wrapper>>();
		private readonly WeakReference<ConcurrentDictionary<K, SoftReference<Wrapper>>> weakref;
		public SharedLruCache(){
			weakref = new WeakReference<ConcurrentDictionary<K, SoftReference<Wrapper>>>(keyValuePairs);
		}
		public void Set(K key, V val){
		start:
			SoftReference<Wrapper> wrapper = keyValuePairs.GetOrAdd(key, (K key2) =>
			{
				//Expires in 5 minutes
				return new SoftReference<Wrapper>(new Wrapper(key2, weakref), 300000);
			});

			if(!wrapper.TryGetTarget(out Wrapper wrapper2)){
				goto start;
			}

			wrapper.TryRenew();
			wrapper2.value = val;
			wrapper2.invalid = false; //Reinstate if invalid
			Interlocked.MemoryBarrier(); //We need this memory barrier since we are signalling get that it can continue
			wrapper2.active = true;
		}
		public bool Get(K key, out V val){
			if(!keyValuePairs.TryGetValue(key, out SoftReference<Wrapper> wr)){
				val = default;
				return false;
			}

			if(!wr.TryGetTarget(out Wrapper wrapper)){
				val = default;
				return false;
			}

			wr.TryRenew();
			//Spin until wrapper gets marked as active
			while (!wrapper.active){
				
			}
			Interlocked.MemoryBarrier(); //We need this memory barrier since we are being signalled
			if (wrapper.invalid){
				val = default;
				return false;
			}
			
			val = wrapper.value;
			return true;
		}

		/// <summary>
		/// Guarantees that the key doesn't exist in the cache
		/// </summary>
		public void Remove(K key){
			//We simply mark the wrapper as invalid and wait until the next GC cycle
			if(keyValuePairs.TryGetValue(key, out SoftReference<Wrapper> wr)){
				if(wr.TryGetTarget(out Wrapper wrapper)){
					wrapper.invalid = true;
				}
			}
		}
	}
}
