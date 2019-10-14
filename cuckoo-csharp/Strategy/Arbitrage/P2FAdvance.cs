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

namespace cuckoo_csharp.Strategy.Arbitrage
{
    // eth永续= B ，(ethu19  * xbtusd ) =A  
    //eth永续 bid1/(ethu19 bid1 * xbtusd ask1)-1   （A买B卖）
    //eth永续 ask1/(ethu19 ask1 * xbtusd bid1)-1   （A卖B买）
    //买卖数量计算：  eth永续多仓价值=ethu19空仓价值+xbtusd空仓价值   （单位eth）
    /// <summary>
    /// 期对期
    /// 要求 A价<B价，并且A限价开仓
    /// 
    /// </summary>
    public class P2FAdvance
    {
        private IExchangeAPI mExchangeAAPI;
        private IExchangeAPI mExchangeBAPI;
        private IWebSocket mOrderws;
        private IWebSocket mOrderBookA1ws;
        private IWebSocket mOrderBookA2ws;
        private IWebSocket mOrderBookBws;

        private int mOrderBookA1wsCounter = 0;
        private int mOrderBookA2wsCounter = 0;
        private int mOrderBookBwsCounter = 0;
        private int mOrderDetailsAwsCounter = 0;
        private int mId;

        private readonly object mLockObj = new object();
        /// <summary>
        /// A交易所的的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderBookA1;
        /// <summary>
        /// A交易所的的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderBookA2;
        /// <summary>
        /// B交易所的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderBookB;
        /// <summary>
        /// 当前挂出去的订单空方向
        /// </summary>
        private ExchangeOrderResult mCurOrderAResult;
        private OrderType mCurOrderType;
        /// <summary>
        /// 当前订单成交后，反向开仓数量A1
        /// </summary>
        private decimal mChangeAmountA1;
        /// <summary>
        /// 当前订单成交后，反向开仓数量A2
        /// </summary>
        private decimal mChangeAmountA2;
        /// <summary>
        /// 当前订单成交后，反向开仓数量B2
        /// </summary>
        private decimal mChangeAmountB;
        /// <summary>
        /// A交易所的历史订单ID
        /// </summary>
        private List<string> mOrderIds = new List<string>();

        private List<string> mOrderIdsFiled = new List<string>();
        /// <summary>
        /// 部分填充
        /// </summary>
        private Dictionary<string, decimal> mFilledPartiallyDic = new Dictionary<string, decimal>();
        private Options mData { get; set; }
        /// <summary>
        /// 当前开仓数量
        /// </summary>
        private decimal mCurA1Amount
        {
            get
            {
                return mData.CurA1Amount;
            }
            set
            {
                mData.CurA1Amount = value;
                mData.SaveToDB(mDBKey);
            }
        }
        /// <summary>
        /// 当前开仓数量
        /// </summary>
        private decimal mCurA2Amount
        {
            get
            {
                return mData.CurA2Amount;
            }
            set
            {
                mData.CurA2Amount = value;
                mData.SaveToDB(mDBKey);
            }
        }
        /// <summary>
        /// 当前开仓数量
        /// </summary>
        private decimal mCurBAmount
        {
            get
            {
                return mData.CurBAmount;
            }
            set
            {
                mData.CurBAmount = value;
                mData.SaveToDB(mDBKey);
            }
        }
        /// <summary>
        /// 当前本位币数量（btc）
        /// </summary>
        private decimal mCurBasicCoinAmount
        {
            get
            {
                return mData.CurBasicCoinAmount;
            }
            set
            {
                mData.CurBasicCoinAmount = value;
                mData.SaveToDB(mDBKey);
            }
        }
        private string mDBKey;
        private Task mRunningTask;
        private bool mExchangePending = false;
        private bool mOrderwsConnect = false;
        private bool mOrderBookA1wsConnect = false;
        private bool mOrderBookA2wsConnect = false;
        private bool mOrderBookBwsConnect = false;
        private bool mOnTrade = false;//是否在交易中
        private bool mOnConnecting = false;
        public P2FAdvance(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("INTERTEMPORAL:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA1+config.SymbolA2, config.SymbolB, id);
            RedisDB.Init(config.RedisConfig);
            mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mData.ExchangeNameA);
            mExchangeBAPI = mExchangeAAPI;
        }
        public void Start()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
            mExchangeAAPI.LoadAPIKeys(mData.EncryptedFileA);
             UpdateAvgDiffAsync();
             SubWebSocket();
             //WebSocketProtect();
             //CheckPosition();
            //ChangeMaxCount();
        }
        #region Connect
        private void SubWebSocket()
        {
            mOnConnecting = true;
//             mOrderws = mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
//             mOrderws.Connected += async (socket) => { mOrderwsConnect = true; Logger.Debug("GetOrderDetailsWebSocket 连接"); OnConnect(); };
//             mOrderws.Disconnected += async (socket) =>
//             {
//                 mOrderwsConnect = false;
//                 WSDisConnectAsync("GetOrderDetailsWebSocket 连接断开");
//             };
            //避免没有订阅成功就开始订单
            Thread.Sleep(3 * 1000);
            mOrderBookA1ws = mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookA1Handler, 20, mData.SymbolA1);
            mOrderBookA1ws.Connected += async (socket) => { mOrderBookA1wsConnect = true; Logger.Debug("GetFullOrderBookWebSocket A1 连接"); OnConnect(); };
            mOrderBookA1ws.Disconnected += async (socket) =>
            {
                mOrderBookA1wsConnect = false;
                WSDisConnectAsync("GetFullOrderBookWebSocket A1 连接断开");
            };
            mOrderBookA2ws = mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookA2Handler, 20, mData.SymbolA2);
            mOrderBookA2ws.Connected += async (socket) => { mOrderBookA2wsConnect = true; Logger.Debug("GetFullOrderBookWebSocket A2 连接"); OnConnect(); };
            mOrderBookA2ws.Disconnected += async (socket) =>
            {
                mOrderBookA2wsConnect = false;
                WSDisConnectAsync("GetFullOrderBookWebSocket A2 连接断开");
            };
            mOrderBookBws = mExchangeBAPI.GetFullOrderBookWebSocket(OnOrderbookBHandler, 20, mData.SymbolB);
            mOrderBookBws.Connected += async (socket) => { mOrderBookBwsConnect = true; Logger.Debug("GetFullOrderBookWebSocket B 连接"); OnConnect(); };
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
                int delayTime = 30;//保证次数至少要3s一次，否则重启
                mOrderBookA1wsCounter = 0;
                mOrderBookA2wsCounter = 0;
                mOrderBookBwsCounter = 0;
                mOrderDetailsAwsCounter = 0;
                await Task.Delay(1000 * delayTime);
                Logger.Debug(Utils.Str2Json("mOrderBookA1wsCounter", mOrderBookA1wsCounter, "mOrderBookA2wsCounter", mOrderBookA2wsCounter, "mOrderBookBwsCounter", mOrderBookBwsCounter, "mOrderDetailsAwsCounter", mOrderDetailsAwsCounter));
                bool detailConnect = true;
                if (mOrderDetailsAwsCounter == 0)
                    detailConnect = await IsConnectAsync();
                Logger.Debug(Utils.Str2Json("mOrderDetailsAwsCounter", mOrderDetailsAwsCounter));
                if (mOrderBookA1wsCounter < 1 || mOrderBookA2wsCounter < 1 || mOrderBookBwsCounter < 1 || (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    if (mCurOrderAResult != null)
                    {
                        CancelCurOrderA();
                    }
                    await CloseWS();
                    Logger.Debug("开始重新连接ws");
                    SubWebSocket();
                    await Task.Delay(5 * 1000);
                }
            }
        }

        private async Task CloseWS()
        {
            mOrderwsConnect = false;
            mOrderBookA1wsConnect = false;
            mOrderBookA2wsConnect = false;
            mOrderBookBwsConnect = false;
            await Task.Delay(5 * 1000);
            Logger.Debug("销毁ws");
            mOrderws.Dispose();
            mOrderBookA1ws.Dispose();
            mOrderBookA2ws.Dispose();
            mOrderBookBws.Dispose();
        }

        /// <summary>
        /// 测试 GetOrderDetailsWebSocket 是否有推送消息
        /// 发送一个多单限价，用卖一+100作为价格（一定被取消）。 等待10s如果GetOrderDetailsWebSocket没有返回消息说明已经断开
        private async Task<bool> IsConnectAsync()
        {
            await mOrderws.SendMessageAsync("ping");
            await Task.Delay(5 * 1000);
            return mOrderDetailsAwsCounter > 0;
        }
        private bool OnConnect()
        {
            //             bool connect = mOrderwsConnect & mOrderBookA1wsConnect & mOrderBookA2wsConnect & mOrderBookBwsConnect;
            //             if (connect)
            //                 mOnConnecting = false;
            //             return connect;
            return true;
        }
        private async Task WSDisConnectAsync(string tag)
        {
            if (mCurOrderAResult != null)
            {
                CancelCurOrderA();
            }
            await Task.Delay(30 * 1000);
            Logger.Error(tag + " 连接断开");
            if (OnConnect() == false)
            {
                if (!mOnConnecting)//如果当前正在连接中那么不连接否则开始重连
                {
                    await CloseWS();
                    SubWebSocket();
                }
            }
        }
        /// <summary>
        /// 检查仓位是否对齐
        /// 在非开仓阶段检测，避免A成交B成交中的情况
        /// </summary>
        private async void CheckPosition()
        {
            while (true)
            {
                if (!OnConnect())
                {
                    await Task.Delay(5 * 1000);
                    continue;
                }
                if (mCurOrderAResult != null)
                    continue;
                if (mOnTrade)
                    continue;
                mExchangePending = true;
                Logger.Debug("-----------------------CheckPosition-----------------------------------");
                ExchangeMarginPositionResult posA1;
                ExchangeMarginPositionResult posA2;
                ExchangeMarginPositionResult posB;
                try
                {
                    //等待10秒 避免ws虽然推送数据刷新，但是rest 还没有刷新数据
                    await Task.Delay(10 * 1000);
                    posA1 = await mExchangeAAPI.GetOpenPositionAsync(mData.SymbolA1);
                    posA2 = await mExchangeAAPI.GetOpenPositionAsync(mData.SymbolA2);
                    posB = await mExchangeAAPI.GetOpenPositionAsync(mData.SymbolB);
                    if (posA1==null || posA2==null || posB==null)
                    {
                        mExchangePending = false;
                        await Task.Delay(5 * 60 * 1000);
                        continue;
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.Error(Utils.Str2Json("GetOpenPositionAsync ex", ex.ToString()));
                    mExchangePending = false;
                    continue;
                }
                //屏蔽计算仓位
                /*
                decimal realAmount = posA1.Amount;
                if ((posA1.Amount + posB.Amount) != 0)//如果没有对齐停止交易，市价单到对齐
                {
                    if (Math.Abs(posA1.Amount + posB.Amount) > mData.PerTrans * 10)
                    {
                        Logger.Error(Utils.Str2Json("CheckPosition ex", "A,B交易所相差过大 程序关闭，请手动处理"));
                        throw new Exception("A,B交易所相差过大 程序关闭，请手动处理");
                    }
                    for (int i = 0; ;)
                    {
                        decimal count = posA1.Amount + posB.Amount;
                        ExchangeOrderRequest requestA = new ExchangeOrderRequest();
                        requestA.Amount = Math.Abs(count);
                        requestA.MarketSymbol = mData.SymbolA1;
                        bool bigerA = Math.Abs(posA1.Amount) > Math.Abs(posB.Amount);//如果A数量多，A平仓，否则相反
                        bool isABuy = posA1.Amount > 0;
                        requestA.IsBuy = bigerA ? (!isABuy) : isABuy;//如果A数量多，平仓，那么和当前方向反向操作
                        requestA.OrderType = OrderType.Market;
                        try
                        {
                            Logger.Debug(Utils.Str2Json("差数量", count));
                            Logger.Debug(Utils.Str2Json("requestA", requestA.ToString()));
                            var orderResults = await mExchangeAAPI.PlaceOrdersAsync(requestA);
                            ExchangeOrderResult resultA = orderResults[0];
                            realAmount += requestA.IsBuy ? requestA.Amount : -requestA.Amount;
                            break;
                        }
                        catch (System.Exception ex)
                        {
                            if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Bad Gateway"))
                            {
                                await Task.Delay(2000);
                            }
                            else
                            {
                                Logger.Error(Utils.Str2Json("CheckPosition ex", ex.ToString()));
                                throw ex;
                            }
                        }
                    }
                }
                if (realAmount != mCurA1Amount)
                {
                    Logger.Debug(Utils.Str2Json("Change curAmount", realAmount));
                    mCurA1Amount = realAmount;
                }
                //==================挂止盈单==================如果止盈点价格>三倍当前价格那么不挂止盈单
                //一单为空，那么挂止盈多单，止盈价格为另一单的强平价格（另一单多+当前价格百分之20，空-当前价格百分之20）
                //一单为多 相反
                else if (realAmount != 0)*/
                {
                    decimal realAmountA1= mData.CurA1Amount;
                    decimal realAmountA2 =mData.CurA2Amount;
                    decimal realAmountB = mData.CurBAmount;
                    Logger.Debug(Utils.Str2Json("挂止盈单", "盈单", "realAmountA1", realAmountA1, "realAmountA2", realAmountA2, "realAmountB", realAmountB));
                    List<ExchangeOrderResult> profitA1;
                    List<ExchangeOrderResult> profitA2;
                    List<ExchangeOrderResult> profitB;
                    ExchangeOrderResult profitOrderA1 = null;
                    ExchangeOrderResult profitOrderA2 = null;
                    ExchangeOrderResult profitOrderB = null;
                    //下拉最新的值 ，来重新计算 改开多少止盈订单
                    try
                    {
                        profitA1 = new List<ExchangeOrderResult>(await mExchangeAAPI.GetOpenProfitOrderDetailsAsync(mData.SymbolA1,OrderType.Stop));
                        profitA2 = new List<ExchangeOrderResult>(await mExchangeAAPI.GetOpenProfitOrderDetailsAsync(mData.SymbolA2, OrderType.Stop));
                        profitB = new List<ExchangeOrderResult>(await mExchangeAAPI.GetOpenProfitOrderDetailsAsync(mData.SymbolB,OrderType.Stop));
                        foreach (ExchangeOrderResult re in profitA1)
                        {
                            if (re.Result == ExchangeAPIOrderResult.Pending)
                            {
                                profitOrderA1 = re;
                                break;
                            }
                        }
                        foreach (ExchangeOrderResult re in profitA2)
                        {
                            if (re.Result == ExchangeAPIOrderResult.Pending)
                            {
                                profitOrderA2 = re;
                                break;
                            }
                        }
                        foreach (ExchangeOrderResult re in profitB)
                        {
                            if (re.Result == ExchangeAPIOrderResult.Pending)
                            {
                                profitOrderB = re;
                                break;
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Logger.Error(Utils.Str2Json("GetOpenOrderDetailsAsync ex", ex.ToString()));
                        mExchangePending = false;
                        continue;
                    }
                    async Task<ExchangeOrderResult> doProfitAsync(ExchangeOrderRequest request, ExchangeOrderResult lastResult,decimal curPrice)
                    {
                        if (request.Amount==0)
                        {
                            return null;
                        }
                        if (lastResult != null)
                        {
                            if (lastResult.IsBuy != request.IsBuy)//方向不同取消
                            {
                                await mExchangeAAPI.CancelOrderAsync(lastResult.OrderId);
                                lastResult = null;
                            }
                            else
                            {
                                if (lastResult.Amount == request.Amount && Math.Abs(lastResult.StopPrice - request.StopPrice) < 10)//数量相同并且止盈价格变动不大 ，不修改
                                    return null;
                                request.ExtraParameters.Add("orderID", lastResult.OrderId);
                            }
                        }
                        if (request.StopPrice > curPrice * 3 || request.StopPrice < curPrice * 0.33m)//如果止盈点价格>三倍当前价格那么不挂止盈单
                        {
                            if (lastResult != null)
                                await mExchangeAAPI.CancelOrderAsync(lastResult.OrderId);
                            return null;
                        }
                        request.ExtraParameters.Add("execInst", "Close,LastPrice");
                        for (int i = 0; ;)
                        {
                            try
                            {
                                Logger.Debug(Utils.Str2Json("request profit", request.ToStopString()));
                                var orderResults = await mExchangeAAPI.PlaceOrdersAsync(request);
                                ExchangeOrderResult result = orderResults[0];
                                return result;
                            }
                            catch (System.Exception ex)
                            {
                                if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Bad Gateway"))
                                {
                                    await Task.Delay(2000);
                                }
                                else
                                {
                                    Logger.Error(Utils.Str2Json("doProfitAsync ex", ex.ToString()));
                                    throw ex;
                                }
                            }
                        }
                    }
                    //eth永续 bid1/(ethu19 bid1 * xbtusd ask1)-1   （A买B卖）
                    bool aBuy = posA1.Amount > 0;
                    decimal curPriceA1 = posA1.BasePrice;
                    decimal curPriceA2 = posA2.BasePrice;
                    decimal curPriceB = posB.BasePrice;
                    decimal stopA1 = Math.Abs(posA1.BasePrice - posA1.LiquidationPrice);
                    decimal stopA2 = Math.Abs(posA2.BasePrice - posA2.LiquidationPrice);
                    decimal stopB = Math.Abs(posB.BasePrice - posB.LiquidationPrice);
                    ExchangeOrderRequest orderA1 = new ExchangeOrderRequest()
                    {
                        MarketSymbol = mData.SymbolA1,
                        IsBuy = !aBuy,
                        Amount = Math.Abs(posA1.Amount),
                        StopPrice = NormalizationMinUnit(posA1.LiquidationPrice + (aBuy == true ? stopA1 * 0.2m : -stopA1 * 0.2m), mData.MinPriceUnitA1),
                        OrderType = OrderType.Stop,
                    };
                    profitOrderA1 = await doProfitAsync(orderA1, profitOrderA1,curPriceA1);
                    ExchangeOrderRequest orderA2 = new ExchangeOrderRequest()
                    {
                        MarketSymbol = mData.SymbolA2,
                        IsBuy = !aBuy,
                        Amount = Math.Abs(posA2.Amount),
                        StopPrice = NormalizationMinUnit(posA2.LiquidationPrice + (aBuy == true ? stopA2 * 0.2m : -stopA2 * 0.2m), mData.MinPriceUnitA2),
                        OrderType = OrderType.Stop,
                    };
                    profitOrderA2 = await doProfitAsync(orderA2, profitOrderA2, curPriceA2);
                    ExchangeOrderRequest orderB = new ExchangeOrderRequest()
                    {
                        MarketSymbol = mData.SymbolB,
                        IsBuy = aBuy,
                        Amount = Math.Abs(posB.Amount),
                        StopPrice = NormalizationMinUnit(posB.LiquidationPrice + (aBuy == false ? stopB * 0.2m : -stopB * 0.2m), mData.MinPriceUnitB),
                        OrderType = OrderType.Stop,
                    };
                    profitOrderB = await doProfitAsync(orderB, profitOrderB, curPriceB);
                }
                mExchangePending = false;
                await Task.Delay(5 * 60 * 1000);
            }
        }
        #endregion
        private void OnProcessExit(object sender, EventArgs e)
        {
            Logger.Debug("------------------------ OnProcessExit ---------------------------");
            mExchangePending = true;
            if (mCurOrderAResult != null)
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
            var amount = await exchange.GetWalletSummaryAsync(symbol);
            return amount;
        }
        /// <summary>
        /// 根据当前btc数量修改最大购买数量
        /// </summary>
        private async void ChangeMaxCount()
        {
            while (true)
            {
                if (!OnConnect())
                {
                    await Task.Delay(5 * 1000);
                    continue;
                }
                await CountDiffGridMaxCount();
                await Task.Delay(1 * 3600 * 1000);
            }
        }
        private async Task CountDiffGridMaxCount()
        {
            //不计算动态计算最大数量
            return;
            if (mData.AutoCalcMaxPosition)
            {
                try
                {
                    decimal noUseBtc = await GetAmountsAvailableToTradeAsync(mExchangeAAPI, "XBt") / 100000000;
                    decimal allBtc = noUseBtc;
                    //总仓位 = 总btc数量*（z19+u19）/2 *3倍杠杆/2种合约 
                    decimal allPosition = allBtc * mData.Leverage / (mOrderBookA1.Bids.FirstOrDefault().Value.Price*3);
                    allPosition = Math.Round(allPosition / mData.PerTrans) * mData.PerTrans;
                    decimal lastPosition = 0;
                    foreach (Diff diff in mData.DiffGrid)
                    {
                        lastPosition += allPosition * diff.Rate;
                        lastPosition = Math.Round(lastPosition / mData.PerTrans) * mData.PerTrans;
                        diff.MaxA1SellAmount = mData.OpenPositionSellA ? lastPosition : 0;
                        diff.MaxA1BuyAmount = mData.OpenPositionBuyA ? lastPosition : 0;
                    }
                    mData.SaveToDB(mDBKey);
                    Logger.Debug(Utils.Str2Json("noUseBtc", noUseBtc, "allPosition", allPosition));
                }
                catch (System.Exception ex)
                {
                    Logger.Error("ChangeMaxCount ex" + ex.ToString());
                }
            }
        }
        private void OnOrderbookA1Handler(ExchangeOrderBook order)
        {
                mOrderBookA1wsCounter++;
                mOrderBookA1 = order;
                OnOrderBookHandler();
        }
        private void OnOrderbookA2Handler(ExchangeOrderBook order)
        {
                mOrderBookA2wsCounter++;
                mOrderBookA2 = order;
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
            bool canDo = false;
            lock(mLockObj)
            {
                canDo = Precondition();
                if (canDo)
                {
                    mExchangePending = true;
                }
            }
            if (canDo)
            {
                //mExchangePending = true;
                Options temp = Options.LoadFromDB<Options>(mDBKey);
                Options last_mData = mData;
                if (mCurOrderAResult == null)//避免多线程读写错误
                    mData = temp;
                else
                {
                    temp.CurA1Amount = mCurA1Amount;
                    mData = temp;
                }
                if (last_mData.OpenPositionBuyA != mData.OpenPositionBuyA || last_mData.OpenPositionSellA != mData.OpenPositionSellA)//仓位修改立即刷新
                    CountDiffGridMaxCount();
                await Execute();
                await Task.Delay(mData.IntervalMillisecond);
                mExchangePending = false;
            }
        }
        private bool Precondition()
        {
            if (!OnConnect())
                return false;
            if (mOrderBookA1 == null|| mOrderBookA2 == null || mOrderBookB == null)
                return false;
            if (mRunningTask != null)
                return false;
            if (mExchangePending)
                return false;
            if (mOrderBookA1.Asks.Count == 0 || mOrderBookA1.Bids.Count == 0 || mOrderBookA2.Asks.Count == 0 || mOrderBookA2.Bids.Count == 0 || mOrderBookB.Bids.Count == 0 || mOrderBookB.Asks.Count == 0)
                return false;
            return true;
        }
        private async Task Execute()
        {
            decimal buyPriceA, sellPriceA;
            decimal buyPriceA1, sellPriceA1;
            decimal buyPriceA2, sellPriceA2;
            decimal sellPriceB, buyPriceB;
            decimal bidAAmount, askAAmount, bidBAmount, askBAmount;
            decimal a2bDiff = 0;
            decimal b2aDiff = 0;
            decimal buyAmount = mData.PerTrans;
            if (Precondition())
            {
                return;
            }
            lock (mOrderBookA1)//现在A也是市价
            {
                buyPriceA1 = mOrderBookA1.Asks.ElementAtOrDefault(0).Value.Price;
                sellPriceA1 = mOrderBookA1.Bids.ElementAtOrDefault(2).Value.Price;
                bidAAmount = mOrderBookA1.Bids.FirstOrDefault().Value.Amount;
                askAAmount = mOrderBookA1.Asks.FirstOrDefault().Value.Amount;
            }
            lock (mOrderBookA2)
            {
                buyPriceA2 = mOrderBookA2.Asks.ElementAtOrDefault(2).Value.Price;
                sellPriceA2 = mOrderBookA2.Bids.FirstOrDefault().Value.Price;
            }
            lock (mOrderBookB)
            {
                buyPriceB = mOrderBookB.Bids.ElementAtOrDefault(1).Value.Price;
                sellPriceB = mOrderBookB.Asks.ElementAtOrDefault(1).Value.Price;
                bidBAmount = mOrderBookB.Bids.FirstOrDefault().Value.Amount;
                askBAmount = mOrderBookB.Asks.FirstOrDefault().Value.Amount;
            }
            //有可能orderbook bids或者 asks没有改变
            if (buyPriceA1 != 0 && sellPriceA1 != 0 && buyPriceA2 != 0 && sellPriceA2 != 0 && sellPriceB != 0 && buyPriceB != 0 && buyAmount != 0)
            {    // eth永续= B ，(ethu19  * xbtusd ) =A  
                a2bDiff = buyPriceB / (buyPriceA1 * buyPriceA2 ) - 1;//eth永续 bid1/(ethu19 bid1 * xbtusd ask1)-1   （A买B卖）
                b2aDiff = sellPriceB /(sellPriceA1 * sellPriceA2)  - 1;//eth永续 ask1/(ethu19 ask1 * xbtusd bid1)-1   （A卖B买）
                Diff diff = GetDiff(a2bDiff, b2aDiff, out buyAmount);
                Logger.Debug(Utils.Str2Json("eth永续", buyPriceB, "ethu19", buyPriceA1, "xbtusd", buyPriceA2));
                PrintInfo( sellPriceA1, buyPriceA1,  sellPriceA2, buyPriceA2,  buyPriceB, sellPriceB,a2bDiff, b2aDiff, diff.A2BDiff, diff.B2ADiff, buyAmount, bidAAmount, askAAmount, bidBAmount, askBAmount);
               //return;
                if (b2aDiff < diff.B2ADiff ) //满足差价并且当前B空仓数量小于最大B空仓数量
                {
                    mOnTrade = true;
                    if (mCurA1Amount > 0)//平仓，平仓数量按当前份数计算 B：x 个，A1 n个，A2 m个 .... A1平仓数量= n/( x/2) B1平仓数量  A2平仓数量 =m/( x/2)
                    {
                        if (Math.Abs(mCurA1Amount) <= buyAmount)//平仓的最后一次 ，所有数量平完
                        {
                            buyAmount = Math.Abs(mCurA1Amount);
                            mChangeAmountA2 = Math.Abs(mCurA2Amount);
                            mChangeAmountB = Math.Abs(mCurBAmount);
                        }
                        else
                        {
                            mChangeAmountA2 = Math.Ceiling(Math.Abs(mCurA2Amount / (mCurA1Amount / buyAmount)));//  A2/(A1/per)
                            mChangeAmountB = Math.Ceiling(Math.Abs(mCurBAmount / (mCurA1Amount / buyAmount)));//  B/(A1/per)
                        }
                    }
                    else//开仓
                    {
                        buyAmount = buyAmount;
                        mChangeAmountA2 = NormalizationMinUnit(((buyAmount * sellPriceA1) * sellPriceA2), mData.MinPriceUnitA2);
                        mChangeAmountB = NormalizationMinUnit(((buyAmount * sellPriceA1) / (sellPriceB * mData.FactorA2)), mData.MinPriceUnitB);
                    }
                    mChangeAmountA1 = buyAmount;
                    Logger.Debug(Utils.Str2Json("buyAmount", buyAmount, "mChangeAmountA2", mChangeAmountA2, "mChangeAmountB", mChangeAmountB));
                    mRunningTask = ASellBBuy(sellPriceA1, buyAmount, false);
                    Logger.Debug(Utils.Str2Json("idea", "Price", "sellPriceA1", sellPriceA1, "sellPriceA2", sellPriceA2, "sellPriceB", sellPriceB, "b2aDiff", b2aDiff));
                }
                else if (a2bDiff > diff.A2BDiff) //满足差价并且没达到最大数量
                {
                    mOnTrade = true;
                    if (mCurA1Amount < 0)//平仓，平仓数量按当前份数计算 B：x 个，A1 n个，A2 m个 .... A1平仓数量= n/( x/2) B1平仓数量  A2平仓数量 =m/( x/2)
                    {
                        if (Math.Abs(mCurA1Amount) <= buyAmount)//平仓的最后一次 ，所有数量平完
                        {
                            buyAmount = Math.Abs(mCurA1Amount);
                            mChangeAmountA2 = Math.Abs(mCurA2Amount);
                            mChangeAmountB = Math.Abs(mCurBAmount);
                        }
                        else
                        {
                            mChangeAmountA2 = Math.Ceiling(Math.Abs(mCurA2Amount / (mCurA1Amount / buyAmount)));//  A2/(A1/per)
                            mChangeAmountB = Math.Ceiling(Math.Abs(mCurBAmount / (mCurA1Amount / buyAmount)));//  B/(A1/per)
                        }
                    }
                    else//开仓
                    {
                        buyAmount = buyAmount;
                        mChangeAmountA2 = NormalizationMinUnit(((buyAmount * buyPriceA1) * buyPriceA2), mData.MinPriceUnitA2);
                        mChangeAmountB = NormalizationMinUnit(((buyAmount * buyPriceA1) / (buyPriceB * mData.FactorA2)), mData.MinPriceUnitB);
                    }
                    mChangeAmountA1 = buyAmount;
                    Logger.Debug(Utils.Str2Json("buyAmount", buyAmount, "mChangeAmountA2", mChangeAmountA2, "mChangeAmountB", mChangeAmountB));
                    mRunningTask = ABuyBSell(buyPriceA1, buyAmount, false);
                    Logger.Debug(Utils.Str2Json("idea", "Price", "buyPriceA1", buyPriceA1, "buyPriceA2", buyPriceA2, "buyPriceB", buyPriceB, "a2bDiff", a2bDiff));

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
                        Logger.Error(Utils.Str2Json("mRunningTask ex", ex));
                        if (ex.ToString().Contains("Invalid orderID") || ex.ToString().Contains("Not Found"))
                            mCurOrderAResult = null;
                        mRunningTask = null;
                    }
                }
            }
        }
        /// <summary>
        /// 网格，分为n段。从小范围到大范围，如果当前方向为开仓，并且小于当前最大开仓数量 那么设置diff为本段值
        /// </summary>
        /// <param name="a2bDiff"></param>
        /// <param name="b2aDiff"></param>
        private Diff GetDiff(decimal a2bDiff, decimal b2aDiff, out decimal buyAmount)
        {
            buyAmount = mData.PerTrans;
            List<Diff> diffList;
            lock (mData.DiffGrid)
            {
                diffList = new List<Diff>(mData.DiffGrid);
            }
            Diff returnDiff = diffList[0];
            foreach (var diff in diffList)
            {
                returnDiff = diff;
                if (b2aDiff < diff.B2ADiff && -mCurA1Amount < diff.MaxA1SellAmount)
                {
                    if ((mCurA1Amount + mData.ClosePerTrans) <= 0)
                        buyAmount = mData.ClosePerTrans;
                    break;
                }
                else if (a2bDiff > diff.A2BDiff && mCurA1Amount < diff.MaxA1BuyAmount)
                {
                    if ((mCurA1Amount - mData.ClosePerTrans) >= 0)
                        buyAmount = mData.ClosePerTrans;
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
                        decimal avgDiff;
                        JObject jsonResult = await Utils.GetHttpReponseAsync(url);

                        if (jsonResult["status"].ConvertInvariant<int>() == 1)
                        {
                            avgDiff = jsonResult["data"]["value"].ConvertInvariant<decimal>();
                            avgDiff = Math.Round(avgDiff, 4);//强行转换
                            foreach (var diff in mData.DiffGrid)
                            {
                                diff.A2BDiff = avgDiff + diff.ProfitRange + mData.DeltaDiff;
                                diff.B2ADiff = avgDiff - diff.ProfitRange + mData.DeltaDiff;
                                mData.SaveToDB(mDBKey);
                            }
                            Logger.Debug(Utils.Str2Json(" UpdateAvgDiffAsync avgDiff", avgDiff));
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
        private void PrintInfo(decimal bidA1, decimal askA1, decimal bidA2, decimal askA2, decimal bidB, decimal askB, decimal a2bDiff, decimal b2aDiff, decimal A2BDiff, decimal B2ADiff, decimal buyAmount,
            decimal bidAAmount, decimal askAAmount, decimal bidBAmount, decimal askBAmount)
        {
            Logger.Debug("================================================");
            Logger.Debug(Utils.Str2Json("BA价差当前百分比↑", a2bDiff.ToString(), "BA价差百分比↑", A2BDiff.ToString()));
            Logger.Debug(Utils.Str2Json("BA价差当前百分比↓", b2aDiff.ToString(), "BA价差百分比↓", B2ADiff.ToString()));
            Logger.Debug(Utils.Str2Json("Bid A1", bidA1, "Bid A2", bidA2, " Bid B", bidB, "bidAAmount", bidAAmount, "bidBAmount", bidBAmount));
            Logger.Debug(Utils.Str2Json("Ask A1", askA1, "Ask A2", askA2, " Ask B", askB, "askAAmount", askAAmount, "askBAmount", askBAmount));
            Logger.Debug(Utils.Str2Json("mCurA1Amount", mCurA1Amount, "mCurA2Amount", mCurA2Amount, "mCurBAmount", mCurBAmount, " buyAmount", buyAmount));
        }
        /// <summary>
        /// 当curAmount 小于 0的时候就是平仓
        /// A买B卖
        /// </summary>
        private async Task ABuyBSell(decimal buyPrice, decimal buyAmount, bool isLimit = true)
        {
            await AddOrder2Exchange(true, mData.SymbolA1, buyPrice, buyAmount, isLimit);
        }
        /// <summary>
        /// 当curAmount大于0的时候就是开仓
        /// B买A卖
        /// </summary>
        /// <param name="exchangeAmount"></param>
        private async Task ASellBBuy(decimal sellPrice, decimal buyAmount,bool isLimit = true)
        {
            await AddOrder2Exchange(false, mData.SymbolA1, sellPrice, buyAmount,  isLimit);
        }
        private async Task AddOrder2Exchange(bool isBuy, string symbol, decimal buyPrice, decimal buyAmount,bool isLimit = true)
        {
            //A限价买
            ExchangeOrderRequest requestA = new ExchangeOrderRequest();
            requestA.Amount = buyAmount;
            requestA.MarketSymbol = symbol;
            requestA.IsBuy = isBuy;
            if (isLimit)
            {
                requestA.OrderType = OrderType.Limit;
                requestA.Price = NormalizationMinUnit(buyPrice, mData.MinPriceUnitA1);
                requestA.ExtraParameters.Add ( "execInst", "ParticipateDoNotInitiate") ;
                //ExtraParameters = { { "displayQty", 0 }, { "execInst", "ParticipateDoNotInitiate,AllOrNone" } }
            }
            else
            {
                requestA.OrderType = OrderType.Market;
            }
            bool isAddNew = true;
            bool hadCancelOrder = false;
            try
            {
                if (mCurOrderAResult != null)
                {
                    //如果已经挂单 市价那么不提交订单
                    if (mCurOrderType == OrderType.Market)
                    {
                        return;
                    }
                    else if (requestA.OrderType == OrderType.Market)//如果新单是市价并且有挂单，那么取消挂单
                    {
                        await CancelCurOrderA();
                        hadCancelOrder = true;
                    }
                    else if (mCurOrderAResult.IsBuy == requestA.IsBuy)//方向相同，并且达到修改条件
                    {
                        isAddNew = false;
                        requestA.ExtraParameters.Add("orderID", mCurOrderAResult.OrderId);
                        //检查是否有改动必要
                        //做多涨价则判断
                        if (requestA.Price == mCurOrderAResult.Price)
                        {
                            return;
                        }
                    }
                    else
                    {//如果方向相反那么直接取消
                        await CancelCurOrderA();
                        hadCancelOrder = true;
                    }
                    //如果已出现部分成交并且需要修改价格，则取消部分成交并重新创建新的订单
                    if (!hadCancelOrder)//如果上面已经撤销订单，就不用继续撤销，否则可能mCurOrderAResult =null 抛错
                    {
                        if (mFilledPartiallyDic.Keys.Contains(mCurOrderAResult.OrderId))
                        {
                            await CancelCurOrderA();
                            if (requestA.ExtraParameters.Keys.Contains("orderID"))
                                requestA.ExtraParameters.Remove("orderID");
                        }
                    }
                };
                //requestA.Price = 1000;
                async Task<ExchangeOrderResult>doMart()
                {
                    for (int i = 1; ; i++)//当B交易所也是bitmex， 防止bitmex overload一直提交到成功
                    {
                        try
                        {
                            var result = await mExchangeBAPI.PlaceOrderAsync(requestA);
                            Logger.Debug("--------------------------------AddOrder2Exchange Result-------------------------------------");
                            Logger.Debug(result.ToString());
                            return result;
                        }
                        catch (Exception ex)
                        {
                            if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("403 Forbidden") || ex.ToString().Contains("Bad Gateway"))
                            {
                                Logger.Error(Utils.Str2Json("requestA", requestA.ToStringInvariant(), "doMart 已知错误", ex));
                                await Task.Delay(1000);
                            }
                            else
                            {
                                Logger.Error(Utils.Str2Json("AddOrder2Exchange抛错", ex.ToString()));
                                Environment.Exit(0);
                                throw ex;
                            }
                        }
                    }
                }//顺序执行
                List<ExchangeOrderResult> reverseOrderTask = null;
                ExchangeOrderResult orderA1Task =  null;
                if (isBuy)
                {//1、初始有btc 2、买ethbtc 得到eth(A1)  3、卖ethusdt得到usdt(B) 4、买btcusdt 得到btc(A2)
                    requestA.Amount = FloorMinUnit(mCurBasicCoinAmount/ mOrderBookA1.Asks.ElementAtOrDefault(0).Value.Price, mData.MinPriceUnitA1);
                    orderA1Task = await doMart();
                    reverseOrderTask = await OrderFiledDo(true, orderA1Task.Amount);
                    //4、卖ethbtc 得到btc(A1)
                }
                else
                {//1、初始有btc 2、卖btcusdt 得到usdt(A2) 3、买ethusdt得到eth(B) 4、卖ethbtc 得到btc(A1)
                    reverseOrderTask = await OrderFiledDo(false, mCurBasicCoinAmount);
                    ExchangeOrderResult resultB = reverseOrderTask[1];
                    //4、卖ethbtc 得到btc(A1)
                    requestA.Amount = FloorMinUnit(resultB.Amount, mData.MinPriceUnitA1);
                    orderA1Task = await doMart();
                }
                mCurOrderAResult = orderA1Task;
                ExchangeOrderResult exchangeOrderResultBack = orderA1Task;
                Logger.Debug(orderA1Task.ToExcleString());
                Logger.Debug(reverseOrderTask[0].ToExcleString());
                Logger.Debug(reverseOrderTask[1].ToExcleString());
                PrintFilledOrder(orderA1Task, reverseOrderTask[0], reverseOrderTask[1]);
                mCurOrderType = requestA.OrderType;
                mOrderIds.Add(exchangeOrderResultBack.OrderId);
                mCurOrderAResult = null;
                mOnTrade = false;
                Logger.Debug(Utils.Str2Json("requestA", requestA.ToString()));
                Logger.Debug(Utils.Str2Json("Add mCurrentLimitOrder", exchangeOrderResultBack.ToExcleString(), "CurAmount", mCurA1Amount));
                Logger.Debug(Utils.Str2Json("market Filled", "已经市价成交"));
                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                Logger.Error(Utils.Str2Json("AddOrder2Exchange 抛出", ex));
                //如果是添加新单那么设置为null 
                if (isAddNew || ex.ToString().Contains("Invalid orderID") || ex.ToString().Contains("Not Found"))
                {
                    mCurOrderAResult = null;
                    mOnTrade = false;
                }
                else if (ex.ToString().Contains("overloaded"))
                    await Task.Delay(5000);
                else if (ex.ToString().Contains("RateLimitError"))
                    await Task.Delay(30000);
                else
                {
                    Environment.Exit(0);
                    throw ex;
                }
            }
        }
        private async Task CancelCurOrderA()
        {
            ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
            cancleRequestA.ExtraParameters.Add("orderID", mCurOrderAResult.OrderId);
            //在onOrderCancle的时候处理
            await mExchangeAAPI.CancelOrderAsync(mCurOrderAResult.OrderId, mData.SymbolA1);
        }
        /// <summary>
        /// 订单成交 ，修改当前仓位和删除当前订单
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderFilled(ExchangeOrderResult order,bool must = false)
        {
//             lock (order)//先收到 order filed 在之后收到 pending 。因为order是同一个这时候会成交0张
//             {
// 
//                 Logger.Debug("-------------------- Order Filed ---------------------------");
//                 Logger.Debug(order.ToString());
//                 Logger.Debug(order.ToExcleString());
//                 //如果有重复成交订单不执行
//                 if (mOrderIdsFiled.Contains(order.OrderId))
//                 {
//                     Logger.Debug("-------------------- 有重复提交订单！！！！！！ ---------------------------" + order.OrderId);
//                     return;
//                 }
//                 mOrderIdsFiled.Add(order.OrderId);
//                 bool isFilled = false;
//                 decimal transAmount;
//                 decimal curAmountA2;
//                 decimal curAmountB;
//                 GetParTrans(order, out transAmount, out curAmountA2, out curAmountB, out isFilled);
//                 OrderFiledDo(order.IsBuy, transAmount, curAmountA2, curAmountB,order.OrderId);
//                 // 如果 当前挂单和订单相同那么删除
//                 if (mCurOrderAResult != null && (mCurOrderAResult.OrderId == order.OrderId))
//                 {
//                     mCurOrderAResult = null;
//                     mOnTrade = false;
//                 }
//             }
        }
        //1、初始有btc 2、卖btcusdt 得到usdt(A2) 3、买ethusdt得到eth(B) 
        private async Task<List<ExchangeOrderResult>> OrderFiledDo(bool orderIsBuy, decimal transAmount,string orderId = "")
        {
            List<ExchangeOrderResult> returnList = new List<ExchangeOrderResult>();
            async Task fun()
            {
                ExchangeOrderResult backResultA2 = null;
                ExchangeOrderResult backResultB = null;
                ExchangeOrderResult backResultA2Task = null;
                ExchangeOrderResult backResultBTask = null;
                if (orderIsBuy)
                {//3、卖ethusdt得到usdt(B) 4、买btcusdt 得到btc(A2)

                    backResultBTask = await ReverseOpenMarketOrder(false, mData.SymbolB, FloorMinUnit(transAmount, mData.MinPriceUnitB));//获得usdt
                    backResultA2Task = await ReverseOpenMarketOrder(true, mData.SymbolA2, FloorMinUnit(backResultBTask.Amount* backResultBTask.AveragePrice/ mOrderBookA2.Asks.ElementAtOrDefault(0).Value.Price, mData.MinPriceUnitA2) );//获得btc
                    //mCurBasicCoinAmount = backResultA2Task.Amount;
                }
                else
                {
                    //2、卖btcusdt 得到usdt(A2) 3、买ethusdt得到eth(B)
                    backResultA2Task = await ReverseOpenMarketOrder(false, mData.SymbolA2, FloorMinUnit(transAmount, mData.MinPriceUnitA2));//获得usdt
                    backResultBTask = await ReverseOpenMarketOrder(true, mData.SymbolB, FloorMinUnit(backResultA2Task.Amount* backResultA2Task.AveragePrice/mOrderBookB.Asks.ElementAtOrDefault(0).Value.Price, mData.MinPriceUnitB) );//获得eth
                    
                }
                backResultA2 = backResultA2Task;
                backResultB = backResultBTask;

//                 mCurA1Amount += orderIsBuy ? transAmount : -transAmount;
//                 mCurA2Amount += orderIsBuy ? curAmountA2 : -curAmountA2;
//                 mCurBAmount += orderIsBuy ? -curAmountB : curAmountB;
                Logger.Debug(Utils.Str2Json("mCurBasicCoinAmount:", mCurBasicCoinAmount));
                returnList.Add(backResultA2);
                returnList.Add( backResultB);
            }
            await fun();
            return returnList;
        }
        /// <summary>
        /// 订单部分成交
        /// 部分成交如果要改价不光是要取消，而且还要记录当前未成交数量，下次继续成交
        /// </summary>
        /// <param name="order"></param>
        private async Task OnFilledPartiallyAsync(ExchangeOrderResult order)
        {
            if (order.Amount == order.AmountFilled)
                return;
            Logger.Debug("-------------------- Order Filed Partially---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            mExchangePending = true;
            bool isFilled = false;
            decimal transAmount;
            decimal curAmountA2;
            decimal curAmountB;
            GetParTrans(order,out transAmount,out curAmountA2,out curAmountB ,out isFilled);
            ExchangeOrderResult backResultA2 = await ReverseOpenMarketOrder( order.IsBuy, mData.SymbolA2, curAmountA2);
            ExchangeOrderResult backResultB = await ReverseOpenMarketOrder( !order.IsBuy, mData.SymbolB, curAmountB);
            mCurA1Amount += order.IsBuy ? transAmount : -transAmount;
            mCurA2Amount += order.IsBuy ? curAmountA2 : -curAmountA2;
            mCurBAmount += order.IsBuy ? -curAmountB : curAmountB;
            Logger.Debug(Utils.Str2Json("mCurA1Amount:", transAmount, "curAmountA2:", curAmountA2, "curAmountB:", curAmountB));
            mExchangePending = false;
            PrintFilledOrder(order, backResultA2,backResultB);
        }
        private decimal PrintFilledOrder(ExchangeOrderResult orderA, ExchangeOrderResult backOrderA2, ExchangeOrderResult backOrderB)
        {
            if (orderA == null)
                return 0;
            if (backOrderB == null)
                return 0;
            try
            {
                Logger.Debug("--------------PrintFilledOrder--------------");
                Logger.Debug(Utils.Str2Json("filledTime", Utils.GetGMTimeTicks(orderA.OrderDate).ToString(),
                    "direction", orderA.IsBuy ? "buy" : "sell",
                    "orderData", orderA.ToExcleString()));

                    DateTime dt = backOrderB.OrderDate.AddHours(8);
                decimal realDiff = (backOrderB.AveragePrice / (orderA.AveragePrice * backOrderA2.AveragePrice) - 1);
                    List<string> strList = new List<string>()
                    {
                        //eth永续 bid1/(ethu19 bid1 * xbtusd ask1)-1
                        dt.ToShortDateString()+"/"+dt.ToLongTimeString(),orderA.IsBuy ? "buy" : "sell",orderA.Amount.ToString(), realDiff.ToString()
                    };
                    Utils.AppendCSV(new List<List<string>>() { strList }, Path.Combine(Directory.GetCurrentDirectory(), "ClosePosition.csv"), false);
                return realDiff;
               // }
            }
            catch (Exception ex)
            {
                Logger.Error("PrintFilledOrder" + ex);
            }
            return 0;
        }
        /// <summary>
        /// 订单取消，删除当前订单
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderCanceled(ExchangeOrderResult order)
        {
            Logger.Debug("-------------------- Order Canceled ---------------------------");
            Logger.Debug("Canceled  " + order.ToExcleString() + "CurAmount" + mCurA1Amount);
            if (mCurOrderAResult != null && mCurOrderAResult.OrderId == order.OrderId)
            {
                mCurOrderAResult = null;
                mOnTrade = false;
            }
        }
        /// <summary>
        /// 当A交易所的订单发生改变时候触发
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderAHandler(ExchangeOrderResult order)
        {
            mOrderDetailsAwsCounter++;
            if (order.MarketSymbol.Equals("pong"))
            {
                Logger.Debug("pong");
                return;
            }
            Logger.Debug("-------------------- OnOrderAHandler ---------------------------");
            if (order.Result == ExchangeAPIOrderResult.FilledPartially || order.Result == ExchangeAPIOrderResult.Filled)
            {
                if (order.StopPrice > 0 && order.Amount > 0 &&( order.MarketSymbol == mData.SymbolA1 || order.MarketSymbol == mData.SymbolA2|| order.MarketSymbol == mData.SymbolB))
                {
                    Task<ExchangeOrderResult> t1 = null;
                    Task<ExchangeOrderResult>  t2 = null;
                    Task<ExchangeOrderResult> t3 = null;
                    ExchangeOrderResult or = new ExchangeOrderResult();
                    List<Task<ExchangeOrderResult>> ary = new List<Task<ExchangeOrderResult>>();
                    if (order.MarketSymbol != mData.SymbolA1)
                    {
                        t1 = ReverseOpenMarketOrder( mCurA1Amount<0, mData.SymbolA1, Math.Abs(mCurA1Amount));
                        ary.Add(t1);
                    }
                    if(order.MarketSymbol != mData.SymbolA2)
                    {
                        t2 = ReverseOpenMarketOrder( mCurA2Amount < 0, mData.SymbolA2, Math.Abs(mCurA2Amount));
                        ary.Add(t2);
                    }
                    if (order.MarketSymbol != mData.SymbolB)
                    {
                        t3 = ReverseOpenMarketOrder( mCurBAmount < 0, mData.SymbolB, Math.Abs(mCurBAmount));
                        ary.Add(t3);
                    }
                    Task.WaitAll(ary.ToArray());
                    Logger.Error("止盈触发，市价成交订单并停止程序");
                    Environment.Exit(0);
                    throw new Exception("止盈触发停止运行程序");
                }
            }
            if (order.MarketSymbol != mData.SymbolA1)
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
                    if (mCurOrderType == OrderType.Limit)//市价已经直接处理
                        OnOrderFilled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartially:
                    OnFilledPartiallyAsync(order);
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
        private void GetParTrans(ExchangeOrderResult order , out decimal changeAmountA1, out decimal changeAmountA2, out decimal changeAmountB,out bool isFilled)
        {
            Logger.Debug("-------------------- GetParTrans ---------------------------");
            decimal filledAmount = 0;
            changeAmountA1 = 0;
            isFilled = false;
            mFilledPartiallyDic.TryGetValue(order.OrderId, out filledAmount);
            Logger.Debug(" filledAmount: " + filledAmount.ToStringInvariant());
            if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount == 0)
            {
                mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                changeAmountA1 = order.AmountFilled;
            }
            else if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount != 0)
            {
                if (filledAmount < order.AmountFilled)
                {
                    mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                    changeAmountA1 = order.AmountFilled - filledAmount;
                }
                else
                    changeAmountA1 = 0;
            }
            else if (order.Result == ExchangeAPIOrderResult.Filled && filledAmount == 0)
            {
                isFilled = true;
                changeAmountA1 = order.Amount;
            }
            else if (order.Result == ExchangeAPIOrderResult.Filled && filledAmount != 0)
            {
                isFilled = true;
                mFilledPartiallyDic.Remove(order.OrderId);
                changeAmountA1 = order.Amount - filledAmount;
            }
            decimal rate = changeAmountA1 / mChangeAmountA1;
            changeAmountA2 = Math.Floor(rate * mChangeAmountA2);
            changeAmountB = Math.Floor(rate * mChangeAmountB);
            Logger.Error(Utils.Str2Json("changeAmountA1", changeAmountA1, "changeAmountA2", changeAmountA2, "changeAmountB", changeAmountB));
        }
        /// <summary>
        /// 反向市价开仓
        /// </summary>
        private async Task<ExchangeOrderResult> ReverseOpenMarketOrder(bool isBuy,string symbol,decimal changeAmount)
        {
            if (changeAmount <= 0)//部分成交返回两次一样的数据，导致第二次transAmount=0
            {   
                Logger.Debug("----------------------------ReverseOpenMarketOrder---------------------------" + symbol+ "changeAmount:"+ changeAmount);
                return null;
            } 
            ExchangeOrderResult backResult = null;
            //只有在成交后才修改订单数量
            var req = new ExchangeOrderRequest();
            req.Amount = changeAmount;
            req.IsBuy = isBuy;
            req.OrderType = OrderType.Market;
            req.MarketSymbol = symbol;
            Logger.Debug("----------------------------ReverseOpenMarketOrder---------------------------"+ symbol);
            Logger.Debug(req.ToStringInvariant());
            var ticks = DateTime.Now.Ticks;


            for (int i = 1; ; i++)//当B交易所也是bitmex， 防止bitmex overload一直提交到成功
            {
                try
                {
                    var res = await mExchangeBAPI.PlaceOrderAsync(req);
                    Logger.Debug("--------------------------------ReverseOpenMarketOrder Result-------------------------------------");
                    Logger.Debug(res.ToString());
                    backResult = res;
                    break;
                }
                catch (Exception ex)
                {
                    if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("403 Forbidden")|| ex.ToString().Contains("Bad Gateway"))
                    {
                        Logger.Error(Utils.Str2Json("req", req.ToStringInvariant(), "ReverseOpenMarketOrder 已知错误", ex));
                        await Task.Delay(1000);
                    }
                    else
                    {
                        Logger.Error(Utils.Str2Json("ReverseOpenMarketOrder抛错", ex.ToString()));
                        Environment.Exit(0);
                        throw ex;
                    }
                }
            }
            await Task.Delay(mData.IntervalMillisecond);
            return backResult;
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
            requestA.MarketSymbol = mData.SymbolA1;
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
        decimal NormalizationMinUnit(decimal price,decimal per  )
        {
            var s = 1 / per;
            return Math.Round(price * s) / s;
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
        decimal FloorMinUnit(decimal price, decimal per)
        {
            var s = 1 / per;
            return Math.Floor(price * s) / s;
        }

        public class Options
        {
            public string ExchangeNameA;
            public string ExchangeNameB;
            public string SymbolA1;
            public string SymbolA2;
            public string SymbolB;
            public string Symbol;
            public decimal DeltaDiff = 0m;
            /// <summary>
            /// 开仓差
            /// </summary>
            public List<Diff> DiffGrid = new List<Diff>();
            public decimal PerTrans;
            /// <summary>
            /// 平仓倍数，平仓是开仓的n倍
            /// </summary>
            public decimal ClosePerTrans;
            /// <summary>
            /// 最小价格单位
            /// </summary>
            public decimal MinPriceUnitA1 = 0.001m;
            /// <summary>
            /// 最小数量
            /// </summary>
            public decimal MinPriceUnitA2 = 0.000001m;
            /// <summary>
            /// 最小数量
            /// </summary>
            public decimal MinPriceUnitB = 0.00001m;
            /// <summary>
            /// 最小订单总价格
            /// </summary>
            public decimal MinOrderPrice = 0.0011m;
            public decimal FactorA2 = 0.000001m;
            public decimal FactorB = 1m;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurA1Amount = 0;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurA2Amount = 0;
            /// <summary>
            /// 当前B仓位数量
            /// </summary>
            public decimal CurBAmount = 0;
            /// <summary>
            /// 当前本位币数量，默认btc
            /// </summary>
            public decimal CurBasicCoinAmount = 0;
            /// <summary>
            /// 上一笔订单部分成交的数量
            /// </summary>
            public decimal FillPartAmount = 0;
            /// <summary>
            /// 间隔时间
            /// </summary>
            public int IntervalMillisecond = 500;
            /// <summary>
            /// 自动计算利润范围
            /// </summary>
            public bool AutoCalcProfitRange = false;
            /// <summary>
            /// 自动计算最大开仓数量
            /// </summary>
            public bool AutoCalcMaxPosition = true;
            /// <summary>
            /// 是否能开多仓
            /// </summary>
            public bool OpenPositionBuyA = true;
            /// <summary>
            /// 是否能开空仓
            /// </summary>
            public bool OpenPositionSellA = true;
            /// <summary>
            /// 杠杆倍率
            /// </summary>
            public decimal Leverage = 3.5m;
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
            public string RedisConfig = "localhost,password=l3h2p1w0*";

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
            /// B的最大空仓数量(是MaxBSellAmount/PerTrans/2 = A的数量)
            /// </summary>
            public decimal MaxA1SellAmount;
            /// <summary>
            /// B的最大多仓数量
            /// </summary>
            public decimal MaxA1BuyAmount = 0m;

            public decimal Rate = 0.5m;
        }

    }
}