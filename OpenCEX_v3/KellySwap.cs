using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Tasks;
using StackExchange.Redis;
using System.Globalization;

namespace OpenCEX
{
	//OpenCEX KellySwap: the advanced-technology automated market maker
	public struct Pair
	{
		public readonly string pri;
		public readonly string sec;
		public readonly string hash;

		public Pair(string pri, string sec)
		{
			StaticUtils.ChkLegalSymbol(pri);
			this.pri = pri;
			this.sec = sec ?? throw new ArgumentNullException(nameof(sec));
			hash = pri + '_' + sec;
		}
		public void EnsureValid()
		{
			if (hash is null)
			{
				throw new NullReferenceException("Uninitialized pair descriptor");
			}
		}
	}
	public sealed class Order
	{
		public readonly BigInteger price;
		public BigInteger balance;
		public readonly ulong id;
		public readonly bool aon;
		public readonly bool ioc;
		public readonly bool buy;
		public readonly DateTimeOffset expiry;
		public readonly Pair pair;

		public Order(BigInteger price, BigInteger balance, ulong id, bool aon, bool ioc, bool buy, DateTimeOffset expiry, Pair pair)
		{
			this.price = price;
			this.balance = balance;
			this.id = id;
			this.aon = aon;
			this.ioc = ioc;
			this.buy = buy;
			this.expiry = expiry;
			this.pair = pair;
		}
	}

	/// <summary>
	/// Represents a KellySwap pool (NOTE: the operators here do not debit, since debiting should be done by caller)
	/// </summary>
	public sealed class KellySwapPool
	{
		public BigInteger PrimaryReserve { get; private set; }
		public BigInteger SecondaryReserve { get; private set; }
		public BigInteger TotalSupply { get; private set; }

		public readonly Pair pair;
		private readonly RedisKVHelper redisKVHelper;
		private readonly AsyncReaderWriterLock asyncReaderWriterLock = new AsyncReaderWriterLock();
		private readonly BalancesManager balancesManager;

		public static async Task<KellySwapPool> Create(Pair pair, RedisKVHelper redisKVHelper, BalancesManager balancesManager)
		{
			pair.EnsureValid();
			if (!redisKVHelper.valid)
			{
				throw new NullReferenceException("Invalid redisKVHelper");
			}

			Task<RedisValue> readpri = redisKVHelper.Get(pair.hash + "_KSP_PRI");
			Task<RedisValue> readsec = redisKVHelper.Get(pair.hash + "_KSP_SEC");
			Task<RedisValue> readsupply = redisKVHelper.Get(pair.hash + "_KSP_SUPPLY");
			if (!balancesManager.valid)
			{
				throw new NullReferenceException("Invalid balancesManager");
			}
			KellySwapPool kellySwapPool = new KellySwapPool(pair, redisKVHelper, balancesManager, await readpri, await readsec, await readsupply);

			return kellySwapPool;
		}
		public async Task Flush()
		{
			await asyncReaderWriterLock.AcquireWriterLock();
			try
			{
				Task tsk1 = redisKVHelper.Set(pair.hash + "_KSP_PRI", PrimaryReserve.ToString());
				Task tsk2 = redisKVHelper.Set(pair.hash + "_KSP_SEC", SecondaryReserve.ToString());
				await redisKVHelper.Set(pair.hash + "_KSP_SUPPLY", TotalSupply.ToString());
				await tsk2;
				await tsk1;
			}
			finally
			{
				asyncReaderWriterLock.ReleaseWriterLock();
			}
		}

		private KellySwapPool(Pair pair, RedisKVHelper redisKVHelper, BalancesManager balancesManager, string primaryReserve, string secondaryReserve, string totalSupply)
		{
			this.pair = pair;
			this.redisKVHelper = redisKVHelper;
			this.balancesManager = balancesManager;
			PrimaryReserve = BigInteger.Parse(primaryReserve, NumberStyles.None);
			SecondaryReserve = BigInteger.Parse(secondaryReserve, NumberStyles.None);
			TotalSupply = BigInteger.Parse(totalSupply, NumberStyles.None);
		}

		/// <summary>
		/// Adds liquidity to the KellySwap pool
		/// </summary>
		public async Task AddLiquidity(BigInteger PrimaryIn, BigInteger SecondaryIn, BigInteger MinimumPrimaryIn, BigInteger MinimumSecondaryIn, string to)
		{
			if (PrimaryIn.Sign < 1 || SecondaryIn.Sign < 1)
			{
				throw new InvalidOperationException("Attempted to add a zero or negative amount of liquidity to KellySwap pool");
			}
			if (PrimaryIn < MinimumPrimaryIn || SecondaryIn < MinimumSecondaryIn)
			{
				UserError.Throw("Minimum liquidity exceeds maximum liquidity", 1);
			}
			await asyncReaderWriterLock.AcquireWriterLock();
			try
			{
				if (PrimaryReserve.IsZero && SecondaryReserve.IsZero)
				{
					Mint2(PrimaryIn, SecondaryIn, to);
				}
				else
				{
					BigInteger SecondaryAmountOptimal = PrimaryIn * SecondaryReserve / PrimaryReserve;
					if (SecondaryAmountOptimal <= SecondaryIn)
					{
						if (SecondaryAmountOptimal < MinimumSecondaryIn)
						{
							UserError.Throw("Liquidity not added due to slippage", 2);
						}
						Mint2(PrimaryIn, SecondaryAmountOptimal, to);
						balancesManager.CreditOrDebit(to + '_' + pair.sec, SecondaryIn - SecondaryAmountOptimal);
					}
					else
					{
						BigInteger PrimaryAmountOptimal = SecondaryIn * PrimaryReserve / SecondaryReserve;
						if (PrimaryAmountOptimal > PrimaryIn)
						{
							throw new InvalidOperationException("Optimal primary amount exceeds primary input");
						}
						if (PrimaryAmountOptimal < MinimumPrimaryIn)
						{
							UserError.Throw("Liquidity not added due to slippage", 3);
						}
						balancesManager.CreditOrDebit(to + '_' + pair.pri, PrimaryIn - PrimaryAmountOptimal);
						Mint2(PrimaryAmountOptimal, SecondaryIn, to);
					}
				}
			}
			finally
			{
				asyncReaderWriterLock.ReleaseWriterLock();
			}
		}

		private void Mint2(BigInteger PrimaryIn, BigInteger SecondaryIn, string to)
		{
			if (PrimaryIn.Sign < 1 || SecondaryIn.Sign < 1)
			{
				throw new InvalidOperationException("Attempted to add a zero or negative amount of liquidity to KellySwap pool");
			}
			BigInteger liquidity;
			if (TotalSupply.IsZero)
			{
				liquidity = StaticUtils.Sqrt(PrimaryIn * SecondaryIn);
				TotalSupply = liquidity;
				liquidity -= 1000;

			}
			else
			{
				liquidity = BigInteger.Min(PrimaryIn * TotalSupply / PrimaryReserve, SecondaryIn * TotalSupply / SecondaryReserve);
			}
			if (liquidity.Sign < 1)
			{
				UserError.Throw("Insufficent liquidity tokens minted", 4);
			}
			balancesManager.CreditOrDebit(to + "_LP_" + pair.hash, liquidity);
			PrimaryReserve += PrimaryIn;
			SecondaryReserve += SecondaryIn;
		}
		/// <summary>
		/// Adds liquidity to the KellySwap pool
		/// </summary>
		public async Task Mint(BigInteger PrimaryIn, BigInteger SecondaryIn, string to)
		{
			if (PrimaryIn.Sign < 1 || SecondaryIn.Sign < 1)
			{
				throw new InvalidOperationException("Attempted to add a zero or negative amount of liquidity to KellySwap pool");
			}
			balancesManager.CreditOrDebit(to + '_' + pair.pri, BigInteger.Negate(PrimaryIn));
			balancesManager.CreditOrDebit(to + '_' + pair.sec, BigInteger.Negate(SecondaryIn));
			await asyncReaderWriterLock.AcquireWriterLock();
			try
			{
				Mint2(PrimaryIn, SecondaryIn, to);
			}
			finally
			{
				asyncReaderWriterLock.ReleaseWriterLock();
			}
		}

		public async Task Swap(BigInteger input, BigInteger minOutput, string to, bool buy)
		{
			await asyncReaderWriterLock.AcquireWriterLock();
			switch (input.Sign)
			{
				case 1:
					BigInteger amountInWithFee = input * 997;
					BigInteger inputReserve;
					BigInteger outputReserve;
					if (buy)
					{
						inputReserve = PrimaryReserve;
						outputReserve = SecondaryReserve;
						to += '_' + pair.sec;
					}
					else
					{
						inputReserve = SecondaryReserve;
						outputReserve = PrimaryReserve;
						to += '_' + pair.pri;
					}
					BigInteger output = amountInWithFee * outputReserve / (inputReserve * 1000 + amountInWithFee);
					if (output < minOutput)
					{
						UserError.Throw("Swap not executed due to slippage", 5);
					}
					inputReserve += input;
					outputReserve -= output;
					if (buy)
					{
						PrimaryReserve = inputReserve;
						SecondaryReserve = outputReserve;
					}
					else
					{
						PrimaryReserve = outputReserve;
						SecondaryReserve = inputReserve;
					}
					balancesManager.CreditOrDebit(to, output);
					break;
				case 0:
					if (minOutput.Sign == 1)
					{
						UserError.Throw("Swap not executed due to slippage", 6);
					}
					break;
				case -1:
					throw new InvalidOperationException("Attempted to add a zero or negative amount of liquidity to KellySwap pool");
			}
		}
	}
}
