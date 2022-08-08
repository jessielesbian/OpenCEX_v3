using System;
using System.Collections.Concurrent;
using System.Text;
using System.Net.WebSockets;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.Threading;
using System.IO;

namespace OpenCEX
{
	public sealed class WebSocketReceiveEvent : EventArgs{
		public readonly string data;

		public WebSocketReceiveEvent(string data)
		{
			this.data = data ?? throw new ArgumentNullException(nameof(data));
		}
	}
	public sealed class WebSocketHelper
	{
		private readonly WebSocket webSocket;
		private readonly ConcurrentDictionary<string, int> interestedIn = new ConcurrentDictionary<string, int>();
		private readonly SemaphoreSlim sendLocker = new SemaphoreSlim(1, 1);
		public ulong userid;
		public async Task Send(ArraySegment<byte> bytes) {
			await sendLocker.WaitAsync();
			try
			{
				//All sends are inhibited after closing
				if(nomoresend == 0){
					await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, default);
				}
			} finally {
				sendLocker.Release();
			}
		}
		private event EventHandler<WebSocketReceiveEvent> OnWebSocketReceive;
		public void RegisterWebSocketReceiver(EventHandler<WebSocketReceiveEvent> eventHandler)
		{
			OnWebSocketReceive += eventHandler;
		}

		public void DeRegisterWebSocketReceiver(EventHandler<WebSocketReceiveEvent> eventHandler)
		{
			OnWebSocketReceive -= eventHandler;
		}

		private async void HandleWebsocketNotification(object sender, WebSocketNotification webSocketNotification){
			if (interestedIn.ContainsKey(webSocketNotification.method))
			{
				try
				{	
					await Send(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(webSocketNotification)));
				} catch (Exception e){
					Console.Error.WriteLine("Unexpected internal server error: {0}", e);
				}
			}
		}

		public WebSocketHelper(WebSocket webSocket)
		{
			this.webSocket = webSocket;
			Watcher();
		}
		private volatile int nomoresend;
		public volatile bool doread;

		/// <summary>
		/// The watcher keeps an eye on the WebSocket, and dispatches messages to handlers
		/// </summary>
		private async void Watcher(){
			StaticUtils.RegisterWebsocketNotificationListener(HandleWebsocketNotification);
			try
			{
				byte[] buf = new byte[65536];
				while (StaticUtils.Running)
				{
					if(doread){
						await using (MemoryStream ms = new MemoryStream(65536))
						{
						read:
							WebSocketReceiveResult recv = await webSocket.ReceiveAsync(buf, default);
							if (recv.MessageType.HasFlag(WebSocketMessageType.Close))
							{
								nomoresend = 1;
								await sendLocker.WaitAsync();
								try
								{
									await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "The client requested that the server close the connection", default);
								}
								finally
								{
									sendLocker.Release();
								}
								return;
							}
							int len = recv.Count;
							await ms.WriteAsync(buf, 0, len);
							if (!recv.EndOfMessage)
							{
								ms.Capacity += len;
								goto read;
							}
							buf = ms.GetBuffer();
						}

						OnWebSocketReceive?.Invoke(this, new WebSocketReceiveEvent(Encoding.UTF8.GetString(buf)));
					} else{
						await Task.Delay(1);
					}
				}
			}
			catch (Exception e){
				Console.Error.WriteLine("Unexpected internal server error: {0}", e);
			}
			finally{
				StaticUtils.DeRegisterWebsocketNotificationListener(HandleWebsocketNotification);
				nomoresend = 1;
				await sendLocker.WaitAsync();
				try{
					webSocket.Dispose();
				} finally{
					sendLocker.Release();
				}
			}
		}
	}
	[JsonObject(MemberSerialization.OptIn)]
	public class WebSocketNotification : EventArgs{
		[JsonProperty] public readonly string jsonrpc = "2.0";
		[JsonProperty] public readonly string method;
		[JsonProperty] private readonly object[] @params;

		public WebSocketNotification(string method, object[] @params)
		{
			this.method = method ?? throw new ArgumentNullException(nameof(method));
			this.@params = @params ?? throw new ArgumentNullException(nameof(@params));
		}
	}
}
