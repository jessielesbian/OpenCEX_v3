using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCEX
{
	/// <summary>
	/// OpenCEX v3.0 ultra high performance LRU cache with a background eviction thread
	/// </summary>
	public sealed class LruCache<K, V> : IDisposable
	{
		private volatile int gcsignal;
		private sealed class TrackedValue{
			public LinkedListNode<K> linkedListNode;
			public V value;

			public TrackedValue(V value)
			{
				this.value = value;
			}
		}
		public LruCache(){
			//Start garbage collector
			CollectGarbage();
		}
		private readonly Dictionary<K, TrackedValue> dict = new Dictionary<K, TrackedValue>();
		private readonly LinkedList<K> trackedKeys = new LinkedList<K>();
		private readonly SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);
		
		public async Task<V> Get(K key, bool remove){
			await semaphoreSlim.WaitAsync();
			try
			{	
				if (dict.TryGetValue(key, out TrackedValue trackedValue))
				{
					trackedKeys.Remove(trackedValue.linkedListNode);
					if(remove){
						dict.Remove(trackedValue.linkedListNode.Value);
					} else{
						trackedValue.linkedListNode = trackedKeys.AddFirst(key);
					}
					
					return trackedValue.value;
				}
				else
				{
					CacheMissException.Throw();
					throw new Exception("Failed to throw cache miss exception (should not reach here)");
				}
			} finally{
				semaphoreSlim.Release();
			}
			
		}
		
		public async Task Set(K key, V value){
			await semaphoreSlim.WaitAsync();
			try
			{
				if (dict.TryGetValue(key, out TrackedValue trackedValue))
				{
					trackedKeys.Remove(trackedValue.linkedListNode);
					trackedValue.linkedListNode = trackedKeys.AddFirst(key);
					trackedValue.value = value;
				}
				else
				{
					trackedValue = new TrackedValue(value)
					{
						linkedListNode = trackedKeys.AddFirst(key)
					};
					dict.Add(key, trackedValue);
				}
			} finally{
				semaphoreSlim.Release();
			}
		}
		
		/// <summary>
		/// Non-blocking garbage collection
		/// </summary>
		private async void CollectGarbage(){

			//Keep on GCing until we got abort signal
			while (gcsignal == 0)
			{
				await Task.Delay(1); //GC runs every microsecond
				await semaphoreSlim.WaitAsync();

				//Collect all the garbage
				try
				{
					int count = trackedKeys.Count;
					if (count > 65536)
					{
						count -= 65535;
						for (int i = 0; ++i < count && gcsignal == 0;)
						{
							dict.Remove(trackedKeys.Last.Value);
							trackedKeys.RemoveLast();
						}
					}
				}
				finally
				{
					semaphoreSlim.Release();
				}
			}
		}

		/// <summary>
		/// This method MUST be called to stop the cache eviction background loop, otherwise we will waste CPU and RAM
		/// </summary>
		public void Dispose(){
			//Signal GC abort
			gcsignal = 1;
		}


	}
}
