using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCEX
{
	//Stolen from StackOverflow user xtadex, augmented by Jessie Lesbian
	public struct AsyncReaderWriterLock : IDisposable
	{
		private readonly SemaphoreSlim _readSemaphore;
		private readonly SemaphoreSlim _writeSemaphore;
		private long _readerCount;
		private int disposedValue;

		public static AsyncReaderWriterLock Create(){
			return new AsyncReaderWriterLock(new SemaphoreSlim(1, 1), new SemaphoreSlim(1, 1));
		}
		private AsyncReaderWriterLock(SemaphoreSlim readSemaphore, SemaphoreSlim writeSemaphore) : this()
		{
			_readSemaphore = readSemaphore;
			_writeSemaphore = writeSemaphore;
		}

		private async Task AcquireWriterSemaphore(CancellationToken token)
		{
			await _writeSemaphore.WaitAsync(token).ConfigureAwait(false);

		}

		public async Task AcquireWriterLock(CancellationToken token = default)
		{
			await AcquireWriterSemaphore(token).ConfigureAwait(false);
			await SafeAcquireReadSemaphore(token).ConfigureAwait(false);
		}

		public async Task UpgradeToWriterLock(CancellationToken token = default)
		{
			await AcquireWriterSemaphore(token).ConfigureAwait(false);
			if (Interlocked.Decrement(ref _readerCount) > 0)
			{
				await SafeAcquireReadSemaphore(token).ConfigureAwait(false);
			}
		}

		public void ReleaseWriterLock()
		{
			_readSemaphore.Release();
			_writeSemaphore.Release();
		}

		public async Task AcquireReaderLock(CancellationToken token = default)
		{
			await AcquireWriterSemaphore(token).ConfigureAwait(false);
			if (Interlocked.Increment(ref _readerCount) == 1)
			{
				try
				{
					await SafeAcquireReadSemaphore(token).ConfigureAwait(false);
				}
				catch
				{
					Interlocked.Decrement(ref _readerCount);

					throw;
				}
			}
			_writeSemaphore.Release();
		}

		public void ReleaseReaderLock()
		{
			if (Interlocked.Decrement(ref _readerCount) == 0)
			{
				_readSemaphore.Release();
			}
		}

		private async Task SafeAcquireReadSemaphore(CancellationToken token)
		{
			try
			{
				await _readSemaphore.WaitAsync(token).ConfigureAwait(false);
			}
			catch
			{
				_writeSemaphore.Release();

				throw;
			}
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposedValue, 1) == 0)
			{
				GC.SuppressFinalize(this);
				_writeSemaphore.Dispose();
				_readSemaphore.Dispose();
			}
		}
	}
}
