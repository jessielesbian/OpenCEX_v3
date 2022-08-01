using Newtonsoft.Json;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCEX
{
	public sealed class TestPlugin : IPluginEntry
	{
		private static void Main()
		{
			
		}
		
		[JsonObject(MemberSerialization.Fields)]
		private sealed class ReCaptchaResponse
		{
			public bool success;
		}
		public void Init()
		{
			Console.WriteLine("Loading OpenCEX v3.0 authentication plugin...");
			{
				Func<ulong, Task<string>> GenerateSession;
				{
					TimeSpan month = TimeSpan.FromDays(30);
					GenerateSession = async (ulong userid) => {
						byte[] token = new byte[64];
						StaticUtils.ThreadStaticContext.randomNumberGenerator.GetBytes(token, 0, 64);
						byte[] hash = new byte[64];
						Sha3Digest sha3Digest = new Sha3Digest(512);
						sha3Digest.BlockUpdate(token, 0, 64);
						sha3Digest.DoFinal(hash, 0);
						if (await StaticUtils.redis.StringSetAsync('S' + Convert.ToBase64String(hash, 0, 64), userid.ToString(), month))
						{
							return Convert.ToBase64String(token, 0, 64);
						}
						else
						{
							throw new IOException("Failed to create session");
						}
					};
				}
				Func<string, Task> VerifyCaptcha;
				{
					KeyValuePair<string, string> ReCaptchaSecretKey = new KeyValuePair<string, string>("secret", Environment.GetEnvironmentVariable("OpenCEX_ReCaptchaSecretKey") ?? throw new InvalidOperationException("Missing ReCaptcha secret key"));
					VerifyCaptcha = async (string response) =>
					{
						HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://www.google.com/recaptcha/api/siteverify");
						req.Content = new FormUrlEncodedContent(new KeyValuePair<string, string>[] { ReCaptchaSecretKey, new KeyValuePair<string, string>("response", response) });
						IDictionary<string, object> props = req.Properties;
						props.Add("secret", ReCaptchaSecretKey);
						props.Add("response", response);

						if (!JsonConvert.DeserializeObject<ReCaptchaResponse>(await (await StaticUtils.SafeSend(req)).Content.ReadAsStringAsync()).success)
						{
							UserError.Throw("Invalid captcha", 12);
						}
					};
				}
				{
					object RecaptchaSiteKey = Environment.GetEnvironmentVariable("OpenCEX_ReCaptchaSiteKey") ?? throw new InvalidOperationException("Missing ReCaptcha site key");
					StaticUtils.RegisterRequestMethod("getCaptchaSiteKey", (object[] a, ulong b, WebSocketHelper c) =>
					{
						return Task.FromResult(RecaptchaSiteKey);
					});
				}
				StaticUtils.RegisterRequestMethod("login", async (object[] parameters, ulong _, WebSocketHelper wshelper) => {
					if (parameters.Length != 3)
					{
						UserError.Throw("Invalid number of arguments", 13);
					}

					if (parameters[0] is string && parameters[1] is string && parameters[2] is string)
					{
						if (parameters[0] is null || parameters[1] is null || parameters[2] is null)
						{
							UserError.Throw("Null arguments not accepted", 14);
						}
						Task chkcaptcha = VerifyCaptcha((string)parameters[0]);
						ulong userid2;
						try
						{
							string username = (string)parameters[1];
							char[] pass = ((string)parameters[2]).ToCharArray();
							if (pass.Length > 36)
							{
								UserError.Throw("Password length exceeds 72 bytes", 17);
							}
							RedisValue[] redisValues = await StaticUtils.redis.StringGetAsync(new RedisKey[] { "AP" + username, "AU" + username });

							//NOTE: We check against a "magic hash" if the user doesn't exist to protect against timing attacks. If the magic hash is cracked we still have a second safety check down the road.
							if (!OpenBsdBCrypt.CheckPassword(redisValues[0].IsNullOrEmpty ? "$2a$14$SFfj6eJ12eU9yyKZf2aQ/e9516b4uIU8pyBcumAYFn13Hptxv2wGS" : (string) redisValues[0], pass))
							{
								UserError.Throw("Invalid credentials!", 20);
							}
							RedisValue uidstr = redisValues[1];
							if(uidstr.IsNullOrEmpty){
								UserError.Throw("Invalid credentials!", 20);
							}
							userid2 = Convert.ToUInt64(uidstr);

						}
						finally
						{
							await chkcaptcha;
						}
						if (wshelper is { })
						{
							wshelper.userid = userid2;
							Interlocked.MemoryBarrier();
						}
						return await GenerateSession(userid2);
					}
					else
					{
						UserError.Throw("Non-string arguments not accepted", 15);
						return null;
					}
				});

				StaticUtils.RegisterRequestMethod("createAccount", async (object[] parameters, ulong _, WebSocketHelper wshelper) => {
					if (parameters.Length != 3)
					{
						UserError.Throw("Invalid number of arguments", 13);
					}

					if (parameters[0] is string && parameters[1] is string && parameters[2] is string)
					{
						if (parameters[0] is null || parameters[1] is null || parameters[2] is null)
						{
							UserError.Throw("Null arguments not accepted", 14);
						}
						Task chkcaptcha = VerifyCaptcha((string)parameters[0]);
						ITransaction transaction = StaticUtils.redis.CreateTransaction();
						long userid;
						try
						{
							string username = (string)parameters[1];
							string accpasshashkey = "AP" + username;
							string accuseridkey = "AU" + username;

							//We use Redis conditional commit to eliminate the expensive read needed to check username availability
							transaction.AddCondition(Condition.KeyNotExists(accpasshashkey));
							transaction.AddCondition(Condition.KeyNotExists(accuseridkey));
							char[] pass = ((string)parameters[2]).ToCharArray();
							if (pass.Length > 36)
							{
								UserError.Throw("Password length exceeds 72 bytes", 17);
							}
							byte[] salt = new byte[16];
							StaticUtils.ThreadStaticContext.randomNumberGenerator.GetBytes(salt, 0, 16);
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
							transaction.StringSetAsync(accpasshashkey, OpenBsdBCrypt.Generate(pass, salt, 14));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
							transaction.AddCondition(Condition.KeyNotExists(accuseridkey));
							userid = await StaticUtils.redis.StringIncrementAsync("OpenCEX_UserID-CTR", 1L) + 1;
							if (userid < 1)
							{
								throw new InvalidOperationException("Account number limit exceeded");
							}
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
							transaction.StringSetAsync(accuseridkey, userid);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
						}
						finally
						{
							await chkcaptcha; //Late (optimistic) captcha checking, since we can still be reverted here
						}
						if (!await transaction.ExecuteAsync())
						{
							UserError.Throw("Account opening failed", 16);
						}
						ulong userid2 = (ulong)userid;
						if (wshelper is { })
						{
							wshelper.userid = userid2;
							Interlocked.MemoryBarrier();
						}
						return await GenerateSession(userid2);
					}
					else
					{
						UserError.Throw("Non-string arguments not accepted", 15);
						return null;
					}
				});
			}

			StaticUtils.RegisterRequestMethod("restoreSession", async (object[] parameters, ulong _, WebSocketHelper wshelper) => {
				if (parameters.Length != 1)
				{
					UserError.Throw("Invalid number of arguments", 13);
				}
				if (wshelper is null)
				{
					UserError.Throw("Websockets only", 19);
				}
				if (parameters[0] is string token)
				{
					byte[] bytes;
					try
					{
						bytes = Convert.FromBase64String(token);
					}
					catch
					{
						UserError.Throw("Invalid session token", 18);
						return 0;
					}
					Sha3Digest sha3Digest = new Sha3Digest(512);
					sha3Digest.BlockUpdate(bytes, 0, bytes.Length);
					bytes = new byte[64];
					sha3Digest.DoFinal(bytes, 0);
					RedisValue redisValue = await StaticUtils.redis.StringGetAsync('S' + Convert.ToBase64String(bytes, 0, 64));
					if (redisValue.IsNullOrEmpty)
					{
						UserError.Throw("Invalid session token", 18);
					}
					wshelper.userid = Convert.ToUInt64(redisValue);
					Interlocked.MemoryBarrier();
					return true;
				}
				else
				{
					UserError.Throw("Non-string arguments not accepted", 15);
					return null;
				}
			});
		}
	}
}
