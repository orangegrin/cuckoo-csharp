using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp;
using System.Threading;
using cuckoo_csharp.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Globalization;
using StackExchange.Redis;
using System.Collections.Immutable;
using System.ComponentModel.Design;
using System.Security.Cryptography;

namespace cuckoo_csharp.Entity
{
    public class LimitMaker
    {
        public LimitMakerConfig limitMakerConfig;

        public IExchangeAPI exchangeAPI;

        public string SymbolA;

        /// <summary>
        /// 交易中的订单
        /// </summary>
        public  Dictionary<string, ExchangeOrderResult> mOpenOrderDic = new Dictionary<string, ExchangeOrderResult>();

        public IWebSocket mOrderws;
        private IWebSocket mOrderBookAws;
        public int mOrderBookAwsCounter = 0;
        public int mOrderBookBwsCounter = 0;
        public int mOrderDetailsAwsCounter = 0;

        public ExchangeOrderBook mOrderBookA;
        /// <summary>
        /// 是否需要OrderBook
        /// </summary>
        private bool mNeedOrderBook = false;
        /// <summary>
        /// 当前等待次数
        /// </summary>
        public int mCurWaitCount = 0;
        /// <summary>
        /// A交易所的历史订单ID
        /// </summary>
        public List<string> mOrderIds = new List<string>();
        /// <summary>
        /// A交易所 历史成交订单详情
        /// </summary>
        public Dictionary<string, ExchangeOrderResult> mOrderResultsDic = new Dictionary<string, ExchangeOrderResult>();
        /// <summary>
        /// A交易所的历史成交订单ID
        /// </summary>
        private List<string> mOrderFiledIds = new List<string>();
        /// <summary>
        /// 部分填充
        /// </summary>
        private Dictionary<string, decimal> mFilledPartiallyDic = new Dictionary<string, decimal>();

        private List<LimitMaker> limitMakers = new List<LimitMaker>();

        public Action<ExchangeOrderBook> mOnOrderBookChange;
        /// <summary>
        /// 当前开仓数量
        /// </summary>
        public decimal mCurAAmount
        {
            get
            {
                return limitMakerConfig.CurAAmount;
            }
            set
            {
                limitMakerConfig.CurAAmount = value;
                //limitMakerConfig.SaveToDB(mDBKey);
            }
        }
        /// <summary>
        /// 开仓总金额
        /// </summary>
        public decimal mCurAllPrice;
        private string mDBKey;
        private Task mRunningTask;
        private bool mExchangePending = false;
        public bool mOrderwsConnect = false;
        public bool mOrderBookAwsConnect = false;
        public bool mOrderBookBwsConnect = false;
        private bool mOnCheck = false;
        private bool mOnTrade = false;//是否在交易中
        private bool mOnConnecting = false;
        private bool mBuyAState;
        /// <summary>
        /// 指数价格
        /// </summary>
        private decimal mIndexPrice;


        public LimitMaker(LimitMakerConfig config, string exchangeName, string SymbolA)
        {
            limitMakerConfig = config;
            exchangeAPI = ExchangeAPI.GetExchangeAPI(exchangeName, config.Id);
            exchangeAPI.LoadAPIKeys(config.EncryptedFile);
            this.SymbolA = SymbolA;
        }

        public void Init()
        {
            mCurWaitCount = limitMakerConfig.WaitCount;
            mNeedOrderBook = limitMakerConfig.NeedOrderBook;
            SubWebSocket(ConnectState.All, this);
            WebSocketProtect(this);
            //全量orderbook 不需要检查
//             if (mNeedOrderBook)
//                 CheckOrderBook();

            //测试 sign 
            var request0 = new ExchangeOrderRequest()
            {
                MarketSymbol = "999999",
                Price = 11155,
                Amount = 1,
                IsBuy = true,
                OrderType = OrderType.Limit
            };
            request0.ExtraParameters.Add("positionEffect", 1);
            var request1 = new ExchangeOrderRequest()
            {
                MarketSymbol = "999999",
                Price = 11166,
                Amount = 2,
                IsBuy = true,
                OrderType = OrderType.Limit
            };
            request1.ExtraParameters.Add("positionEffect", 1);
            var request2 = new ExchangeOrderRequest()
            {
                MarketSymbol = "999999",
                Price = 11177,
                Amount = 3,
                IsBuy = true,
                OrderType = OrderType.Limit
            };
            request2.ExtraParameters.Add("positionEffect", 1);

            //mExchangeAAPI.PlaceOrderAsync(request0);
            //mExchangeAAPI.CancelOrderAsync("all");
            //mExchangeAAPI.CancelOrderAsync("11597157962063533", "BTC/CUSD");
            //mExchangeAAPI.CancelOrderAsync("11597373409557890", "999999");
            //mExchangeAAPI.PlaceOrdersAsync(request0, request1, request2);

            //mExchangeAAPI.GetOrderBookWebSocket((orderbook) => { }, 20, "1000000");

            //mExchangeAAPI.GetOrderDetailsWebSocket((ExchangeOrderResult r) => { });
            //exchangeAPI.GetOrderBookAsync(SymbolA);

            //             var v = exchangeAPI.GetOpenOrderDetailsAsync(SymbolA);
            //             Task.WaitAll(v);


            //http://yapi.coinidx.io/mock/267/api/v1/future/rpo
            // /api/v1/future/rpo
            //230230
            var playLoad = new Dictionary<string, object>() {
                { "cId" ,1000022 },
                { "mp" ,1500 },
                { "mq" ,20 },
                { "ts" ,1 }
            };
            /*
cId:1000022
mp:"1500"
mq:"20"
ts,1

Content-Type: application/json
apiKey: 230230
             */
            //TODO
            /*
            var headers = new Dictionary<string, string>() {
                { "Content-Type" ,"application/json" },
                { "apiKey" ,"230230" }
            };
            var v = Utils.PostHttpReponseAsync("https://apitest.ccfox.com/api/v1/future/rpo", playLoad,"POST", headers);
            Task.WaitAll(v);
            Logger.Debug(v.Result.ToString());
            */
        }

        #region connect

        private void SubWebSocket(ConnectState state = ConnectState.All, LimitMaker limitMaker = null)
        {
            limitMaker = this;
            mOnConnecting = true;

            if (state.HasFlag(ConnectState.Orders))
            {
                limitMaker.mOrderws = limitMaker.exchangeAPI.GetOrderDetailsWebSocket(limitMaker.OnOrderAHandler);
                limitMaker.mOrderws.Connected += async (socket) => { limitMaker.mOrderwsConnect = true; Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "GetOrderDetailsWebSocket 连接"); OnConnect(); };
                limitMaker.mOrderws.Disconnected += async (socket) =>
                {
                    limitMaker.mOrderwsConnect = false;
                    WSDisConnectAsync("GetOrderDetailsWebSocket 连接断开");
                };
            }
            //避免没有订阅成功就开始订单
            if(mNeedOrderBook)
            {
                if (state.HasFlag(ConnectState.A))
                {
                    mOrderBookAws = limitMaker.exchangeAPI.GetFullOrderBookWebSocket((orderbook) =>
                    {
                        mOrderBookAwsCounter++;
                        //Logger.Debug("mOrderBookAwsCounter1111::::" + mOrderBookAwsCounter);
                        mOrderBookA = orderbook;
                        if (mOnOrderBookChange!=null)
                        {
                            mOnOrderBookChange(mOrderBookA);
                        }
                    }, 20, limitMaker.SymbolA);
                    mOrderBookAws.Connected += async (socket) => { mOrderBookAwsConnect = true; Logger.Debug("GetFullOrderBookWebSocket A 连接"); OnConnect(); };
                    mOrderBookAws.Disconnected += async (socket) =>
                    {
                        mOrderBookAwsConnect = false;
                        WSDisConnectAsync("GetFullOrderBookWebSocket A 连接断开");
                    };
                }
            }
        }

        /// <summary>
        /// WS 守护线程
        /// </summary>
        private async void WebSocketProtect(LimitMaker limitMaker)
        {
            while (true)
            {
                if (!OnConnect())
                {
                    await Task.Delay(5 * 1000);
                    continue;
                }
                int delayTime = 60;//保证次数至少要3s一次，否则重启
                limitMaker.mOrderBookAwsCounter = 0;
                limitMaker.mOrderBookBwsCounter = 0;
                limitMaker.mOrderDetailsAwsCounter = 0;
                await Task.Delay(1000 * delayTime);
                Logger.Debug(Utils.Str2Json("mOrderBookAwsCounter", limitMaker.mOrderBookAwsCounter, "mOrderBookBwsCounter", limitMaker.mOrderBookBwsCounter, "mOrderDetailsAwsCounter", limitMaker.mOrderDetailsAwsCounter));
                bool detailConnect = true;
                if (limitMaker.mOrderDetailsAwsCounter == 0)
                    detailConnect = await IsConnectAsync(limitMaker);
                Logger.Debug(Utils.Str2Json("mOrderDetailsAwsCounter", limitMaker.mOrderDetailsAwsCounter));
                if (limitMaker.mOrderBookAwsCounter < CheckNeedOrderBookWS(1,0) || limitMaker.mOrderBookBwsCounter < 0 || (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    await ReconnectWS();
                }
            }
        }
        private async Task CheckOrderBook()
        {
            ExchangeOrderPrice? lastAAsk = null;
            ExchangeOrderPrice? curAAsk;

            ExchangeOrderPrice? lastABid = null;
            ExchangeOrderPrice? curABid;

            while (true)
            {
                await Task.Delay(30 * 1000);
                if (mOrderBookA == null  || !OnConnect())
                    continue;
                else
                {
                    if (lastAAsk == null)
                    {
                        lock (mOrderBookA)
                        {
                            lastAAsk = mOrderBookA.Asks.FirstOrDefault().Value;
                        }
                        await Task.Delay(60 * 1000);
                    }
                  

                    if (lastABid == null)
                    {
                        lock (mOrderBookA)
                        {
                            lastABid = mOrderBookA.Bids.FirstOrDefault().Value;
                        }
                        await Task.Delay(60 * 1000);
                    }

                    lock (mOrderBookA)
                    {
                        curAAsk = mOrderBookA.Asks.FirstOrDefault().Value;
                        curABid = mOrderBookA.Bids.FirstOrDefault().Value;
                    }
                    ConnectState? state = null;
                    if ((lastAAsk.Value.Price == curAAsk.Value.Price && lastAAsk.Value.Amount == curAAsk.Value.Amount) || (lastABid.Value.Price == curABid.Value.Price && lastABid.Value.Amount == curABid.Value.Amount))
                    {
                        state = ConnectState.A;

                    }
                    if (state != null)
                    {
                        Logger.Error("CheckOrderBook  orderbook 错误");
                        await ReconnectWS(state.Value);
                    }
                    lastAAsk = curAAsk;
                    lastABid = curABid;
                }

            }
        }
        /// <summary>
        /// 根据是否需要连接orderbook返回对应值
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="need"></param>
        /// <param name="notNeed"></param>
        /// <returns></returns>
        private T CheckNeedOrderBookWS<T>(T need,T notNeed)
        {
           return  mNeedOrderBook?need:notNeed;
        }
        private async Task ReconnectWS(ConnectState state = ConnectState.All)
        {
            await CancelAllOrders();

            await CloseWS(state);
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "开始重新连接ws");
            SubWebSocket(state);
            await Task.Delay(5 * 1000);
        }

        private async Task CloseWS(ConnectState state = ConnectState.All)
        {
            await Task.Delay(5 * 1000);
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "销毁ws");

            if (state.HasFlag(ConnectState.A))
            {
                mOrderBookAwsConnect = false;
                mOrderBookAws.Dispose();
            }
            if (state.HasFlag(ConnectState.Orders))
            {
                mOrderwsConnect = false;
                mOrderws.Dispose();
            }

        }

        /// <summary>
        /// 测试 GetOrderDetailsWebSocket 是否有推送消息
        /// 发送一个多单限价，用卖一+100作为价格（一定被取消）。 等待10s如果GetOrderDetailsWebSocket没有返回消息说明已经断开
        private async Task<bool> IsConnectAsync(LimitMaker limitMaker)
        {
            await mOrderws.SendMessageAsync("ping");
            await Task.Delay(5 * 1000);
            return limitMaker.mOrderDetailsAwsCounter > 0;
        }
        public bool OnConnect()
        {
            bool connect = mOrderwsConnect & (mNeedOrderBook ? mOrderBookAwsConnect:true);//& mOrderBookAwsConnect & mOrderBookBwsConnect;
            if (connect)
                mOnConnecting = false;
            return connect;
        }

        private async Task WSDisConnectAsync(string tag)
        {
            await CancelAllOrders();

            //删除避免重复 重连
            await Task.Delay(40 * 1000);
            Logger.Error(tag + " 连接断开");
            if (OnConnect() == false)
            {
                if (!mOnConnecting)//如果当前正在连接中那么不连接否则开始重连
                {
                    await CloseWS();
                    SubWebSocket(ConnectState.All, this);
                }
            }
        }
        #endregion


        /// <summary>
        ///获取orderbook
        /// </summary>
        /// <returns></returns>
        public async Task<ExchangeOrderBook> GetOrderBook()
        {
            return await exchangeAPI.GetOrderBookAsync(SymbolA, limitMakerConfig.EatLevel);
        }


        private ExchangeOrderResult GetOpenOrder(string orderId)
        {
            //lock(mOpenOrderDic)
            {
                if (mOpenOrderDic.TryGetValue(orderId, out ExchangeOrderResult mCurOrderA))
                {
                    return mCurOrderA;
                }
            }
            return null;
        }
        private async Task CancelCurOrderA(ExchangeOrderResult mCurOrderA)
        {
            ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
            cancleRequestA.ExtraParameters.Add("orderID", mCurOrderA.OrderId);
            //在onOrderCancle的时候处理
            string orderIDA = mCurOrderA.OrderId;
            try
            {
                await exchangeAPI.CancelOrderAsync(mCurOrderA.OrderId, SymbolA);

            }
            catch (Exception ex)
            {
                Logger.Debug(ex.ToString());
                if (ex.ToString().Contains("CancelOrderEx"))
                {
                    await Task.Delay(5000);
                    await exchangeAPI.CancelOrderAsync(mCurOrderA.OrderId, SymbolA);
                }
            }
        }
        /// <summary>
        /// 当A交易所的订单发生改变时候触发
        /// </summary>
        /// <param name="order"></param>
        public void OnOrderAHandler(ExchangeOrderResult order)
        {
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- OnOrderAHandler ---------------------------");
            mOrderDetailsAwsCounter++;
            if (order.MarketSymbol.Equals("pong"))
            {
                Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "pong");
                return;
            }


            if (order.MarketSymbol != SymbolA)
                return;
            if (order.Message != null && order.Message.Equals("Position"))
            {
                mCurAAmount = order.Amount;
                mCurAllPrice = order.Price;
            }

            if (!IsMyOrder(order.OrderId))
            {
                AddFilledOrderResult(order);
                return;
            }

            switch (order.Result)
            {
                case ExchangeAPIOrderResult.Unknown:
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order Unknown ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Filled:
                    OnOrderFilled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartially:
                    OnFilledPartiallyAsync(order);
                    break;
                case ExchangeAPIOrderResult.Pending:
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order Pending ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Error:
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order Error ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Canceled:
                    OnOrderCanceled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartiallyAndCancelled:
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order FilledPartiallyAndCancelled ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.PendingCancel:
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order PendingCancel ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                default:
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order Default ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
            }
        }

        #region 
        /// <summary>
        /// 判断是否是我的ID
        /// </summary>
        /// <param name="orderId"></param>
        /// <returns></returns>
        private bool IsMyOrder(string orderId)
        {
            lock (mOrderIds)
            {
                return mOrderIds.Contains(orderId);
            }
        }

        public async Task CancelAllOrders()
        {
            await exchangeAPI.CancelOrderAsync("all");
            mOpenOrderDic.Clear();
        }
        /// <summary>
        /// 检查是否超过最大 挂单数量,如果超过那么取消部分订单
        /// </summary>
        /// <returns></returns>
        public async Task CheckMaxOrdersCountAsync()
        {

            if (mOpenOrderDic.Count >= limitMakerConfig.MaxOrderCount)
            {
                List<KeyValuePair<string, ExchangeOrderResult>> removeList = null;
                lock (mOpenOrderDic)
                {
                    int count = mOpenOrderDic.Count - limitMakerConfig.MaxOrderCount + limitMakerConfig.CancelCount;
                    var v = mOpenOrderDic.ToList();
                    v.Sort((a, b) =>
                    {
                        return Convert.ToInt32( (a.Value.OrderDate- b.Value.OrderDate).TotalSeconds);
                    });


                    removeList = v.Take(count).ToList();
                }
                if (removeList.Count > 0)
                {
                    foreach (var pair in removeList)
                    {
                        try
                        {
                            await exchangeAPI.CancelOrderAsync(pair.Value.OrderId, pair.Value.MarketSymbol);
                        }
                        catch (System.Exception ex)
                        {
                            if (!ex.ToString().Contains("order no exist"))
                                throw ex;
                            else
                                Logger.Debug(ex.ToString());
                        }
                        lock (mOpenOrderDic)
                        {
                            mOpenOrderDic.Remove(pair.Key);
                        }

                    }
                }
            }
        }

        private void AddFilledOrderResult(ExchangeOrderResult orderRequest)
        {
            lock (mOrderResultsDic)
            {
                if (orderRequest.Result == ExchangeAPIOrderResult.Filled || orderRequest.Result == ExchangeAPIOrderResult.FilledPartially)
                {
                    if (mOrderResultsDic.TryGetValue(orderRequest.OrderId, out ExchangeOrderResult lastResult))
                    {
                        if (lastResult.Result != ExchangeAPIOrderResult.Filled)
                        {
                            mOrderResultsDic[orderRequest.OrderId] = orderRequest;
                        }
                    }
                    else
                        mOrderResultsDic.Add(orderRequest.OrderId, orderRequest);
                }
            }
        }


        /// <summary>
        /// 订单成交 ，修改当前仓位和删除当前订单
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderFilled(ExchangeOrderResult order)
        {
            lock (mOrderFiledIds)//避免多线程
            {
                lock (mOpenOrderDic)
                {
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order Filed 0 ---------------------------");
                    if (mOrderFiledIds.Contains(order.OrderId))//可能重复提交同样的订单
                    {
                        Logger.Error(Utils.Str2Json("重复提交订单号", order.OrderId));
                        return;
                    }
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order Filed 1 ---------------------------");
                    mOrderFiledIds.Add(order.OrderId);
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + order.ToString());
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + order.ToExcleString());
                    Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order Filed 2 ---------------------------");

                    ChangePositionByFiledOrder(order);
                    // 如果 当前挂单和订单相同那么删除
                    ExchangeOrderResult mCurOrderA = GetOpenOrder(order.OrderId);
                    if (mCurOrderA != null && mCurOrderA.OrderId == order.OrderId)
                    {
                        mOpenOrderDic.Remove(mCurOrderA.OrderId);
                        mOnTrade = false;
                    }
                }
            }
        }
        /// <summary>
        /// 订单部分成交
        /// </summary>
        /// <param name="order"></param>
        private async Task OnFilledPartiallyAsync(ExchangeOrderResult order)
        {
            if (order.Amount == order.AmountFilled)
                return;
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order Filed Partially---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            ChangePositionByFiledOrder(order);
        }
        private void PrintFilledOrder(ExchangeOrderResult order, ExchangeOrderResult backOrder)
        {
            if (order == null)
                return;
            if (backOrder == null)
                return;
            try
            {
                Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "--------------PrintFilledOrder--------------");
                Logger.Debug(Utils.Str2Json("filledTime", Utils.GetGMTimeTicks(order.OrderDate).ToString(),
                    "direction", order.IsBuy ? "buy" : "sell",
                    "orderData", order.ToExcleString()));
                //如果是平仓打印日志记录 时间  ，diff，数量
                decimal lastAmount = mCurAAmount + (order.IsBuy ? -backOrder.Amount : backOrder.Amount);
                if ((lastAmount > 0 && !order.IsBuy) ||//正仓位，卖
                    (lastAmount < 0) && order.IsBuy)//负的仓位，买
                {
                    DateTime dt = backOrder.OrderDate.AddHours(8);
                    List<string> strList = new List<string>()
                    {
                        dt.ToShortDateString()+"/"+dt.ToLongTimeString(),order.IsBuy ? "buy" : "sell",backOrder.Amount.ToString(), (order.AveragePrice-backOrder.AveragePrice).ToString()
                    };
                    Utils.AppendCSV(new List<List<string>>() { strList }, Path.Combine(Directory.GetCurrentDirectory(), "Maker_" + limitMakerConfig.Id + ".csv"), false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("PrintFilledOrder" + ex);
            }
        }
        /// <summary>
        /// 订单取消，删除当前订单
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderCanceled(ExchangeOrderResult order)
        {
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- Order Canceled ---------------------------");
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "Canceled  " + order.ToExcleString() + "CurAmount" + limitMakerConfig.CurAAmount);

            ExchangeOrderResult mCurOrderA = GetOpenOrder(order.OrderId);
            if (mCurOrderA != null && mCurOrderA.OrderId == order.OrderId)
            {
                mOpenOrderDic.Remove(mCurOrderA.OrderId);
                mOnTrade = false;
            }
        }

        /// <summary>
        /// 反向市价开仓
        /// </summary>
        private void ChangePositionByFiledOrder(ExchangeOrderResult order)
        {
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "----------------------------111ChangePositionByFiledOrder---------------------------");
            var transAmount = GetParTrans(order);
            if (transAmount <= 0)//部分成交返回两次一样的数据，导致第二次transAmount=0
                return;
            //只有在成交后才修改订单数量
            //mCurAAmount += order.IsBuy ? transAmount : -transAmount;
            Logger.Debug(Utils.Str2Json("CurAmount:", mCurAAmount));
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "mId{0} {1}", limitMakerConfig.Id, mCurAAmount);
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "----------------------------222ChangePositionByFiledOrder---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
        }
        /// <summary>
        /// 计算反向开仓时应当开仓的数量（如果部分成交）
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        private decimal GetParTrans(ExchangeOrderResult order)
        {
            Logger.Debug("mId:" + limitMakerConfig.Id + "   " + "-------------------- GetParTrans ---------------------------");
            lock (mFilledPartiallyDic)//防止多线程并发
            {
                decimal filledAmount = 0;
                mFilledPartiallyDic.TryGetValue(order.OrderId, out filledAmount);
                Logger.Debug("mId:" + limitMakerConfig.Id + "   " + " filledAmount: " + filledAmount.ToStringInvariant());
                if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount == 0)
                {
                    mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                    return order.AmountFilled;
                }
                else if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount != 0)
                {
                    if (filledAmount < order.AmountFilled)
                    {
                        mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                        return order.AmountFilled - filledAmount;
                    }
                    else
                        return 0;
                }
                else if (order.Result == ExchangeAPIOrderResult.Filled && filledAmount == 0)
                {
                    return order.Amount;
                }
                else if (order.Result == ExchangeAPIOrderResult.Filled && filledAmount != 0)
                {
                    //mFilledPartiallyDic.Remove(order.OrderId);//修复部分成交多次重复推送 引起的bug
                    return order.Amount - filledAmount;
                }
                return 0;
            }
        }
        #endregion
        /// <summary>
        /// 检查是否满足交易等待时间
        /// </summary>
        /// <returns></returns>
        public bool CheckNeedWait()
        {
            if (mCurWaitCount <= 0)
            {
                mCurWaitCount = limitMakerConfig.WaitCount;
                return true;
            }
            mCurWaitCount--;
            return false;
        }


        public async Task CountDiffGridMaxCount()
        {

            //                 try
            //                 {
            //                     decimal noUseBtc = await GetAmountsAvailableToTradeAsync(exchangeAPI, "");
            //                     decimal allCoin = noUseBtc;
            //                     decimal avgPrice = ((mOrderBookA.Bids.FirstOrDefault().Value.Price + mOrderBookB.Asks.FirstOrDefault().Value.Price) / 2);
            //                     mAllPosition = allCoin * mData.Leverage / avgPrice / 2;//单位eth个数
            //                     mData.PerTrans = Math.Round(mData.PerBuyUSD / avgPrice / mData.MinAmountA) * mData.MinAmountA;
            //                     mData.ClosePerTrans = Math.Round(mData.ClosePerBuyUSD / avgPrice / mData.MinAmountA) * mData.MinAmountA;
            //                     decimal lastPosition = 0;
            //                     foreach (Diff diff in mData.DiffGrid)
            //                     {
            //                         lastPosition += mAllPosition * diff.Rate;
            //                         lastPosition = Math.Round(lastPosition / mData.PerTrans) * mData.PerTrans;
            //                         diff.MaxASellAmount = mData.OpenPositionSellA ? lastPosition : 0;
            //                         diff.MaxABuyAmount = mData.OpenPositionBuyA ? lastPosition : 0;
            //                     }
            //                     mData.SaveToDB(mDBKey);
            //                     Logger.Debug(Utils.Str2Json("noUseBtc", noUseBtc, "allPosition", mAllPosition));
            //                 }
            //                 catch (System.Exception ex)
            //                 {
            //                     Logger.Error("ChangeMaxCount ex" + ex.ToString());
            //                 }
        }

    }

    /// <summary>
    /// 限价做市
    /// </summary>
    public class LimitMakerConfig
    {
        public string Id;

        public decimal CurAAmount = 0;

        public decimal CurMoney = 10m;

        public decimal MaxLeverage = 0.1m;

        public decimal MinChangeAmountRate = 0.01m;
        public decimal MaxChangeAmountRate = 0.05m;
        /// <summary>
        /// 最小一跳,小数点后多少位
        /// </summary>
        public int PerRate = 2;
        /// <summary>
        /// 最大挂单数量
        /// </summary>
        public int MaxOrderCount = 50;
        /// <summary>
        /// 当挂单满了每次取消的数量
        /// </summary>
        public int CancelCount = 20;
        /// <summary>
        /// 是否为市价单
        /// </summary>
        public bool IsLimit = true;
        /// <summary>
        /// 是否需要orderbook
        /// </summary>
        public bool NeedOrderBook = false;
        /// <summary>
        /// 等待多少次后再开始
        /// </summary>
        public int WaitCount = 1;
        /// <summary>
        /// 吃前多少档 的数量
        /// </summary>
        public int EatLevel = 5;
        /// <summary>
        /// 吃前多少档 的数量
        /// </summary>
        public decimal MaxEat = 0.1m;
        /// <summary>
        /// 吃前多少档 的数量
        /// </summary>
        public decimal MinEat = 0.05m;
        /// <summary>
        /// 吃档 最小一跳,小数点后多少位
        /// </summary>
        public int PerEatRate = 2;
        /// <summary>
        /// 等待时间 单位ms
        /// </summary>
        public int DelayTime = 3000;
        /// <summary>
        /// 限价超过盘口随机取消率
        /// </summary>
        public decimal RandomCancelRate = 0.7m;
        /// <summary>
        /// 加密文件路径
        /// </summary>
        public string EncryptedFile;

        public decimal MaxPosition => MaxLeverage * CurMoney;
    }
    [Flags]
    enum ConnectState
    {
        All = 7,
        A = 1,
        B = 2,
        Orders = 4
    }
}

