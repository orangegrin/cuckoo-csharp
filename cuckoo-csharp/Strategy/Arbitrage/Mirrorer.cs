using cuckoo_csharp.Tools;
using ExchangeSharp;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace cuckoo_csharp.Strategy.Arbitrage
{
    class Mirrorer
    {
        private IWebSocket mOrderws;
        private bool mOrderDetailsAwsPong = false;
        private int mId;
        private string mDBKey;
        private Options mData;
        private IExchangeAPI mExchangeAAPI;
        private IExchangeAPI mExchangeBAPI;
        private bool mOrderwsConnect = false;
        public Mirrorer(Options config, int id)
        {
            mId = id;
            mDBKey = string.Format("MIRRORER:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
            mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            mExchangeAAPI = new ExchangeBitMEXAPI();
            mExchangeBAPI = new ExchangeBitMEXAPI();
            if (mExchangeAAPI == mExchangeBAPI) {
                throw new Exception("Single exchanges are not supported.");
            }
        }

        internal void Start()
        {
            mExchangeAAPI.LoadAPIKeys(mData.EncryptedFileA);
            mExchangeBAPI.LoadAPIKeys(mData.EncryptedFileB);
            SubWebSocket();
            WebSocketProtect();
        }
        private void SubWebSocket()
        {
            mOrderws = mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
            mOrderws.Connected += async (socket) => { mOrderwsConnect = true; Logger.Debug("GetOrderDetailsWebSocket 连接");  };
            mOrderws.Disconnected += async (socket) =>
            {
                mOrderwsConnect = false;
                WSDisConnectAsync("GetOrderDetailsWebSocket 连接断开");
            };
        }
        /// <summary>
        /// WS 守护线程
        /// </summary>
        private async void WebSocketProtect()
        {
            while (true)
            {
                if (!OnConnect())
                {
                    await Task.Delay(10 * 1000);
                    continue;
                }
                await mOrderws.SendMessageAsync("ping");
                int delayTime = 10;//保证次数至少要5s一次，否则重启
                mOrderDetailsAwsPong = false;
                await Task.Delay(1000 * delayTime);
                Logger.Debug(Utils.Str2Json("mOrderDetailsAwsPong", mOrderDetailsAwsPong));
                bool detailConnect = mOrderDetailsAwsPong;
                if ( (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    mOrderwsConnect = false;
                    await Task.Delay(5 * 1000);
                    Logger.Debug("销毁ws");
                    mOrderws.Dispose();
                    Logger.Debug("开始重新连接ws");
                    SubWebSocket();
                    await Task.Delay(5 * 1000);
                }
            }
        }
        private bool OnConnect()
        {
            return mOrderwsConnect;
        }
        private async Task WSDisConnectAsync(string tag)
        {
            await Task.Delay(5 * 60 * 1000);
            if (OnConnect() == false)
                throw new Exception(tag + " 连接断开");
        }
        private Dictionary<string, string> mOrderPairs = new Dictionary<string, string>();
        private Dictionary<string, decimal> mFilledPartiallyDic = new Dictionary<string, decimal>();

        private void OnOrderAHandler(ExchangeOrderResult order)
        {
            mOrderDetailsAwsPong = true ;
            if (order.MarketSymbol.Equals("pong"))
            {
                Logger.Debug("pong");
                return;
            }
            mData = Options.LoadFromDB<Options>(mDBKey);
            Logger.Debug("Current Multiple: " + mData.Multiple);
            if (order.MarketSymbol != mData.SymbolA)
                return;
            switch (order.Result)
            {
                case ExchangeAPIOrderResult.Unknown:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Unknown ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Filled:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Filled ---------------------------");
                    OnOrderFilledAsync(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartially:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order FilledPartially ---------------------------");
                    OnFilledPartially(order);
                    break;
                case ExchangeAPIOrderResult.Pending:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Pending ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Error:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Error ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Canceled:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Canceled ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.FilledPartiallyAndCancelled:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order FilledPartiallyAndCancelled ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.PendingCancel:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order PendingCancel ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                default:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Default ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
            }
        }

        private void OnFilledPartially(ExchangeOrderResult order)
        {
            MirrorOrderAsync(order);
        }

        private void OnOrderFilledAsync(ExchangeOrderResult order)
        {
            MirrorOrderAsync(order);
        }
        private decimal GetExchangeAmount(ExchangeOrderResult order)
        {
            Logger.Debug("mId:" + mId + "  " + "-------------------- GetExchangeAmount ---------------------------");
            decimal filledAmount = 0;
            mFilledPartiallyDic.TryGetValue(order.OrderId, out filledAmount);
            Logger.Debug("mId:" + mId + " filledAmount: " + filledAmount.ToStringInvariant());
            if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount == 0)
            {
                mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                return order.AmountFilled;
            }
            if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount != 0)
            {
                mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                return order.AmountFilled - filledAmount;
            }

            if (order.Result == ExchangeAPIOrderResult.Filled && filledAmount == 0)
            {
                return order.Amount;
            }

            if (order.Result == ExchangeAPIOrderResult.Filled && filledAmount != 0)
            {
                mFilledPartiallyDic.Remove(order.OrderId);
                return order.Amount - filledAmount;
            }
            return 0;
        }
        private async Task MirrorOrderAsync(ExchangeOrderResult order)
        {
            var amount = GetExchangeAmount(order);
            if (amount == 0)
                return;
            ExchangeOrderRequest requestA = new ExchangeOrderRequest()
            {
                Amount = amount * mData.Multiple,
                MarketSymbol = mData.SymbolB,
                IsBuy = order.IsBuy,
                OrderType = OrderType.Market
            };
            start:
            try
            {
                var v = await mExchangeBAPI.PlaceOrdersAsync(requestA);
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
                Task.Delay(mData.IntervalMillisecond);
                goto start;
            }
        }

        public class Options
        {
            public string ExchangeNameA;
            public string ExchangeNameB;
            public string SymbolA;
            public string SymbolB;
            /// <summary>
            /// 间隔时间
            /// </summary>
            public int IntervalMillisecond = 1000;
            public int Multiple = 10;
            /// <summary>
            /// A交易所加密串路径
            /// </summary>
            public string EncryptedFileA;
            /// <summary>
            /// B交易所加密串路径
            /// </summary>
            public string EncryptedFileB;

            public void SaveToDB(string DBKey)
            {
                RedisDB.Instance.StringSet(DBKey, this);
            }
            public static T LoadFromDB<T>(string DBKey)
            {
                return RedisDB.Instance.StringGet<T>(DBKey);
            }
        }
    }
}
