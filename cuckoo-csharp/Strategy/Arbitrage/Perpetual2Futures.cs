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

namespace cuckoo_csharp.Strategy.Arbitrage
{
    /// <summary>
    /// 期对期
    /// 要求 A价<B价，并且A限价开仓
    /// </summary>
    public class Perpetual2Futures
    {
        private IExchangeAPI mExchangeAAPI;
        private IExchangeAPI mExchangeBAPI;
        private IWebSocket mOrderws;
        private IWebSocket mOrderBookAws;
        private IWebSocket mOrderBookBws;

        private int mOrderBookAwsCounter=0;
        private int mOrderBookBwsCounter=0;
        private int mOrderDetailsAwsCounter = 0;
        private int mId;
        /// <summary>
        /// A交易所的的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderBookA;
        /// <summary>
        /// B交易所的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderBookB;
        /// <summary>
        /// 当前挂出去的订单
        /// </summary>
        private ExchangeOrderResult mCurOrderA;
        /// <summary>
        /// A交易所的历史订单ID
        /// </summary>
        private List<string> mOrderIds = new List<string>();
        /// <summary>
        /// 部分填充
        /// </summary>
        private Dictionary<string, decimal> mFilledPartiallyDic = new Dictionary<string, decimal>();
        private Options mData { get; set; }
        /// <summary>
        /// 当前开仓数量
        /// </summary>
        private decimal mCurAmount
        {
            get
            {
                return mData.CurAmount;
            }
            set
            {
                mData.CurAmount = value;
                mData.SaveToDB(mDBKey);
            }
        }
        private string mDBKey;
        private Task mRunningTask;
        private bool mExchangePending = false;
        private bool mOrderwsConnect = false;
        private bool mOrderBookAwsConnect = false;
        private bool mOrderBookBwsConnect = false;
        public Perpetual2Futures(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("INTERTEMPORAL:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
            RedisDB.Init(config.RedisConfig);
            mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mData.ExchangeNameA);
            mExchangeBAPI = mExchangeAAPI;
            UpdateAvgDiffAsync();
        }
        public void Start()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
            mExchangeAAPI.LoadAPIKeys(mData.EncryptedFileA);
            SubWebSocket();
            WebSocketProtect();
        }
        /// <summary>
        /// 倒计时平仓
        /// </summary>
        private async Task ClosePosition()
        {
            double deltaTime = (mData.CloseDate - DateTime.Now).TotalSeconds;
            Logger.Debug(Utils.Str2Json("deltaTime",deltaTime));
            await Task.Delay((int)(deltaTime * 1000));
            Logger.Debug("关闭策略只平仓不开仓");
            lock(mData)
            {
                foreach (var diff in mData.DiffGrid)
                {
                    diff.MaxAmount = 0;
                    diff.InitialExchangeBAmount = 0;
                }
                mData.SaveToDB(mDBKey);
            }
        }
#region Connect
        private void SubWebSocket()
        {
            mOrderws = mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
            mOrderws.Connected += async (socket) => { mOrderwsConnect = true; Logger.Debug("GetOrderDetailsWebSocket 连接"); };
            mOrderws.Disconnected += async (socket) =>
            {
                mOrderwsConnect = false;
                WSDisConnectAsync("GetOrderDetailsWebSocket 连接断开");
            };
            //避免没有订阅成功就开始订单
            Thread.Sleep(3 * 1000);
            mOrderBookAws = mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 20, mData.SymbolA);
            mOrderBookAws.Connected += async (socket) => { mOrderBookAwsConnect = true; Logger.Debug("GetFullOrderBookWebSocket A 连接"); };
            mOrderBookAws.Disconnected += async (socket) =>
            {
                mOrderBookAwsConnect = false;
                WSDisConnectAsync("GetFullOrderBookWebSocket A 连接断开");
            };
            mOrderBookBws = mExchangeBAPI.GetFullOrderBookWebSocket(OnOrderbookBHandler, 20, mData.SymbolB);
            mOrderBookBws.Connected += async (socket) => { mOrderBookBwsConnect = true; Logger.Debug("GetFullOrderBookWebSocket B 连接"); };
            mOrderBookBws.Disconnected += async (socket) =>
            {
                mOrderBookBwsConnect = false;
                WSDisConnectAsync("GetFullOrderBookWebSocket B 连接断开");
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
                    await Task.Delay(5 * 1000);
                    continue;
                }
                int delayTime = 60*5;//保证次数至少要2s一次，否则重启
                mOrderBookAwsCounter = 0;
                mOrderBookBwsCounter = 0;
                mOrderDetailsAwsCounter = 0;
                await Task.Delay( 1000 * delayTime);
                Logger.Debug(Utils.Str2Json("mOrderBookAwsCounter" , mOrderBookAwsCounter , "mOrderBookBwsCounter" , mOrderBookBwsCounter, "mOrderDetailsAwsCounter" ,mOrderDetailsAwsCounter));
                bool detailConnect = true;
                if(mOrderDetailsAwsCounter==0)
                    detailConnect = await IsConnectAsync();
                Logger.Debug(Utils.Str2Json("mOrderDetailsAwsCounter",mOrderDetailsAwsCounter));
                if (mOrderBookAwsCounter< delayTime/2 || mOrderBookBwsCounter< delayTime/2 || (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    if (mCurOrderA != null)
                    {
                        CancelCurOrderA();
                    }
                    mOrderwsConnect = false;
                    mOrderBookAwsConnect = false;
                    mOrderBookBwsConnect = false;
                    await Task.Delay(5 * 1000);
                    Logger.Debug("销毁ws");
                    mOrderws.Dispose();
                    mOrderBookAws.Dispose();
                    mOrderBookBws.Dispose();
                    Logger.Debug("开始重新连接ws");
                    SubWebSocket();
                    await Task.Delay(5 * 1000);
                }
            }
        }
        /// <summary>
        /// 测试 GetOrderDetailsWebSocket 是否有推送消息
        /// 发送一个多单限价，用卖一+100作为价格（一定被取消）。 等待10s如果GetOrderDetailsWebSocket没有返回消息说明已经断开
        private async Task<bool> IsConnectAsync()
        {
            decimal buyPrice;
            lock(mOrderBookA)
            {
                buyPrice = mOrderBookA.Asks.FirstOrDefault().Value.Price+100;
            }
            ExchangeOrderRequest request = new ExchangeOrderRequest()
            {
                ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
            };
            request.Amount = mData.PerTrans;
            request.MarketSymbol = mData.SymbolA;
            request.IsBuy = true;
            request.OrderType = OrderType.Limit;
            request.Price = NormalizationMinUnit(buyPrice);
            for (int i = 1; ; i++)//防止bitmex overload一直提交到成功
            {
                try
                {
                    var orderResults = await mExchangeAAPI.PlaceOrdersAsync(request);
                    break;
                }
                catch (System.Exception ex)
                {
                    if (ex.ToString().Contains("overloaded"))
                    {
                        await Task.Delay(2000);
                    }
                    else
                    {
                        Logger.Error(Utils.Str2Json("IsConnectAsync抛错", ex.ToString()));
                        throw ex;
                        break;
                    }

                }
            }
            await Task.Delay(10 * 1000);
            return mOrderDetailsAwsCounter > 0;
        }
        private bool OnConnect()
        {
            return mOrderwsConnect & mOrderBookAwsConnect & mOrderBookBwsConnect ;
        }
        private async Task WSDisConnectAsync(string tag)
        {
            if (mCurOrderA != null)
            {
                CancelCurOrderA();
            }
            await Task.Delay(10 * 60 * 1000);
            if(OnConnect()==false)
                throw new Exception(tag+" 连接断开");
        }
#endregion
        private void OnProcessExit(object sender, EventArgs e)
        {
            Logger.Debug("------------------------ OnProcessExit ---------------------------");
            mExchangePending = true;
            if (mCurOrderA != null)
            {
                CancelCurOrderA();
                Thread.Sleep(5 * 1000);
            }
        }
        /// <summary>
        /// 获取交易所某个币种的数量
        /// </summary>
        /// <param name="exchange"></param>
        /// <param name="symbol"></param>
        /// <returns></returns>
        public async Task<decimal> GetAmountsAvailableToTradeAsync(IExchangeAPI exchange, string symbol)
        {
            var amounts = await exchange.GetAmountsAvailableToTradeAsync();
            decimal value = 0;
            foreach (var amount in amounts)
            {
                if (amount.Key == symbol)
                    value = amount.Value;
            }
            return value;
        }

        private void OnOrderbookHandler(ExchangeOrderBook order)
        {
            if (order.MarketSymbol == mData.SymbolA)
                OnOrderbookAHandler(order);
            else
                OnOrderbookBHandler(order);
        }

        private void OnOrderbookAHandler(ExchangeOrderBook order)
        {
            mOrderBookAwsCounter++;
            mOrderBookA = order;
            OnOrderBookHandler();
        }
        private void OnOrderbookBHandler(ExchangeOrderBook order)
        {
            mOrderBookBwsCounter++;
            mOrderBookB = order;
            OnOrderBookHandler();
        }
        async void OnOrderBookHandler()
        {
            if (Precondition())
            {
                mExchangePending = true;
                mData = Options.LoadFromDB<Options>(mDBKey);
                await Execute();
                await Task.Delay(mData.IntervalMillisecond);
                mExchangePending = false;
            }

        }
        private bool Precondition()
        {
            if (!OnConnect())
                return false;
            if (mOrderBookA == null || mOrderBookB == null)
                return false;
            if (mRunningTask != null)
                return false;
            if (mExchangePending)
                return false;
            if (mOrderBookA.Asks.Count == 0 || mOrderBookA.Bids.Count == 0 || mOrderBookB.Bids.Count == 0 || mOrderBookB.Asks.Count == 0)
                return false;
            return true;
        }
        private async Task Execute()
        {
            decimal buyPriceA;
            decimal sellPriceA;
            decimal sellPriceB;
            decimal buyPriceB;
            decimal bidAAmount, askAAmount, bidBAmount, askBAmount;
            decimal a2bDiff = 0;
            decimal b2aDiff = 0;
            decimal buyAmount = mData.PerTrans;
            lock (mOrderBookA)
            {
                if (Precondition())
                    return;
                buyPriceA = mOrderBookA.Bids.FirstOrDefault().Value.Price;
                sellPriceA = mOrderBookA.Asks.FirstOrDefault().Value.Price;
                bidAAmount = mOrderBookA.Bids.FirstOrDefault().Value.Amount;
                askAAmount = mOrderBookA.Asks.FirstOrDefault().Value.Amount;
            }
            lock (mOrderBookB)
            {
                sellPriceB = mOrderBookB.Bids.FirstOrDefault().Value.Price;
                buyPriceB = mOrderBookB.Asks.FirstOrDefault().Value.Price;
                bidBAmount = mOrderBookB.Bids.FirstOrDefault().Value.Amount;
                askBAmount = mOrderBookB.Asks.FirstOrDefault().Value.Amount;
            } 
            //有可能orderbook bids或者 asks没有改变
            if (buyPriceA != 0 && sellPriceA != 0 && sellPriceB != 0 && buyPriceB != 0 && buyAmount != 0)
            {
                a2bDiff = (buyPriceA/sellPriceB - 1);
                b2aDiff = (sellPriceA/buyPriceB - 1);
                Diff diff = GetDiff(a2bDiff, b2aDiff);
                PrintInfo(buyPriceA, sellPriceA, sellPriceB, buyPriceB, a2bDiff, b2aDiff, diff.A2BDiff, diff.B2ADiff, buyAmount, bidAAmount, askAAmount, bidBAmount, askBAmount);
                
                //满足差价并且
                //只能BBuyASell来开仓，也就是说 ABuyBSell只能用来平仓
                if (a2bDiff < diff.A2BDiff && mData.CurAmount + mData.PerTrans <= diff.InitialExchangeBAmount) //满足差价并且当前A空仓
                {
                    mRunningTask = A2BExchange(buyPriceA);
                }
                else if (b2aDiff > diff.B2ADiff && -mCurAmount < diff.MaxAmount) //满足差价并且没达到最大数量
                {
                    //如果只是修改订单
                    if (mCurOrderA != null && !mCurOrderA.IsBuy)
                    {
                        mRunningTask = B2AExchange(sellPriceA);
                    }
                    //表示是新创建订单
                    else //if (await SufficientBalance())
                    {
                        mRunningTask = B2AExchange(sellPriceA);
                    }
                }
                else if (mCurOrderA != null && diff.B2ADiff >= a2bDiff && a2bDiff >= diff.A2BDiff)//如果在波动区间中，那么取消挂单
                {
                    Logger.Debug(Utils.Str2Json("在波动区间中取消订单" , a2bDiff.ToString()));
                    ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                    cancleRequestA.ExtraParameters.Add("orderID", mCurOrderA.OrderId);
                    mRunningTask = mExchangeAAPI.CancelOrderAsync(mCurOrderA.OrderId, mData.SymbolA);
                    await Task.Delay(5000);
                }

                if (mRunningTask != null)
                {
                    try
                    {
                        await mRunningTask;
                        mRunningTask = null;
                    }
                    catch (System.Exception ex)
                    {
                        Logger.Error(Utils.Str2Json("Execute ex", ex));
                        
                    }
                }
            }
        }
        /// <summary>
        /// 网格，分为n段。从小范围到大范围，如果当前方向为开仓，并且小于当前最大开仓数量 那么设置diff为本段值
        /// </summary>
        /// <param name="a2bDiff"></param>
        /// <param name="b2aDiff"></param>
        private Diff GetDiff(decimal a2bDiff,decimal b2aDiff)
        {
            List<Diff> diffList;
            lock (mData.DiffGrid)
            {
                diffList = new List<Diff>(mData.DiffGrid);
            }
            Diff returnDiff = diffList[0];
            foreach (var diff in diffList)
            {
                if(a2bDiff < diff.A2BDiff && mCurAmount + mData.PerTrans <= diff.InitialExchangeBAmount)
                {
                    returnDiff = diff;
                    break;
                }
                else if (b2aDiff > diff.B2ADiff && -mCurAmount < diff.MaxAmount)
                {
                    returnDiff = diff;
                    break;
                }
            }
            return returnDiff;
        }

        /// <summary>
        /// 刷新差价
        /// </summary>
        public async Task UpdateAvgDiffAsync()
        {
            string url = $"{"http://150.109.52.225:8888/diff?symbol="}{mData.Symbol}{"&exchangeB="}{mData.ExchangeNameB.ToLowerInvariant()}{"&exchangeA="}{mData.ExchangeNameA.ToLowerInvariant()}";
            while (true)
            {
                if (mData.AutoCalcProfitRange)
                {
                    try
                    {
                        JObject jsonResult = await Utils.GetHttpReponseAsync(url);
                        if (jsonResult["status"].ConvertInvariant<int>() == 1)
                        {
                            decimal avgDiff = jsonResult["data"]["value"].ConvertInvariant<decimal>();
                            avgDiff = Math.Round(avgDiff, 4);//强行转换
                            foreach (var diff in mData.DiffGrid)
                            {
                                diff.A2BDiff = avgDiff - diff.ProfitRange + mData.DeltaDiff;
                                diff.B2ADiff = avgDiff + diff.ProfitRange + mData.DeltaDiff;
                                mData.SaveToDB(mDBKey);
                            }
                            Logger.Debug(Utils.Str2Json(" UpdateAvgDiffAsync avgDiff" , avgDiff));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(" UpdateAvgDiffAsync avgDiff:" + ex.ToString());
                    }
                }
                await Task.Delay(600 * 1000);
            }
        }
        private void PrintInfo(decimal bidA, decimal askA, decimal bidB, decimal askB, decimal a2bDiff, decimal b2aDiff, decimal A2BDiff, decimal B2ADiff, decimal buyAmount, 
            decimal bidAAmount, decimal askAAmount, decimal bidBAmount, decimal askBAmount )
        {
            Logger.Debug("================================================");
            Logger.Debug(Utils.Str2Json("BA价差当前百分比↑", a2bDiff.ToString(), "BA价差百分比↑", A2BDiff.ToString() )) ;
            Logger.Debug(Utils.Str2Json("BA价差当前百分比↓" , b2aDiff.ToString(), "BA价差百分比↓" , B2ADiff.ToString()));
            Logger.Debug(Utils.Str2Json("Bid A", bidA, " Bid B", bidB, "bidAAmount", bidAAmount, "bidBAmount", bidBAmount));
            Logger.Debug(Utils.Str2Json("Ask A", askA, " Ask B", askB, "askAAmount", askAAmount, "askBAmount", askBAmount));
            Logger.Debug(Utils.Str2Json("mCurAmount", mCurAmount, " buyAmount",  buyAmount));
        }
        /// <summary>
        /// 当curAmount 小于 0的时候就是平仓
        /// A买B卖
        /// </summary>
        private async Task A2BExchange(decimal buyPrice)
        {
            //A限价买
            ExchangeOrderRequest requestA = new ExchangeOrderRequest()
            {
                ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
            };
            requestA.Amount = mData.PerTrans;
            requestA.MarketSymbol = mData.SymbolA;
            requestA.IsBuy = true;
            requestA.OrderType = OrderType.Limit;
            requestA.Price = NormalizationMinUnit(buyPrice);
            bool isAddNew = true;
            try
            {
                if (mCurOrderA != null)
                {
                    //方向相同，并且达到修改条件
                    if (mCurOrderA.IsBuy == requestA.IsBuy)
                    {
                        isAddNew = false;
                        requestA.ExtraParameters.Add("orderID", mCurOrderA.OrderId);
                        //检查是否有改动必要
                        //做多涨价则判断
                        if (requestA.Price == mCurOrderA.Price)
                        {
                            return;
                        }
                    }
                    else
                    {//如果方向相反那么直接取消
                        await CancelCurOrderA();
                    }
                    //如果已出现部分成交并且需要修改价格，则取消部分成交并重新创建新的订单
                    if (mFilledPartiallyDic.Keys.Contains(mCurOrderA.OrderId))
                    {
                        await CancelCurOrderA();
                        if (requestA.ExtraParameters.Keys.Contains("orderID"))
                            requestA.ExtraParameters.Remove("orderID");
                    }
                };
                var v = await mExchangeAAPI.PlaceOrdersAsync(requestA);
                mCurOrderA = v[0];
                mOrderIds.Add(mCurOrderA.OrderId);
                Logger.Debug(Utils.Str2Json(  "requestA" , requestA.ToString()));
                Logger.Debug(Utils.Str2Json(  "Add mCurrentLimitOrder" , mCurOrderA.ToExcleString() , "CurAmount" , mData.CurAmount));
                if (mCurOrderA.Result == ExchangeAPIOrderResult.Canceled)
                {
                    mCurOrderA = null;
                    await Task.Delay(2000);
                }
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                //如果是添加新单那么设置为null
                if (isAddNew || ex.ToString().Contains("Invalid orderID"))
                    mCurOrderA = null;
                Logger.Error(Utils.Str2Json(  "ex",ex));
                if (ex.ToString().Contains("overloaded"))
                    await Task.Delay(5000);
                if (ex.ToString().Contains("RateLimitError"))
                    await Task.Delay(30000);
            }
        }
        private async Task CancelCurOrderA()
        {
            ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
            cancleRequestA.ExtraParameters.Add("orderID", mCurOrderA.OrderId);
            //在onOrderCancle的时候处理
            await mExchangeAAPI.CancelOrderAsync(mCurOrderA.OrderId, mData.SymbolA);
        }
        /// <summary>
        /// 当curAmount大于0的时候就是开仓
        /// B买A卖
        /// </summary>
        /// <param name="exchangeAmount"></param>
        private async Task B2AExchange(decimal sellPrice)
        {
            //开仓
            ExchangeOrderRequest requestA = new ExchangeOrderRequest()
            {
                ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
            };
            requestA.Amount = mData.PerTrans;
            requestA.MarketSymbol = mData.SymbolA;
            requestA.IsBuy = false;
            requestA.OrderType = OrderType.Limit;
            //避免市价成交
            requestA.Price = NormalizationMinUnit(sellPrice);
            //如果当前有限价单，并且方向不相同，那么取消
            //如果方向相同那么改价
            bool newOrder = true;
            try
            {
                if (mCurOrderA != null)
                {
                    if (mCurOrderA.IsBuy == requestA.IsBuy)
                    {
                        newOrder = false;
                        requestA.ExtraParameters.Add("orderID", mCurOrderA.OrderId);
                        //检查是否有改动必要
                        //做空涨价则判断
                        if (requestA.Price == mCurOrderA.Price)
                        {
                            return;
                        }
                    }
                    else
                    {
                        CancelCurOrderA();
                    }
                    //如果已出现部分成交并且需要修改价格，则取消部分成交并重新创建新的订单
                    if (mFilledPartiallyDic.Keys.Contains(mCurOrderA.OrderId))
                    {
                        await CancelCurOrderA();
                        if (requestA.ExtraParameters.Keys.Contains("orderID"))
                            requestA.ExtraParameters.Remove("orderID");
                    }
                };
                var orderResults = await mExchangeAAPI.PlaceOrdersAsync(requestA);
                mCurOrderA = orderResults[0];
                mOrderIds.Add(mCurOrderA.OrderId);
                Logger.Debug(Utils.Str2Json(  "requestA" , requestA.ToString()));
                Logger.Debug(Utils.Str2Json(  "Add mCurrentLimitOrder" , mCurOrderA.ToExcleString()));
                if (mCurOrderA.Result == ExchangeAPIOrderResult.Canceled)
                {
                    mCurOrderA = null;
                    await Task.Delay(2000);
                }
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                //如果是添加新单那么设置为null
                if (newOrder || ex.ToString().Contains("Invalid orderID"))
                    mCurOrderA = null;
                Logger.Error(Utils.Str2Json( "ex", ex));
                if (ex.ToString().Contains("overloaded"))
                    await Task.Delay(5000);
                if (ex.ToString().Contains("RateLimitError"))
                    await Task.Delay(30000);
            }
        }
        /// <summary>
        /// 检查是否有足够的币
        /// </summary>
        /// <returns></returns>
        private async Task<bool> SufficientBalance()
        {
            var bAmount = await GetAmountsAvailableToTradeAsync(mExchangeBAPI, mData.AmountSymbol);
            decimal buyPrice;
            decimal exchangeAmount;
            lock (mOrderBookB)
            {
                mOrderBookB.GetPriceToBuy(mData.PerTrans, out exchangeAmount, out buyPrice);
            }
            //避免挂新单之前，上一笔B的市价没有成交完
            var spend = mData.PerTrans * buyPrice * 1.3m;
            if (bAmount < spend)
            {
                Logger.Debug(Utils.Str2Json("Insufficient exchange balance", bAmount," ,need spend", spend));
                await Task.Delay(5000);
                return false;
            }
            else
            {
                Logger.Debug(Utils.Str2Json("Insufficient exchange balance", bAmount, " ,need spend", spend));
            }
            return true;
        }
        /// <summary>
        /// 订单成交 ，修改当前仓位和删除当前订单
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderFilled(ExchangeOrderResult order)
        {
            Logger.Debug( "-------------------- Order Filed ---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            void fun()
            {
                mExchangePending = true;
                ReverseOpenMarketOrder(order);
                mExchangePending = false;
                // 如果 当前挂单和订单相同那么删除
                if (mCurOrderA != null && mCurOrderA.OrderId == order.OrderId)
                {
                    //重置数量
                    mCurOrderA = null;
                }
                PrintFilledOrder(order);
            }
            if (mCurOrderA !=null)//可能为null ，locknull报错
            {
                lock (mCurOrderA)
                {
                    fun();
                }
            }
            else
            {
                fun();
            }
        }
        /// <summary>
        /// 订单部分成交
        /// </summary>
        /// <param name="order"></param>
        private void OnFilledPartially(ExchangeOrderResult order)
        {
            if (order.Amount == order.AmountFilled)
                return;
            Logger.Debug( "-------------------- Order Filed Partially---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            ReverseOpenMarketOrder(order);
            PrintFilledOrder(order);
        }
        private void PrintFilledOrder(ExchangeOrderResult order)
        {
            try
            {
                Logger.Debug("--------------PrintFilledOrder--------------");
                Logger.Debug(Utils.Str2Json("filledTime", Utils.GetGMTimeTicks(order.OrderDate).ToString(),
                    "direction", order.IsBuy ? "buy" : "sell",
                    "orderData", order.ToExcleString()));
            }
            catch (Exception ex)
            {

                Logger.Error("PrintFilledOrder"+ex);
            }
        }
        /// <summary>
        /// 订单取消，删除当前订单
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderCanceled(ExchangeOrderResult order)
        {
            Logger.Debug("-------------------- Order Canceled ---------------------------");
            Logger.Debug("Canceled  " + order.ToExcleString() + "CurAmount" + mData.CurAmount);
            if (mCurOrderA != null && mCurOrderA.OrderId == order.OrderId)
            {
                mCurOrderA = null;
            }
        }
        /// <summary>
        /// 当A交易所的订单发生改变时候触发
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderAHandler(ExchangeOrderResult order)
        {
            mOrderDetailsAwsCounter++;
            Logger.Debug("-------------------- OnOrderAHandler ---------------------------");
            if (order.MarketSymbol != mData.SymbolA)
                return;
            if (!IsMyOrder(order.OrderId))
                return;
            switch (order.Result)
            {
                case ExchangeAPIOrderResult.Unknown:
                    Logger.Debug("-------------------- Order Unknown ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Filled:
                    OnOrderFilled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartially:
                    OnFilledPartially(order);
                    break;
                case ExchangeAPIOrderResult.Pending:
                    Logger.Debug("-------------------- Order Pending ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Error:
                    Logger.Debug("-------------------- Order Error ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Canceled:
                    OnOrderCanceled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartiallyAndCancelled:
                    Logger.Debug("-------------------- Order FilledPartiallyAndCancelled ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.PendingCancel:
                    Logger.Debug("-------------------- Order PendingCancel ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                default:
                    Logger.Debug("-------------------- Order Default ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
            }
        }
        /// <summary>
        /// 计算反向开仓时应当开仓的数量（如果部分成交）
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        private decimal GetParTrans(ExchangeOrderResult order)
        {
            Logger.Debug("-------------------- GetParTrans ---------------------------");
            decimal filledAmount = 0;
            mFilledPartiallyDic.TryGetValue(order.OrderId, out filledAmount);
            Logger.Debug(  " filledAmount: " + filledAmount.ToStringInvariant());
            if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount == 0)
            {
                mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                return order.AmountFilled;
            }
            if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount != 0)
            {
                if(filledAmount< order.AmountFilled)
                {
                    mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                    return order.AmountFilled - filledAmount;
                }
                else
                    return 0;
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
        /// <summary>
        /// 反向市价开仓
        /// </summary>
        private async void ReverseOpenMarketOrder(ExchangeOrderResult order)
        {
            var transAmount = GetParTrans(order);
            if (transAmount <= 0)//部分成交返回两次一样的数据，导致第二次transAmount=0
                return;
            if (order.AveragePrice * transAmount < mData.MinOrderPrice)//如果小于最小成交价格，1补全到最小成交价格的数量x，A交易所买x，B交易所卖x+transAmount
            {
                for (int i = 1; ; i++)//防止bitmex overload一直提交到成功
                {
                    try
                    {
                        transAmount = await SetMinOrder(order, transAmount);
                        break;
                    }
                    catch (System.Exception ex)
                    {
                        if (ex.ToString().Contains("overloaded"))
                        {
                            await Task.Delay(2000);
                        }
                        else
                        {
                            Logger.Error(Utils.Str2Json("最小成交价抛错" , ex.ToString()));
                            throw ex;
                            break; 
                        }
                        
                    }
                }
            }
            //只有在成交后才修改订单数量
            mCurAmount += order.IsBuy ? transAmount : -transAmount;
            Logger.Debug(Utils.Str2Json(  "CurAmount:" , mData.CurAmount));
            Logger.Debug("mId{0} {1}", mId, mCurAmount);
            var req = new ExchangeOrderRequest();
            req.Amount = transAmount;
            req.IsBuy = !order.IsBuy;
            req.OrderType = OrderType.Market;
            req.MarketSymbol = mData.SymbolB;
            Logger.Debug( "----------------------------ReverseOpenMarketOrder---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            Logger.Debug(req.ToStringInvariant());
            var ticks = DateTime.Now.Ticks;

            
            for (int i = 1; ; i++)//当B交易所也是bitmex， 防止bitmex overload一直提交到成功
            {
                try
                {
                    var res = await mExchangeBAPI.PlaceOrderAsync(req);
                    Logger.Debug(  "--------------------------------ReverseOpenMarketOrder Result-------------------------------------");
                    Logger.Debug(res.ToString());
                    break;
                }
                catch (Exception ex)
                {
                    if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("403 Forbidden"))
                    {
                        Logger.Error(Utils.Str2Json( "req", req.ToStringInvariant(), "ex", ex));
                        await Task.Delay(2000);
                    }
                    else
                    {
                        Logger.Error(Utils.Str2Json("ReverseOpenMarketOrder抛错" , ex.ToString()));
                        throw ex;
                        break;
                    }
                }
            }
            await Task.Delay(mData.IntervalMillisecond);
        }
        /// <summary>
        /// 如果overload抛出异常
        /// </summary>
        /// <param name="order"></param>
        /// <param name="transAmount"></param>
        /// <returns></returns>
        private async Task<decimal> SetMinOrder(ExchangeOrderResult order, decimal transAmount)
        {
            decimal addAmount = Math.Ceiling(mData.MinOrderPrice / order.AveragePrice) - transAmount;
            //市价买
            ExchangeOrderRequest requestA = new ExchangeOrderRequest();
            requestA.Amount = addAmount;
            requestA.MarketSymbol = mData.SymbolA;
            requestA.IsBuy = order.IsBuy;
            requestA.OrderType = OrderType.Market;
            try
            {
                var orderResults = await mExchangeAAPI.PlaceOrdersAsync(requestA);
                ExchangeOrderResult resultA = orderResults[0];
                transAmount = addAmount + transAmount;
            }
            catch (System.Exception ex)
            {
                Logger.Debug(Utils.Str2Json("SetMinOrder ex" + ex.ToString()));
                throw ex;
            }
            return transAmount;
        }
        /// <summary>
        /// 判断是否是我的ID
        /// </summary>
        /// <param name="orderId"></param>
        /// <returns></returns>
        private bool IsMyOrder(string orderId)
        {
            return mOrderIds.Contains(orderId);
        }
        /// <summary>
        /// 将价格整理成最小单位
        /// 当最小单位为0.5时
        /// 1.1 => 1
        /// 1.4 =>  1.05
        /// 1.8 => 2
        /// </summary>
        /// <param name="price"></param>
        /// <returns></returns>
        decimal NormalizationMinUnit(decimal price)
        {
            var s = 1 / mData.MinPriceUnit;
            return Math.Round(price * s) / s;
        }
        public class Options
        {
            public string ExchangeNameA;
            public string ExchangeNameB;
            public string SymbolA;
            public string SymbolB;
            public string Symbol;
            public decimal DeltaDiff = 0m;
            /// <summary>
            /// 开仓差
            /// </summary>
            public List<Diff> DiffGrid = new List<Diff>();
            public decimal PerTrans;
            /// <summary>
            /// 最小价格单位
            /// </summary>
            public decimal MinPriceUnit = 0.5m;
            /// <summary>
            /// 最小订单总价格
            /// </summary>
            public decimal MinOrderPrice = 0.0011m;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurAmount = 0;
            /// <summary>
            /// 间隔时间
            /// </summary>
            public int IntervalMillisecond = 500;
            /// <summary>
            /// 自动计算利润范围
            /// </summary>
            public bool AutoCalcProfitRange = false;
            /// <summary>
            /// 本位币
            /// </summary>
            public string AmountSymbol = "BTC";
            /// <summary>
            /// A交易所手续费
            /// </summary>
            public decimal FeesA;
            /// <summary>
            /// B交易所手续费
            /// </summary>
            public decimal FeesB;
            /// <summary>
            /// A交易所加密串路径
            /// </summary>
            public string EncryptedFileA;
            /// <summary>
            /// B交易所加密串路径
            /// </summary>
            public string EncryptedFileB;
            /// <summary>
            /// redis连接数据
            /// </summary>
            public string RedisConfig = "localhost";
            /// <summary>
            /// redis连接数据
            /// </summary>
            public DateTime CloseDate = DateTime.Now.AddMinutes(1);

            public void SaveToDB(string DBKey)
            {
                RedisDB.Instance.StringSet(DBKey, this);
            }
            public static T LoadFromDB<T>(string DBKey)
            {
                return RedisDB.Instance.StringGet<T>(DBKey);
            }
        }

        public class Diff
        {
            /// <summary>
            /// 开仓差
            /// </summary>
            public decimal A2BDiff;
            /// <summary>
            /// 平仓差
            /// </summary>
            public decimal B2ADiff;
            /// <summary>
            /// 利润范围
            /// 当AutoCalcProfitRange 开启时有效
            /// </summary>
            public decimal ProfitRange = 0.003m;
            /// <summary>
            /// 最大数量
            /// </summary>
            public decimal MaxAmount;
            /// <summary>
            /// 开始交易时候的初始火币数量
            /// </summary>
            public decimal InitialExchangeBAmount = 0m;
        }

    }
}