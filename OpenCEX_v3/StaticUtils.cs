using System;
using System.Collections.Generic;
using System.Data;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Threading;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Reflection;
using StackExchange.Redis;
using Newtonsoft.Json;
using System.Globalization;
using System.Linq;

using Newtonsoft.Json.Linq;
using System.Net.WebSockets;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Digests;
using Nethereum.Signer;

namespace OpenCEX
{
	/// <summary>
	/// All the thread-private shit
	/// </summary>
	public sealed class ThreadStaticContext{
		public readonly HttpClient httpClient = new HttpClient();
		public readonly RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create();
		public ThreadStaticContext(){
			GC.SuppressFinalize(httpClient);
			GC.SuppressFinalize(randomNumberGenerator);
		}

		~ThreadStaticContext(){
			httpClient.Dispose();
			randomNumberGenerator.Dispose();
		}
	}
	public static partial class StaticUtils
	{


		[JsonObject(MemberSerialization.Fields)]
		private sealed class JsonOrder {
			public string price;
			public string amount;
			public string balance;
			public string owner;
		}

		public static readonly BigInteger ether = new BigInteger(1000000000000000000);

		private struct DualString {
			public readonly string key;
			public readonly string value;
			public DualString(string key, string value)
			{
				this.key = key;
				this.value = value;
			}
		}

		/// <summary>
		/// Stolen from Uniswap v2
		/// </summary>
		public static BigInteger Sqrt(BigInteger y)
		{
			if (y.Sign < 0) {
				throw new DivideByZeroException("Attempted to calculate the square root of negative number");
			}
			if (y > 3)
			{
				BigInteger z = y;
				BigInteger x = y / 2 + 1;
				while (x < z)
				{
					z = x;
					x = (y / x + x) / 2;
				}
				return z;
			}
			else {
				return y.IsZero ? BigInteger.Zero : BigInteger.One;
			}
		}

		/// <summary>
		/// Checks that the given spot symbol is legal, doesn't apply to KellySwap LPs.
		/// </summary>
		public static void ChkLegalSymbol(string symbol) {
			if (symbol is null) {
				throw new NullReferenceException("Symbol cannot be null");
			}
			if (symbol.Contains('_'))
			{
				UserError.Throw("Symbols must not contain underscores", 7);
			}
			if (symbol.Contains('-'))
			{
				UserError.Throw("Symbols must not contain dashes", 8);
			}
			if (symbol.Contains('/'))
			{
				UserError.Throw("Symbols must not contain slashes", 9);
			}
		}
		private static JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings();
		static StaticUtils() {
			Console.WriteLine("OpenCEX v3.0: The open-source cryptocurrency exchange");
			Console.WriteLine("Made by Jessie Lesbian <jessielesbian@protonmail.com> https://www.reddit.com/u/jessielesbian");
			Console.WriteLine();

			jsonSerializerSettings.MissingMemberHandling = MissingMemberHandling.Error;

			if (Environment.GetEnvironmentVariable("OpenCEX_RunningOnHeroku") is null)
			{
				listen = Environment.GetEnvironmentVariable("OpenCEX_Endpoint");
			}
			else
			{
				listen = "http://*:" + Environment.GetEnvironmentVariable("PORT") + "/";
			}
			Console.WriteLine("Connecting to Redis cluster...");
			ConnectionMultiplexer multiplexer = ConnectionMultiplexer.Connect(Environment.GetEnvironmentVariable("OpenCEX_RedisEndpoint"));

			redisServer = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);
			redis = multiplexer.GetDatabase(0);
			lock (requestMethodsLocker)
			{
				requestMethods.Add("doNothing", DoNothing2);
				requestMethods.Add("getCaptchaSiteKey", GetCaptchaSiteKey);
				requestMethods.Add("createAccount", CreateAccount);
				requestMethods.Add("restoreSession", RestoreSession);
			}
		}
		private static async Task<object> RestoreSession(object[] parameters, ulong _, WebSocketHelper wshelper){
			if (parameters.Length != 1)
			{
				UserError.Throw("Invalid number of arguments", 13);
			}
			if(wshelper is null){
				UserError.Throw("Websockets only", 19);
			}
			if(parameters[0] is string token){
				wshelper.userid = await VerifySessionToken(token);
				return true;
			} else{
				UserError.Throw("Non-string arguments not accepted", 15);
				return null;
			}
		}
		private static async Task<object> CreateAccount(object[] parameters, ulong _, WebSocketHelper wshelper)
		{
			if(parameters.Length != 3){
				UserError.Throw("Invalid number of arguments", 13);
			}

			if(parameters[0] is string && parameters[1] is string && parameters[2] is string){
				if(parameters[0] is null || parameters[1] is null || parameters[2] is null){
					UserError.Throw("Null arguments not accepted", 14);
				}
				Task chkcaptcha = VerifyCaptcha((string) parameters[0]);

				ITransaction transaction = redis.CreateTransaction();
				string username = (string)parameters[1];
				string accpasshashkey = "AP" + username;
				string accuseridkey = "AU" + username;

				//We use Redis conditional commit to eliminate the expensive read needed to check username availability
				transaction.AddCondition(Condition.KeyNotExists(accpasshashkey));
				transaction.AddCondition(Condition.KeyNotExists(accuseridkey));
				char[] pass = ((string)parameters[2]).ToCharArray();
				if(pass.Length > 36){
					UserError.Throw("Password length exceeds 72 bytes", 17);
				}
				byte[] salt = new byte[16];
				ThreadStaticContext.randomNumberGenerator.GetBytes(salt, 0, 16);
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
				transaction.StringSetAsync(accpasshashkey, OpenBsdBCrypt.Generate(pass, salt, 14));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
				transaction.AddCondition(Condition.KeyNotExists(accuseridkey));
				long userid = await redis.StringIncrementAsync("OpenCEX_UserID-CTR", 1L) + 1;
				if(userid < 1){
					throw new InvalidOperationException("Account number limit exceeded");
				}
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
				transaction.StringSetAsync(accuseridkey, userid);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
				await chkcaptcha; //Late (optimistic) captcha checking, since we can still be reverted here
				if(!await transaction.ExecuteAsync()){
					UserError.Throw("Account opening failed", 16);
				}
				ulong userid2 = (ulong)userid;
				if (wshelper is { }){
					wshelper.userid = userid2;
					Interlocked.MemoryBarrier();
				}
				return await GenerateSession(userid2);
			} else{
				UserError.Throw("Non-string arguments not accepted", 15);
				return null;
			}
			

			
		}
		public static readonly TimeSpan month = TimeSpan.FromDays(30);
		public static async Task<string> GenerateSession(ulong userid){
			byte[] token = new byte[64];
			ThreadStaticContext.randomNumberGenerator.GetBytes(token, 0, 64);
			byte[] hash = new byte[64];
			Sha3Digest sha3Digest = new Sha3Digest(512);
			sha3Digest.BlockUpdate(token, 0, 64);
			sha3Digest.DoFinal(hash, 0);
			if(await redis.StringSetAsync('S' + Convert.ToBase64String(hash, 0, 64), userid.ToString(), month)){
				return Convert.ToBase64String(token, 0, 64);
			} else{
				throw new IOException("Failed to create session");
			}
		}

		public static async Task<ulong> VerifySessionToken(string token){
			byte[] bytes;
			try{
				bytes = Convert.FromBase64String(token);
			} catch{
				UserError.Throw("Invalid session token", 18);
				return 0;
			}
			Sha3Digest sha3Digest = new Sha3Digest(512);
			sha3Digest.BlockUpdate(bytes, 0, bytes.Length);
			bytes = new byte[64];
			sha3Digest.DoFinal(bytes, 0);
			RedisValue redisValue = await redis.StringGetAsync('S' + Convert.ToBase64String(bytes, 0, 64));
			if(redisValue.IsNullOrEmpty){
				UserError.Throw("Invalid session token", 18);
			}
			return Convert.ToUInt64(redisValue);
		}
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
		private static async Task<object> DoNothing2(object[] @params, ulong userid, WebSocketHelper wshelper)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
		{
			return @params;
		}
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
		private static async Task<object> GetCaptchaSiteKey(object[] @params, ulong userid, WebSocketHelper wshelper)
		{
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
			return RecaptchaSiteKey;
		}

		private static readonly IServer redisServer;
		/// <summary>
		/// DANGEROUS: Only call from test
		/// </summary>
		public static Task WipeDatabase() {
			return redisServer.FlushDatabaseAsync(0);
		}
		//private static readonly EthECKey exchangeWallet = new EthECKey(Environment.GetEnvironmentVariable("OpenCEX_ExchangeWalletKey") ?? throw new InvalidOperationException("Missing exchange wallet key"));
		private static readonly KeyValuePair<string, string> ReCaptchaSecretKey = new KeyValuePair<string, string>("secret", Environment.GetEnvironmentVariable("OpenCEX_ReCaptchaSecretKey") ?? throw new InvalidOperationException("Missing ReCaptcha secret key"));
		private static readonly object requestMethodsLocker = new object();
		private static readonly Dictionary<string, Func<object[], ulong, WebSocketHelper, Task<object>>> requestMethods = new Dictionary<string, Func<object[], ulong, WebSocketHelper, Task<object>>>();

		/// <summary>
		/// Dynamically define a new request method
		/// </summary>
		public static void RegisterRequestMethod(string name, Func<object[], ulong, WebSocketHelper, Task<object>> method)
		{
			lock (requestMethodsLocker) {
				requestMethods.Add(name, method);
			}
		}

		public static bool Running { get; private set; } = true;

		/// <summary>
		/// Inhibits abort during a critical section
		/// </summary>
		public static Task InhibitAbort() => abortInhibition.AcquireReaderLock();

		/// <summary>
		/// Permits abort to occour
		/// </summary>
		public static void AllowAbort() => abortInhibition.ReleaseReaderLock();

		public static IDatabase redis;

		//Optimistic caching for use by Redis helpers
		internal static readonly LruCache<RedisKey, RedisValue> OptimisticRedisCache = new LruCache<RedisKey, RedisValue>();

		/// <summary>
		/// Optimistic Redis Caching: flush the L1 cache to the L2 cache
		/// </summary>
		internal static async Task UpdateOptimisticRedisCache(Task<bool> tsk, bool write, KeyValuePair<RedisKey, RedisValue>[] queue)
		{
			if(await tsk){
				//Committed successfully
				if(write){
					foreach (KeyValuePair<RedisKey, RedisValue> item in queue)
					{
						await OptimisticRedisCache.Set(item.Key, item.Value);
					}
				}
			} else{
				//Failed to commit
				foreach (KeyValuePair<RedisKey, RedisValue> item in queue)
				{
					try{
						await OptimisticRedisCache.Get(item.Key, true);
					} catch(CacheMissException){
						
					}
				}
			}
		}
		
		private static readonly AsyncReaderWriterLock abortInhibition = AsyncReaderWriterLock.Create();

		/// <summary>
		/// Obtains an optimistic lock
		/// </summary>
		public static async Task AcquireOptimisticLock(this ITransaction transaction, RedisKey redisKey){
			transaction.AddCondition(Condition.StringEqual(redisKey, await transaction.StringIncrementAsync(redisKey)));
		}
		[JsonObject(MemberSerialization.Fields)]
		private sealed class JsonRpcError{
			public readonly int code;
			public readonly string message;

			public JsonRpcError(int code, string message)
			{
				this.code = code;
				this.message = message;
			}
		}

		[JsonObject(MemberSerialization.Fields)]
		private class JsonRpcResponse{
			public readonly string jsonrpc = "2.0";
			public readonly object id;
			public JsonRpcResponse(object id){
				this.id = id;
			}
		}

		[JsonObject(MemberSerialization.Fields)]
		private sealed class JsonRpcSuccessResponse : JsonRpcResponse
		{
			public readonly object result;
			public JsonRpcSuccessResponse(object id, object result) : base(id)
			{
				this.result = result;
			}
		}
		[JsonObject(MemberSerialization.Fields)]
		private sealed class JsonRpcErrorResponse : JsonRpcResponse
		{
			public readonly JsonRpcError error;
			public JsonRpcErrorResponse(object id, JsonRpcError error) : base(id)
			{
				this.error = error;
			}
		}
		private static event EventHandler<WebSocketNotification> webSocketEventsHandler;
		public static void RaiseWebsocketNotification(WebSocketNotification webSocketNotification)
		{
			webSocketEventsHandler?.Invoke(null, webSocketNotification);
		}
		public static void RegisterWebsocketNotificationListener(EventHandler<WebSocketNotification> eventHandler)
		{
			webSocketEventsHandler += eventHandler;
		}
		public static void DeRegisterWebsocketNotificationListener(EventHandler<WebSocketNotification> eventHandler)
		{
			webSocketEventsHandler -= eventHandler;
		}

		private sealed class JsonRpcRequest{
			public string jsonrpc;
			public object id; //NOTE: a null id field shall be interpreted as a notification
			public object[] @params;
			public string method;
		}

		private static async Task<object> HandleJsonRequestImpl(JsonRpcRequest jsonRpcRequest, ulong userid, WebSocketHelper webSocket)
		{
			if (jsonRpcRequest.jsonrpc != "2.0")
			{
				UserError.Throw("Invalid Request", -32600);
			}
			if (jsonRpcRequest.@params is null)
			{
				UserError.Throw("Invalid Request", -32600);
			}
			if (jsonRpcRequest.method is null)
			{
				UserError.Throw("Invalid Request", -32600);
			}
			if (requestMethods.TryGetValue(jsonRpcRequest.method, out Func<object[], ulong, WebSocketHelper, Task<object>> meth))
			{
			start:
				object ret;
				try{
					ret = await meth(jsonRpcRequest.@params, userid, webSocket);
				} catch(OptimisticRepeatException e){
					//OPTIMISTIC LOCKING AND CACHING: Retry transaction if we run into OptimisticRepeatExceptions
					await e.WaitCleanUp();
					goto start;
				}
				return ret;
			}
			else
			{
				UserError.Throw("Method not found", -32601);
				throw new Exception("User error not thrown (should not reach here)");
			}

		}
		private static readonly string RecaptchaSiteKey = Environment.GetEnvironmentVariable("OpenCEX_ReCaptchaSiteKey") ?? throw new InvalidOperationException("Missing ReCaptcha site key");
		[JsonObject(MemberSerialization.Fields)]
		private sealed class ReCaptchaResponse{
			public bool success;
		}

		public static async Task VerifyCaptcha(string response){
			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://www.google.com/recaptcha/api/siteverify");
			req.Content = new FormUrlEncodedContent(new KeyValuePair<string, string>[] { ReCaptchaSecretKey, new KeyValuePair<string, string>("response", response)});
			IDictionary<string, object> props = req.Properties;
			props.Add("secret", ReCaptchaSecretKey);
			props.Add("response", response);

			string res = await (await SafeSend(req)).Content.ReadAsStringAsync();
			Console.WriteLine(res);
			if (!JsonConvert.DeserializeObject<ReCaptchaResponse>(res).success){
				UserError.Throw("Invalid captcha", 12);
			}
		}

		private static readonly JsonRpcError internalServerError = new JsonRpcError(-32603, "Internal error");
		public static async void WaitAndDisposeSemaphore(SemaphoreSlim semaphore){
			try{
				await semaphore.WaitAsync();
			} finally{
				semaphore.Dispose();
			}
		}

		public static async Task<string> HandleJsonRequest(string json, ulong userid, WebSocketHelper webSocket){
			if(json is null){
				return "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": -32600, \"message\": \"Invalid Request\"}}";
			}
			if(json.Length > 65536){
				return "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": 1, \"message\": \"Excessive payload size\"}}";
			}
			JsonReader jsonReader = null;
			bool batched;
			try
			{
				jsonReader = new JsonTextReader(new StringReader(json));
				if (!jsonReader.Read())
				{
					return "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": -32600, \"message\": \"Invalid Request\"}}";
				}
				batched = jsonReader.TokenType.HasFlag(JsonToken.StartArray);

			} catch{
				return "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": -32700, \"message\": \"Parse error\"}}";
			} finally{
				jsonReader?.Close();
			}



			if (batched)
			{
				//Batched request
				JsonRpcRequest[] jsonRpcRequests;
				try
				{
					jsonRpcRequests = JsonConvert.DeserializeObject<JsonRpcRequest[]>(json);
				}
				catch
				{
					return "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": -32700, \"message\": \"Parse error\"}}";
				}
				int limit = jsonRpcRequests.Length;
				Task<object>[] tasks = new Task<object>[limit];
				for (int i = 0; i < limit; ++i)
				{
					tasks[i] = HandleJsonRequestImpl(jsonRpcRequests[i], userid, webSocket);
				}
				Queue<JsonRpcResponse> jsonRpcResponses = new Queue<JsonRpcResponse>();
				for (int i = 0; i < limit; ++i)
				{

					object id = jsonRpcRequests[i].id;
					JsonRpcResponse response;
					try
					{
						response = new JsonRpcSuccessResponse(id, await tasks[i]);
					}
					catch (Exception e)
					{
						if (e is UserError ue)
						{
							response = new JsonRpcErrorResponse(id, new JsonRpcError(ue.code, ue.Message));
						}
						else
						{
							Console.Error.WriteLine("Unexpected internal server error: {0}", e);
							response = new JsonRpcErrorResponse(id, internalServerError);
						}
					}
					if (id is { })
					{
						jsonRpcResponses.Enqueue(response);
					}
				}

				try
				{
					if (jsonRpcResponses.Count == 0)
					{
						return null;
					}
					else
					{
						return JsonConvert.SerializeObject(jsonRpcResponses.ToArray());
					}
				}
				catch (Exception e)
				{
					Console.Error.WriteLine("Unexpected internal server error: {0}", e);
					return "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": -32603, \"message\": \"Internal error\"}}";
				}
			}
			else
			{
				//Traditional request
				JsonRpcRequest jsonRpcRequest;
				try
				{
					jsonRpcRequest = JsonConvert.DeserializeObject<JsonRpcRequest>(json);
				}
				catch
				{
					return "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": -32700, \"message\": \"Parse error\"}}";
				}
				try
				{
					string res = JsonConvert.SerializeObject(new JsonRpcSuccessResponse(jsonRpcRequest.id, await HandleJsonRequestImpl(jsonRpcRequest, userid, webSocket)));
					return jsonRpcRequest.id is null ? null : res;
				}
				catch (Exception e)
				{
					if (e is UserError ue)
					{
						JsonRpcError jsonRpcError = new JsonRpcError(ue.code, ue.Message);
						if(jsonRpcRequest.id is { }){
							try
							{
								return JsonConvert.SerializeObject(new JsonRpcErrorResponse(jsonRpcRequest.id, jsonRpcError));
							}
							catch (Exception x)
							{
								Console.Error.WriteLine("Unexpected internal server error: {0}", x);
								return "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": -32603, \"message\": \"Internal error\"}}";
							}
						} else{
							return null;
						}
					}
					else
					{
						Console.Error.WriteLine("Unexpected internal server error: {0}", e);
						return jsonRpcRequest.id is null ? null : JsonConvert.SerializeObject(new JsonRpcErrorResponse(jsonRpcRequest.id, internalServerError));
					}
				}

			}

		}
		private static async void HandleRequest(HttpListenerContext httpListenerContext){
			try{
				HttpListenerRequest httpListenerRequest = httpListenerContext.Request;
				
				if (httpListenerRequest.IsWebSocketRequest)
				{
					HttpListenerWebSocketContext ctx = null;
					try{
						ctx = await httpListenerContext.AcceptWebSocketAsync("OpenCEX-v3");
					} catch (Exception e){
						await ctx.WebSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Connection closed due to internal server error", default);
						Console.Error.WriteLine("Unexpected internal server error: {0}", e);
						return;
					}
					
					WebSocketHelper wshelper = new WebSocketHelper(ctx.WebSocket);
					wshelper.RegisterWebSocketReceiver(WebSocketHelper_OnWebSocketReceive);
					wshelper.doread = true;

					//From now on the WebSocket is the responsibility of the delegate
				}
				else
				{
					HttpListenerResponse httpListenerResponse = httpListenerContext.Response;
					try
					{
						if (httpListenerRequest.HttpMethod != "POST")
						{
							httpListenerResponse.StatusCode = 400;
						}
						else
						{
							Stream str = httpListenerRequest.InputStream;
							string temp;
							Encoding encoding;
							using (StreamReader streamReader = new StreamReader(str, Encoding.UTF8, true, -1, true))
							{
								encoding = streamReader.CurrentEncoding;
								temp = await streamReader.ReadToEndAsync();
							}
							temp = await HandleJsonRequest(temp, 0, null);
							if (temp is { })
							{
								await httpListenerResponse.OutputStream.WriteAsync(encoding.GetBytes(temp));
							}
						}
					}
					finally
					{
						httpListenerResponse?.Close();
					}
				}
			} catch (Exception e){
				Console.Error.WriteLine("Unexpected internal server error: {0}", e);
			}
			
		}

		private static async void WebSocketHelper_OnWebSocketReceive(object sender, WebSocketReceiveEvent e)
		{
			WebSocketHelper webSocketHelper = (WebSocketHelper)sender;
			string returns = await HandleJsonRequest(e.data, webSocketHelper.userid, webSocketHelper);
			if(returns is { }){
				await webSocketHelper.Send(Encoding.UTF8.GetBytes(returns));
			}
		}

		private static async void RequestHandler(HttpListener httpListener)
		{
			while (httpListener.IsListening)
			{
				try{
					HttpListenerContext ctx = await httpListener.GetContextAsync();
					if (ctx is { })
					{
						HandleRequest(ctx);
					}
				} catch (Exception e){
					//Can't exit, since this would cause DoS vulnerabilities
					Console.Error.WriteLine("Unexpected internal server error: {0}", e);
				}
			}
		}

		public static readonly Type[] notypes = new Type[0];
		public static readonly object[] noObjs = new object[0];
		private static void Main()
		{
			Console.WriteLine("Initializing HTTP listener...");
			using HttpListener httpListener = new HttpListener();
			httpListener.Prefixes.Add(listen);
			

			Console.WriteLine("Starting HTTP Listener...");
			httpListener.Start();
			Console.WriteLine("Binding abort listeners...");
			int abortflag = 0;
			AppDomain.CurrentDomain.ProcessExit += (object sender, EventArgs e) => {
				if (Interlocked.Exchange(ref abortflag, 1) == 0)
				{
					Console.WriteLine("Termination signal received, stopping http listener...");
					httpListener.Stop();

					Console.WriteLine("Signalling other threads abort...");
					Running = false;

					Console.WriteLine("Waiting for critical sections to complete...");
					abortInhibition.AcquireWriterLock().Wait();
				}
			};

			Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) =>
			{
				if (Interlocked.Exchange(ref abortflag, 1) == 0)
				{
					Console.WriteLine("CTRL-C received, stopping http listener...");
					httpListener.Stop();

					Console.WriteLine("Signalling other threads abort...");
					Running = false;

					Console.WriteLine("Waiting for critical sections to complete...");
					abortInhibition.AcquireWriterLock().Wait();
				}
			};

			Console.WriteLine("Loading plugins...");
			Type pluginEntryType = typeof(IPluginEntry);
			
			foreach(string str in Directory.GetFiles(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "*.dll", SearchOption.TopDirectoryOnly)){
				Console.WriteLine("Loading assembly " + str + "...");
				foreach(Type type in Assembly.LoadFrom(str).GetTypes()){
					foreach(Type inte in type.GetInterfaces()){
						if(inte.IsEquivalentTo(pluginEntryType)){
							goto noskip;
						}
					}
					continue;
					noskip:
					if (type.IsSealed)
					{
						Console.WriteLine("Initializing plugin class " + type.FullName + "...");
						((IPluginEntry)type.GetConstructor(notypes).Invoke(noObjs)).Init();
					}
					else
					{
						Console.WriteLine("Plugin class " + type.Name + " not initialized since it's not sealed");
					}
				}
			}
			

			Console.WriteLine("Starting request handlers...");
			for (int i = 0; ++i < 5;)
			{
				RequestHandler(httpListener);
			}

			Console.WriteLine("DONE!");
			Thread.Sleep(int.MaxValue);

			
		}
		public static readonly string listen;
		[ThreadStatic] private static ThreadStaticContext threadStaticContext;
		public static ThreadStaticContext ThreadStaticContext { 
			get{
				if(threadStaticContext is null){
					threadStaticContext = new ThreadStaticContext();
				}
				return threadStaticContext;
			}
		}

		public static async Task<HttpResponseMessage> SafeSend(HttpRequestMessage req)
		{
			HttpResponseMessage httpResponseMessage = await ThreadStaticContext.httpClient.SendAsync(req);
			httpResponseMessage.EnsureSuccessStatusCode();
			return httpResponseMessage;
		}


#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
		public static async Task DoNothingAsync(){
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously

		}
		public static readonly Task DoNothing = DoNothingAsync();
	}
}
