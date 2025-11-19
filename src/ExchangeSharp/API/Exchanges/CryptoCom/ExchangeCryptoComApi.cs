using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExchangeSharp
{
	public sealed partial class ExchangeCryptoComApi : ExchangeAPI
	{
		public override string BaseUrl { get; set; } = "https://api.crypto.com/v2";
		public override string BaseUrlWebSocket { get; set; } = "wss://stream.crypto.com/v2";

		public ExchangeCryptoComApi()
		{
			NonceStyle = NonceStyle.UnixMilliseconds;
			NonceOffset = TimeSpan.FromSeconds(0.1);
			RequestContentType = "application/json";
			// WebSocketOrderBookType = not implemented
			MarketSymbolSeparator = "_";
			MarketSymbolIsUppercase = true;
			// ExchangeGlobalCurrencyReplacements[] not implemented
		}

		protected override async Task ProcessRequestAsync(
				IHttpWebRequest request,
				Dictionary<string, object> payload
		)
		{
			if (CanMakeAuthenticatedRequest(payload))
			{
				payload["api_key"] = PublicApiKey.ToUnsecureString();
				payload["sig"] = GetDigitalSignature(payload);
				string json = JsonConvert.SerializeObject(payload);
				request.Method = "POST";

				await request.WriteToRequestAsync(json);
			}
			await base.ProcessRequestAsync(request, payload);
		}

		private string GetDigitalSignature(Dictionary<string, object> payload)
		{
			string method = (string)payload["method"];
			long id = (long)payload["id"];
			long nonce = (long)payload["nonce"];
			Dictionary<string, object> parameters = payload["params"] as Dictionary<string, object>;
			string paramString = "{}";
			if (parameters != null)
			{
				paramString = string.Join("", parameters.Keys.OrderBy(key => key).Select(key => key + parameters[key]));
			}
			var sigPayload = method + id + PublicApiKey.ToUnsecureString() + paramString + nonce;
			using (var hash = new HMACSHA256(Encoding.UTF8.GetBytes(PrivateApiKey.ToUnsecureString() ?? "")))
			{
				var computedHash = hash.ComputeHash(Encoding.UTF8.GetBytes(sigPayload));
				// .NET Standard 2.0 does not have Convert.ToHexString, so use StringBuilder
				var sb = new StringBuilder(computedHash.Length * 2);
				foreach (var b in computedHash)
				{
					sb.AppendFormat("{0:x2}", b);
				}
				return sb.ToString().ToLowerInvariant();
			}
		}

		public override async Task<Dictionary<string, decimal>> GetAmountsAsync()
		{
				Dictionary<string, object> payload = new Dictionary<string, object>();
				payload.Add("id", (long)1);
				payload.Add("method", "private/user-balance");
				payload.Add("params", new { });
				payload.Add("api_key", null);
				payload.Add("nonce", await GenerateNonceAsync());
				payload.Add("sig", null);
				var result = await MakeJsonRequestAsync<JToken>("private/user-balance", null, payload, "POST");
				var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
				foreach (var account in result["accounts"] ?? Enumerable.Empty<JToken>())
				{
						foreach (var balance in account["balances"] ?? Enumerable.Empty<JToken>())
						{
								string currency = balance["currency"].ToStringInvariant();
								decimal amount = balance["total"].ConvertInvariant<decimal>();
								balances[currency] = amount;
						}
				}
				return balances;
		}

		protected override async Task<Dictionary<string, decimal>> OnGetAmountsAvailableToTradeAsync()
		{
				var result = await MakeJsonRequestAsync<JToken>("private/get-account-summary", null, await GetNoncePayloadAsync(), "POST");
				var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
				foreach (var account in result["accounts"] ?? Enumerable.Empty<JToken>())
				{
						foreach (var balance in account["balances"] ?? Enumerable.Empty<JToken>())
						{
								string currency = balance["currency"].ToStringInvariant();
								decimal available = balance["available"].ConvertInvariant<decimal>();
								balances[currency] = available;
						}
				}
				return balances;
		}

		protected override async Task<ExchangeOrderResult[]> OnPlaceOrdersAsync(params ExchangeOrderRequest[] orders)
		{
				var results = new List<ExchangeOrderResult>();
				foreach (var order in orders)
				{
						var payload = await GetNoncePayloadAsync();
						payload["instrument_name"] = order.MarketSymbol;
						payload["side"] = order.IsBuy ? "BUY" : "SELL";
						payload["type"] = order.OrderType == OrderType.Limit ? "LIMIT" : "MARKET";
						payload["quantity"] = order.Amount;
						if (order.OrderType == OrderType.Limit)
						{
								payload["price"] = order.Price;
						}
						var response = await MakeJsonRequestAsync<JToken>("private/create-order", null, payload, "POST");
						var orderResult = new ExchangeOrderResult
						{
								OrderId = response["order_id"].ToStringInvariant(),
								Result = ExchangeAPIOrderResult.PendingOpen
						};
						results.Add(orderResult);
				}
				return results.ToArray();
		}

		protected override async Task<ExchangeOrderResult> OnGetOrderDetailsAsync(string orderId, string? marketSymbol = null, bool isClientOrderId = false)
		{
				var payload = await GetNoncePayloadAsync();
				payload["order_id"] = orderId;
				var response = await MakeJsonRequestAsync<JToken>("private/get-order-detail", null, payload, "POST");
				var order = response["order"];
				var result = new ExchangeOrderResult
				{
						OrderId = order["order_id"].ToStringInvariant(),
						MarketSymbol = order["instrument_name"].ToStringInvariant(),
						Amount = order["quantity"].ConvertInvariant<decimal>(),
						AmountFilled = order["cumulative_quantity"].ConvertInvariant<decimal>(),
						Price = order["price"].ConvertInvariant<decimal>(),
						IsBuy = order["side"].ToStringInvariant().Equals("BUY", StringComparison.OrdinalIgnoreCase),
						OrderDate = CryptoUtility.UnixTimeStampToDateTimeMilliseconds(order["create_time"].ConvertInvariant<long>()),
						Result = order["status"].ToStringInvariant().Equals("FILLED", StringComparison.OrdinalIgnoreCase) ? ExchangeAPIOrderResult.Filled :
								order["status"].ToStringInvariant().Equals("CANCELED", StringComparison.OrdinalIgnoreCase) ? ExchangeAPIOrderResult.Canceled :
								ExchangeAPIOrderResult.Open
				};
				return result;
		}

		protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string? marketSymbol = null)
		{
				var payload = await GetNoncePayloadAsync();
				if (!string.IsNullOrWhiteSpace(marketSymbol))
				{
						payload["instrument_name"] = marketSymbol;
				}
				payload["order_status"] = "OPEN";
				var response = await MakeJsonRequestAsync<JToken>("private/get-open-orders", null, payload, "POST");
				var orders = new List<ExchangeOrderResult>();
				foreach (var order in response["order_list"] ?? Enumerable.Empty<JToken>())
				{
						orders.Add(new ExchangeOrderResult
						{
								OrderId = order["order_id"].ToStringInvariant(),
								MarketSymbol = order["instrument_name"].ToStringInvariant(),
								Amount = order["quantity"].ConvertInvariant<decimal>(),
								AmountFilled = order["cumulative_quantity"].ConvertInvariant<decimal>(),
								Price = order["price"].ConvertInvariant<decimal>(),
								IsBuy = order["side"].ToStringInvariant().Equals("BUY", StringComparison.OrdinalIgnoreCase),
								OrderDate = CryptoUtility.UnixTimeStampToDateTimeMilliseconds(order["create_time"].ConvertInvariant<long>()),
								Result = ExchangeAPIOrderResult.Open
						});
				}
				return orders;
		}

		protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetCompletedOrderDetailsAsync(string? marketSymbol = null, DateTime? afterDate = null)
		{
				var payload = await GetNoncePayloadAsync();
				if (!string.IsNullOrWhiteSpace(marketSymbol))
				{
						payload["instrument_name"] = marketSymbol;
				}
				payload["order_status"] = "FILLED";
				var response = await MakeJsonRequestAsync<JToken>("private/get-order-history", null, payload, "POST");
				var orders = new List<ExchangeOrderResult>();
				foreach (var order in response["order_list"] ?? Enumerable.Empty<JToken>())
				{
						var orderDate = CryptoUtility.UnixTimeStampToDateTimeMilliseconds(order["create_time"].ConvertInvariant<long>());
						if (afterDate == null || orderDate >= afterDate.Value)
						{
								orders.Add(new ExchangeOrderResult
								{
										OrderId = order["order_id"].ToStringInvariant(),
										MarketSymbol = order["instrument_name"].ToStringInvariant(),
										Amount = order["quantity"].ConvertInvariant<decimal>(),
										AmountFilled = order["cumulative_quantity"].ConvertInvariant<decimal>(),
										Price = order["price"].ConvertInvariant<decimal>(),
										IsBuy = order["side"].ToStringInvariant().Equals("BUY", StringComparison.OrdinalIgnoreCase),
										OrderDate = orderDate,
										Result = ExchangeAPIOrderResult.Filled
								});
						}
				}
				return orders;
		}

		protected override async Task<IEnumerable<string>> OnGetMarketSymbolsAsync()
		{
			var instruments = await MakeJsonRequestAsync<JToken>("public/get-instruments");
			var markets = new List<ExchangeMarket>();
			foreach (JToken instrument in instruments["instruments"])
			{
				markets.Add(
						new ExchangeMarket
						{
							MarketSymbol = instrument["instrument_name"].ToStringUpperInvariant(),
							QuoteCurrency = instrument["quote_currency"].ToStringInvariant(),
							BaseCurrency = instrument["base_currency"].ToStringInvariant(),
						}
				);
			}
			return markets.Select(m => m.MarketSymbol);
		}

		protected override async Task<IWebSocket> OnGetTradesWebSocketAsync(
				Func<KeyValuePair<string, ExchangeTrade>, Task> callback,
				params string[] marketSymbols
		)
		{
			if (marketSymbols == null || marketSymbols.Length == 0)
			{
				marketSymbols = new string[] { "" };
			}
			var ws = await ConnectPublicWebSocketAsync(
					"/market",
					async (_socket, msg) =>
					{
						/*{
						{{
							"code": 0,
							"method": "subscribe",
							"result": {
								"instrument_name": "YFI_BTC",
								"subscription": "trade.YFI_BTC",
								"channel": "trade",
								"data": [
									{
										"dataTime": 1645139769555,
										"d": 2258312914797956554,
										"s": "BUY",
										"p": 0.5541,
										"q": 1E-06,
										"t": 1645139769539,
										"i": "YFI_BTC"
									}
								]
							}
						}}
						} */
						JToken token = JToken.Parse(msg.ToStringFromUTF8());
						if (
											token["method"].ToStringInvariant() == "ERROR"
											|| token["method"].ToStringInvariant() == "unknown"
									)
						{
							throw new APIException(
												token["code"].ToStringInvariant()
														+ ": "
														+ token["message"].ToStringInvariant()
										);
						}
						else if (token["method"].ToStringInvariant() == "public/heartbeat")
						{ /* For websocket connections, the system will send a heartbeat message to the client every 30 seconds.
				   * The client must respond back with the public/respond-heartbeat method, using the same matching id, within 5 seconds, or the connection will break. */
							var hrResponse = new
							{
								id = token["id"].ConvertInvariant<long>(),
								method = "public/respond-heartbeat",
							};
							await _socket.SendMessageAsync(hrResponse);

							if (
												token["message"].ToStringInvariant()
												== "server did not receive any client heartbeat, going to disconnect soon"
										)
								Logger.Warn(
													token["code"].ToStringInvariant()
															+ ": "
															+ token["message"].ToStringInvariant()
											);
						}
						else if (
											token["method"].ToStringInvariant() == "subscribe"
											&& token["result"] != null
									)
						{
							var result = token["result"];
							var dataArray = result["data"].ToArray();
							for (int i = 0; i < dataArray.Length; i++)
							{
								JToken data = dataArray[i];
								var trade = data.ParseTrade(
													"q",
													"p",
													"s",
													"t",
													TimestampType.UnixMilliseconds,
													"d"
											);
								string marketSymbol = data["i"].ToStringInvariant();
								if (dataArray.Length == 100) // initial snapshot contains 100 trades
								{
									trade.Flags |= ExchangeTradeFlags.IsFromSnapshot;
									if (i == dataArray.Length - 1)
										trade.Flags |= ExchangeTradeFlags.IsLastFromSnapshot;
								}
								await callback(
													new KeyValuePair<string, ExchangeTrade>(marketSymbol, trade)
											);
							}
						}
					},
					async (_socket) =>
					{ /* We recommend adding a 1-second sleep after establishing the websocket connection, and before requests are sent.
				 * This will avoid occurrences of rate-limit (`TOO_MANY_REQUESTS`) errors, as the websocket rate limits are pro-rated based on the calendar-second that the websocket connection was opened.
				 */
						await Task.Delay(1000);

						/*
						{
								"id": 11,
								"method": "subscribe",
								"params": {
								"channels": ["trade.ETH_CRO"]
								},
								"nonce": 1587523073344
						}
						 */
						var subscribeRequest = new
						{
							// + consider using id field in the future to differentiate between requests
							//id = new Random().Next(),
							method = "subscribe",
							@params = new
							{
								channels = marketSymbols
															.Select(s => string.IsNullOrWhiteSpace(s) ? "trade" : $"trade.{s}")
															.ToArray(),
							},
							nonce = await GenerateNonceAsync(),
						};
						await _socket.SendMessageAsync(subscribeRequest);
					}
			);
			ws.KeepAlive = new TimeSpan(0); // cryptocom throws bad request empty content msgs w/ keepalives
			return ws;
		}
	}

	public partial class ExchangeName
	{
		public const string CryptoCom = "CryptoCom";
	}
}
