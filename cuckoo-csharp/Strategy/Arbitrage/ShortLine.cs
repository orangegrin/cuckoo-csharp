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
    /// <summary>
    /// 期对期
    /// 要求 A价<B价，并且A限价开仓
    /// </summary>
    public class ShortLine
    {
        private IExchangeAPI mExchangeAAPI;
        private IWebSocket mOrderws;
        private IWebSocket mOrderBookAws;

        private int mOrderBookAwsCounter = 0;
        private int mOrderDetailsAwsCounter = 0;
        private int mId;
        /// <summary>
        /// A交易所的的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderBookA;
        /// <summary>
        /// 当前挂出去的订单
        /// </summary>
        private ExchangeOrderResult mCurOrderA;
        /// <summary>
        /// A交易所的历史订单ID
        /// </summary>
        private List<string> mOrderIds = new List<string>();
        /// <summary>
        /// A交易所止损止盈订单ID
        /// </summary>
        private string mProfitWinOrderIds = "";
        /// <summary>
        /// A交易所止损止盈订单ID
        /// </summary>
        private string mProfitLostOrderIds = "";
        /// <summary>
        /// A交易所的历史成交订单ID
        /// </summary>
        private List<string> mOrderFiledIds = new List<string>();
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
        /// <summary>
        /// 仓位开仓价格
        /// </summary>
        private decimal mBasePrice;

        private string mDBKey;
        private Task mRunningTask;
        private bool mExchangePending = false;
        private bool mOrderwsConnect = false;
        private bool mOrderBookAwsConnect = false;
        private bool mOnTrade = false;//是否在交易中
        private bool mOnConnecting = false;
        /// <summary>
        /// 等待提交订单间隔
        /// </summary>
        private bool mOnWaitTimeSpan = false;
        
        public ShortLine(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("INTERTEMPORAL:CONFIG:{0}:{1}:{2}", config.ExchangeNameA,  config.SymbolA,  id);
            RedisDB.Init(config.RedisConfig);
            mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mData.ExchangeNameA);
        }
        public void Start()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
            mExchangeAAPI.LoadAPIKeys(mData.EncryptedFileA);
            UpdateAvgDiffAsync();
            mExchangePending = false;
            CheckPosition();
            SubWebSocket();
            WebSocketProtect();
            //ChangeMaxCount();
            DoWaitTimeSpan();
        }
        #region Connect
        private void SubWebSocket()
        {
            mOnConnecting = true;
            mOrderws = mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
            mOrderws.Connected += async (socket) => { mOrderwsConnect = true; Logger.Debug("GetOrderDetailsWebSocket 连接"); OnConnect(); };
            mOrderws.Disconnected += async (socket) =>
            {
                mOrderwsConnect = false;
                WSDisConnectAsync("GetOrderDetailsWebSocket 连接断开");
            };
            //避免没有订阅成功就开始订单
            Thread.Sleep(3 * 1000);
            mOrderBookAws = mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 20, mData.SymbolA);
            mOrderBookAws.Connected += async (socket) => { mOrderBookAwsConnect = true; Logger.Debug("GetFullOrderBookWebSocket A 连接"); OnConnect(); };
            mOrderBookAws.Disconnected += async (socket) =>
            {
                mOrderBookAwsConnect = false;
                WSDisConnectAsync("GetFullOrderBookWebSocket A 连接断开");
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
                int delayTime = 60;//保证次数至少要3s一次，否则重启
                mOrderBookAwsCounter = 0;
                mOrderDetailsAwsCounter = 0;
                await Task.Delay(1000 * delayTime);
                Logger.Debug(Utils.Str2Json("mOrderBookAwsCounter", mOrderBookAwsCounter,  "mOrderDetailsAwsCounter", mOrderDetailsAwsCounter));
                bool detailConnect = true;
                if (mOrderDetailsAwsCounter == 0)
                    detailConnect = await IsConnectAsync();
                Logger.Debug(Utils.Str2Json("mOrderDetailsAwsCounter", mOrderDetailsAwsCounter));
                if (mOrderBookAwsCounter < 1 ||  (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    if (mCurOrderA != null)
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
            mOrderBookAwsConnect = false;
            await Task.Delay(5 * 1000);
            Logger.Debug("销毁ws");
            mOrderws.Dispose();
            mOrderBookAws.Dispose();
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
            bool connect = mOrderwsConnect & mOrderBookAwsConnect ;
            if (connect)
                mOnConnecting = false;
            return connect;
        }
        
        private async Task WSDisConnectAsync(string tag)
        {
            if (mCurOrderA != null)
            {
                CancelCurOrderA();
            }
            
            //删除避免重复 重连
            await Task.Delay(40 * 1000);
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
        private async void DoWaitTimeSpan()
        {
            while (true)
            {
                if (mOnWaitTimeSpan)
                {
                    int mins = Convert.ToInt16(Math.Floor(mData.TimeSpan / 60m));
                    for (int i=0;i< mins; i++)
                    {
                        Logger.Debug("还有" + (mins - i) + "分钟提交订单");
                        await Task.Delay(1000);
                    }
                    mOnWaitTimeSpan = false;
                }
                await Task.Delay(60 * 1000);
            }
        }

        private async Task MakeProfitOrder()
        {
            mExchangePending = true;
            Logger.Debug("-----------------------CheckPosition-----------------------------------");
            ExchangeMarginPositionResult posA;
            try
            {
                //等待10秒 避免ws虽然推送数据刷新，但是rest 还没有刷新数据
                await Task.Delay(10 * 1000);
                posA = await mExchangeAAPI.GetOpenPositionAsync(mData.SymbolA);
                if (posA == null)
                {
                    mExchangePending = false;
                    await Task.Delay(5 * 60 * 1000);
                    return;
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error(Utils.Str2Json("GetOpenPositionAsync ex", ex.ToString()));
                mExchangePending = false;
                await Task.Delay(1000);
                return;
            }
            decimal realAmount = posA.Amount;
            mBasePrice = posA.BasePrice;
            if (realAmount != mCurAmount)
            {
                Logger.Debug(Utils.Str2Json("差数量", realAmount - mCurAmount));
                Logger.Debug(Utils.Str2Json("Change curAmount", realAmount));
                mCurAmount = realAmount;
            }
            //==================挂止盈单================== 挂止盈止损单
            //一单为空，那么挂止盈多单，止盈价格为另一单的强平价格（另一单多+500，空-500）
            //一单为多 相反
            if (realAmount != 0)
            {
                Logger.Debug(Utils.Str2Json("挂止盈单", realAmount));
                List<ExchangeOrderResult> profitWin;
                List<ExchangeOrderResult> profitLost;
                ExchangeOrderResult profitOrderWin = null;
                ExchangeOrderResult profitOrderLost = null;
                //下拉最新的值 ，来重新计算 改开多少止盈订单
                try
                {
                    profitWin = new List<ExchangeOrderResult>(await mExchangeAAPI.GetOpenProfitOrderDetailsAsync(mData.SymbolA, OrderType.Limit));
                    profitLost = new List<ExchangeOrderResult>(await mExchangeAAPI.GetOpenProfitOrderDetailsAsync(mData.SymbolA, OrderType.MarketIfTouched));
                    profitLost.AddRange( new List<ExchangeOrderResult>(await mExchangeAAPI.GetOpenProfitOrderDetailsAsync(mData.SymbolA, OrderType.Stop)));
                    foreach (ExchangeOrderResult re in profitWin)
                    {
                        if (re.Result == ExchangeAPIOrderResult.Pending)
                        {
                            await mExchangeAAPI.CancelOrderAsync(re.OrderId, re.MarketSymbol);
                        }
                    }
                    foreach (ExchangeOrderResult re in profitLost)
                    {
                        if (re.Result == ExchangeAPIOrderResult.Pending)
                        {
                            await mExchangeAAPI.CancelOrderAsync(re.OrderId, re.MarketSymbol);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.Error(Utils.Str2Json("GetOpenOrderDetailsAsync ex", ex.ToString()));
                    mExchangePending = false;
                    await Task.Delay(1000);
                    return;
                }
                async Task<ExchangeOrderResult> doProfitAsync(ExchangeOrderRequest request, ExchangeOrderResult lastResult)
                {
                    if (lastResult != null)
                    {
                        if (lastResult.IsBuy != request.IsBuy)//方向不同取消
                        {
                            await mExchangeAAPI.CancelOrderAsync(lastResult.OrderId);
                            lastResult = null;
                        }
                        else
                        {
                            if (lastResult.Amount == request.Amount && Math.Abs(lastResult.StopPrice - request.Price) < 10)//数量相同并且止盈价格变动不大 ，不修改
                                return null;
                            request.ExtraParameters.Add("orderID", lastResult.OrderId);
                        }
                    }
                    if (request.Price > mOrderBookA.Bids.FirstOrDefault().Value.Price * 3)//如果止盈点价格>三倍当前价格那么不挂止盈单
                    {
                        if (lastResult != null)
                            await mExchangeAAPI.CancelOrderAsync(lastResult.OrderId);
                        return null;
                    }
                    //request.ExtraParameters.Add("execInst", "Close,LastPrice");
                   
                    for (int i = 0; ;)
                    {
                        try
                        {
                            Logger.Debug(Utils.Str2Json("request profit", request.ToString()));
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
                bool aBuy = mData.OpenPositionIsBuy;
                decimal curPrice;
                decimal realPrice = posA.BasePrice;
                lock (mOrderBookA)
                {
                    curPrice = mOrderBookA.Bids.FirstOrDefault().Value.Price;
                }
                decimal winPrice = Math.Floor(realPrice + mData.ProfitRange * curPrice);
                decimal lostPrice = Math.Floor(realPrice - mData.StopRange * curPrice);
                bool isError = false;
                if (aBuy)
                {
                    if (posA.LiquidationPrice >= curPrice)
                    {
                        Logger.Error(" lostPrice标记价格错误: 当前价格A" + curPrice + "  当前数量：" + mCurAmount);
                        isError = true;
                    }
                    Logger.Error(" posA.LiquidationPrice:" + posA.LiquidationPrice);
                }
                else
                {
                    if (posA.LiquidationPrice <= curPrice)
                    {
                        Logger.Error(" lostPrice标记价格错误: 当前价格A" + curPrice + "  当前数量：" + mCurAmount);
                        isError = true;
                    }
                    Logger.Error(" posA.LiquidationPrice:" + posA.LiquidationPrice);
                }
                if (!isError)
                {
                    decimal lastAmount = Math.Abs(realAmount);
                    ExchangeOrderRequest orderWin = new ExchangeOrderRequest()
                    {
                        MarketSymbol = mData.SymbolA,
                        IsBuy = !aBuy,
                        Amount = Math.Abs(realAmount),
                        Price = (aBuy == false) ? lostPrice : winPrice,
                        OrderType = OrderType.Limit,
                    };
                    orderWin.Amount = lastAmount;
                    orderWin.ExtraParameters.Add("execInst", "Close");
                    profitOrderWin = await doProfitAsync(orderWin, null);
                    if (profitOrderWin != null)
                        mProfitWinOrderIds = profitOrderWin.OrderId;
                    ExchangeOrderRequest orderLost = new ExchangeOrderRequest()
                    {
                        MarketSymbol = mData.SymbolA,
                        IsBuy = !aBuy,
                        Amount = Math.Abs(realAmount),
                        StopPrice = (aBuy == true) ? lostPrice : winPrice,
                    };
                    orderLost.OrderType = OrderType.Stop;
                    orderLost.Amount = lastAmount;
                    orderLost.ExtraParameters.Add("execInst", "Close,LastPrice");
                    profitOrderWin = await doProfitAsync(orderLost, null);
                    if (profitOrderWin != null)
                        mProfitLostOrderIds = profitOrderWin.OrderId;
                }
            }
            mExchangePending = false;
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
                if (mCurOrderA != null || mOnTrade || mExchangePending == true)//交易正在进行或者，准备开单。检查数量会出现问题
                {
                    await Task.Delay(200);
                    continue;
                }
                await MakeProfitOrder();
                await Task.Delay(5 * 60 * 1000);
            }          
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
                await Task.Delay(2*3600 * 1000);
            }
        }
        private async Task CountDiffGridMaxCount()
        {
            /*
            if (mData.AutoCalcMaxPosition)
            {
                try
                {
                    decimal noUseBtc = await GetAmountsAvailableToTradeAsync(mExchangeAAPI, "XBt") / 100000000;
                    decimal allBtc = noUseBtc;
                    //总仓位 = 总btc数量*（z19+u19）/2 *3倍杠杆/2种合约 
                    decimal allPosition = allBtc * (mOrderBookA.Bids.FirstOrDefault().Value.Price + mOrderBookB.Asks.FirstOrDefault().Value.Price) / 2 * mData.Leverage / 2;
                    allPosition = Math.Round(allPosition / mData.PerTrans) * mData.PerTrans;
                    decimal lastPosition = 0;
                    foreach (Diff diff in mData.DiffGrid )
                    {
                        lastPosition += allPosition * diff.Rate;
                        lastPosition = Math.Round(lastPosition / mData.PerTrans) * mData.PerTrans;
                        diff.MaxAmount = mData.OpenPositionSellA ? lastPosition : 0;
                        diff.InitialExchangeBAmount = mData.OpenPositionBuyA ? lastPosition : 0;
                    }
                    mData.SaveToDB(mDBKey);
                    Logger.Debug(Utils.Str2Json("noUseBtc", noUseBtc, "allPosition", allPosition));
                }
                catch (System.Exception ex)
                {
                    Logger.Error("ChangeMaxCount ex" + ex.ToString());
                }
            }
            */
        }
        private void OnOrderbookHandler(ExchangeOrderBook order)
        {
            if (order.MarketSymbol == mData.SymbolA)
                OnOrderbookAHandler(order);
        }

        private void OnOrderbookAHandler(ExchangeOrderBook order)
        {
            mOrderBookAwsCounter++;
            mOrderBookA = order;
            OnOrderBookHandler();
        }
        async void OnOrderBookHandler()
        {
            if (Precondition())
            {
                mExchangePending = true;
                Options temp = Options.LoadFromDB<Options>(mDBKey);
                Options last_mData = mData;
                if (mCurOrderA==null)//避免多线程读写错误
                    mData = temp;
                else
                {
                    temp.CurAmount = mData.CurAmount;
                    mData = temp;
                }
                if (last_mData.OpenPositionIsBuy != mData.OpenPositionIsBuy )//仓位修改立即刷新
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
            if (mOrderBookA == null )
                return false;
            if (mRunningTask != null)
                return false;
            if (mExchangePending)
                return false;
            if (mOnWaitTimeSpan)
                return false;
            if (mOrderBookA.Asks.Count == 0 || mOrderBookA.Bids.Count == 0 )
                return false;
            if (!mData.OnTrade)
                return false;
            return true;
        }
        private async Task Execute()
        {
            decimal buyPriceA;
            decimal sellPriceA;
            decimal sellPriceB = 0;
            decimal buyPriceB = 0;
            decimal bidAAmount, askAAmount, bidBAmount, askBAmount;
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
            //有可能orderbook bids或者 asks没有改变
            if (buyPriceA != 0 && sellPriceA != 0 &&  buyAmount != 0)
            {
                //PrintInfo(buyPriceA, sellPriceA, sellPriceB, buyPriceB, 0, 0, 0, 0, buyAmount, bidAAmount, askAAmount, 0, 0);
                //如果盘口差价超过4usdt 不进行挂单，但是可以改单（bitmex overload 推送ws不及时）
//                 if (mCurOrderA == null && ((sellPriceA <= buyPriceA) || (sellPriceA - buyPriceA >= 4) || (sellPriceB <= buyPriceB) || (sellPriceB - buyPriceB >= 4)))
//                 {
//                     Logger.Debug("范围更新不及时，不纳入计算");
//                     return;
//                 }
                //return;
                //满足差价并且
                //只能BBuyASell来开仓，也就是说 ABuyBSell只能用来平仓
                if (mData.OpenPositionIsBuy && mData.CurAmount + mData.PerTrans <= mData.MaxSellAmount) //满足差价并且当前A空仓
                {
                    mOnTrade = true;
                    mRunningTask = A2BExchange(buyPriceA, buyAmount);
                }
                else if (mData.OpenPositionIsBuy==false && -mCurAmount < mData.MaxBuyAmount) //满足差价并且没达到最大数量
                {
                    mOnTrade = true;
                    mRunningTask = B2AExchange(sellPriceA, buyAmount);
                }
                else if (mCurOrderA != null )//如果在波动区间中，那么取消挂单
                {
                    Logger.Debug(Utils.Str2Json("在波动区间中取消订单" , "cancleID", mCurOrderA.OrderId));
                    ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                    cancleRequestA.ExtraParameters.Add("orderID", mCurOrderA.OrderId);
                    try
                    {
                        mRunningTask = mExchangeAAPI.CancelOrderAsync(mCurOrderA.OrderId, mData.SymbolA);
                        await Task.Delay(3500);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(Utils.Str2Json("CancelOrderAsync ex", ex));
                        mRunningTask = null;
                    }
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
                            mCurOrderA = null;
                        mRunningTask = null;
                    }
                }
            }
        }

        /// <summary>
        /// 刷新差价
        /// </summary>
        public async Task UpdateAvgDiffAsync()
        {
            string dataUrl = $"{"http://150.109.52.225:8006/arbitrage/process?programID="}{mId}{"&symbol="}{mData.Symbol}{"&exchangeB="}{mData.ExchangeNameA.ToLowerInvariant()}{"&exchangeA="}{mData.ExchangeNameA.ToLowerInvariant()}";
            Logger.Debug(dataUrl);
            bool OnTradeFlag = true;
            bool isFirst = true;
            while (true)
            {
                try
                {
                    lock(mData)
                    {
                        mData = Options.LoadFromDB<Options>(mDBKey);
                    }
                    //bool lastOpenPositionSellA = mData.OpenPositionSellA;
                    JObject jsonResult = await Utils.GetHttpReponseAsync(dataUrl);
                    //mData.DeltaDiff = jsonResult["deltaDiff"].ConvertInvariant<decimal>();
                    mData.Leverage = jsonResult["leverage"].ConvertInvariant<decimal>();
                    bool OnTrade = jsonResult["openPositionBuyA"].ConvertInvariant<int>() == 0 ? false : true;
                    if (isFirst)
                    {
                        isFirst = false;
                    }
                    else
                    {
                        if (OnTradeFlag!= OnTrade)
                        {
                            mData.OnTrade = true;
                        }
                    }
                    OnTradeFlag = OnTrade;
                    mData.OpenPositionIsBuy = jsonResult["openPositionSellA"].ConvertInvariant<int>() == 0 ? true : false;
                    var rangeList = JArray.Parse(jsonResult["profitRange"].ToStringInvariant());
//                         decimal avgDiff = jsonResult["maAvg"].ConvertInvariant<decimal>();
//                         if (mData.AutoCalcProfitRange)
//                             avgDiff = jsonResult["maAvg"].ConvertInvariant<decimal>();
//                         else
//                             avgDiff = mData.MidDiff;
//                         avgDiff = Math.Round(avgDiff, 4);//强行转换
                    if (rangeList.Count == 2)
                    {
                        mData.ProfitRange = rangeList[0].ConvertInvariant<decimal>();
                        mData.StopRange = rangeList[1].ConvertInvariant<decimal>();
                    }
                    mData.SaveToDB(mDBKey);
                    //                         if (lastOpenPositionBuyA != mData.OpenPositionBuyA || lastOpenPositionSellA != mData.OpenPositionSellA)//仓位修改立即刷新
                    //                                     CountDiffGridMaxCount();
                    //Logger.Debug(Utils.Str2Json(" UpdateAvgDiffAsync avgDiff", avgDiff));
                }
                catch (Exception ex)
                {
                    Logger.Debug(" UpdateAvgDiffAsync avgDiff:" + ex.ToString());
                }
                
                await Task.Delay(60 * 1000);
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
        private async Task A2BExchange(decimal buyPrice,decimal buyAmount)
        {
            await AddOrder2Exchange(true, mData.SymbolA, buyPrice, buyAmount);
        }
        /// <summary>
        /// 当curAmount大于0的时候就是开仓
        /// B买A卖
        /// </summary>
        /// <param name="exchangeAmount"></param>
        private async Task B2AExchange(decimal sellPrice, decimal buyAmount)
        {
            await AddOrder2Exchange(false, mData.SymbolA, sellPrice, buyAmount);
        }
        private async Task AddOrder2Exchange(bool isBuy,string symbol,decimal buyPrice, decimal buyAmount)
        {
            //A限价买
            ExchangeOrderRequest requestA = new ExchangeOrderRequest()
            {
                ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
            };
            requestA.Amount = buyAmount;
            requestA.MarketSymbol = symbol;
            requestA.IsBuy = isBuy;
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
                Logger.Debug(Utils.Str2Json("requestA", requestA.ToString()));
                Logger.Debug(Utils.Str2Json("Add mCurrentLimitOrder", mCurOrderA.ToExcleString(), "CurAmount", mData.CurAmount));
                if (mCurOrderA.Result == ExchangeAPIOrderResult.Canceled)
                {
                    mCurOrderA = null;
                    mOnTrade = false;
                    await Task.Delay(2000);
                }
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                //如果是添加新单那么设置为null 
                if (isAddNew || ex.ToString().Contains("Invalid orderID") || ex.ToString().Contains("Not Found"))
                {
                    mCurOrderA = null;
                    mOnTrade = false;
                }
                Logger.Error(Utils.Str2Json("ex", ex));
                if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Bad Gateway"))
                    await Task.Delay(5000);
                if (ex.ToString().Contains("RateLimitError"))
                    await Task.Delay(30000);
            }
        }
        private async Task CancelCurOrderA()
        {
            if (mCurOrderA!=null)
            {
                ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                cancleRequestA.ExtraParameters.Add("orderID", mCurOrderA.OrderId);
                //在onOrderCancle的时候处理
                await mExchangeAAPI.CancelOrderAsync(mCurOrderA.OrderId, mData.SymbolA);
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
                Logger.Debug("-------------------- Order Filed ---------------------------");
                if (mOrderFiledIds.Contains(order.OrderId))//可能重复提交同样的订单
                {
                    Logger.Error(Utils.Str2Json("重复提交订单号", order.OrderId));
                    return;
                }
                mOrderFiledIds.Add(order.OrderId);
                Logger.Debug(order.ToString());
                Logger.Debug(order.ToExcleString());
                async void fun()
                {
                    mExchangePending = true;
                    mExchangePending = false;
                    await SetPorfitOrder(order);
                    // 如果 当前挂单和订单相同那么删除
                    if (mCurOrderA != null && mCurOrderA.OrderId == order.OrderId)
                    {
                        mCurOrderA = null;
                        mOnTrade = false;
                    }
                    //PrintFilledOrder(order, null);

                }
                if (mCurOrderA != null)//可能为null ，locknull报错
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
        }
        /// <summary>
        /// 订单部分成交
        /// </summary>
        /// <param name="order"></param>
        private async Task OnFilledPartiallyAsync(ExchangeOrderResult order)
        {
            if (order.Amount == order.AmountFilled)
                return;
            Logger.Debug( "-------------------- Order Filed Partially---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            await SetPorfitOrder(order);
            //PrintFilledOrder(order, null);
        }
        /// <summary>
        /// 挂止盈止损单
        /// </summary>
        private async Task SetPorfitOrder(ExchangeOrderResult order)
        {
            mOnWaitTimeSpan = true;
            var transAmount = GetParTrans(order);
            if (transAmount <= 0)//部分成交返回两次一样的数据，导致第二次transAmount=0
                return;
            ExchangeOrderResult backResult = null;
            mCurAmount += order.IsBuy ? transAmount : -transAmount;
            Logger.Debug(Utils.Str2Json("CurAmount:", mData.CurAmount));
            await CancelCurOrderA();
            await MakeProfitOrder();
        }
        

        private void PrintFilledOrder(ExchangeOrderResult order, ExchangeOrderResult backOrder)
        {
            if (order == null)
                return;
            if (backOrder == null)
                return;
            try
            {
                Logger.Debug("--------------PrintFilledOrder--------------");
                Logger.Debug(Utils.Str2Json("filledTime", Utils.GetGMTimeTicks(order.OrderDate).ToString(),
                    "direction", order.IsBuy ? "buy" : "sell",
                    "orderData", order.ToExcleString()));
                //如果是平仓打印日志记录 时间  ，diff，数量
                decimal lastAmount = mCurAmount + (order.IsBuy? -backOrder.Amount : backOrder.Amount);
                if ((lastAmount >0 && !order.IsBuy) ||//正仓位，卖
                    (lastAmount < 0) && order.IsBuy)//负的仓位，买
                {
                    DateTime dt = backOrder.OrderDate.AddHours(8);
                    List<string> strList = new List<string>()
                    {
                        dt.ToShortDateString()+"/"+dt.ToLongTimeString(),order.IsBuy ? "buy" : "sell",backOrder.Amount.ToString(), (order.AveragePrice/backOrder.AveragePrice-1).ToString()
                    };
                    Utils.AppendCSV(new List<List<string>>() { strList }, Path.Combine(Directory.GetCurrentDirectory(), "ClosePosition.csv"), false);
                }
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
            if( (order.Result == ExchangeAPIOrderResult.FilledPartially || order.Result == ExchangeAPIOrderResult.Filled)&& order.MarketSymbol== mData.SymbolA)
            {
                if ((order.StopPrice > 0 && order.Amount > 0) || mProfitLostOrderIds.Equals(order.OrderId))
                {
                    Logger.Error("止损触发停止运行程序");
                    mData.OnTrade = false;
                    Task task1 = CancelCurOrderA();
                    Task.WaitAll(task1);
                    Task task2 = MakeProfitOrder();
                    //                     Environment.Exit(0);
                    //                     throw new Exception("止损触发停止运行程序");
                }
                else if (mProfitWinOrderIds.Equals(order.OrderId))
                {

                    try
                    {
                        Logger.Debug("--------------PrintFilledOrder--------------");
                        Logger.Debug(Utils.Str2Json("filledTime", Utils.GetGMTimeTicks(order.OrderDate).ToString(),
                            "direction", order.IsBuy ? "buy" : "sell",
                            "orderData", order.ToExcleString()));
                        //如果是平仓打印日志记录 时间  ，diff，数量
                        DateTime dt = order.OrderDate.AddHours(8);
                        List<string> strList = new List<string>()
                        {
                            dt.ToShortDateString()+"/"+dt.ToLongTimeString(),order.IsBuy ? "buy" : "sell",order.Amount.ToString(), (mBasePrice/order.AveragePrice-1).ToString()
                        };
                        Utils.AppendCSV(new List<List<string>>() { strList }, Path.Combine(Directory.GetCurrentDirectory(), "ClosePosition.csv"), false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("PrintFilledOrder" + ex);
                    }
                    Task task1 = CancelCurOrderA();
                    Task.WaitAll(task1);
                    Task task2 = MakeProfitOrder();
                }

            }
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
        private decimal GetParTrans(ExchangeOrderResult order)
        {
            Logger.Debug("-------------------- GetParTrans ---------------------------");
            lock (mFilledPartiallyDic)//防止多线程并发
            {
                decimal filledAmount = 0;
                mFilledPartiallyDic.TryGetValue(order.OrderId, out filledAmount);
                Logger.Debug(" filledAmount: " + filledAmount.ToStringInvariant());
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
            public string SymbolA ;
            public string Symbol;
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
            /// 自动计算最大开仓数量
            /// </summary>
            public bool AutoCalcMaxPosition = true;
            /// <summary>
            ///是否执行交易
            /// </summary>
            public bool OnTrade = true;
            /// <summary>
            ///开仓方向是否为多
            /// </summary>
            public bool OpenPositionIsBuy = true;
            /// <summary>
            /// 杠杆倍率
            /// </summary>
            public decimal Leverage = 3;
            /// <summary>
            /// 止盈率
            /// 当AutoCalcProfitRange 开启时有效
            /// </summary>
            public decimal ProfitRange = 0.01m;
            /// <summary>
            /// 止损率
            /// 当AutoCalcProfitRange 开启时有效
            /// </summary>
            public decimal StopRange = 0.01m;
            /// <summary>
            /// 最大数量
            /// </summary>
            public decimal MaxBuyAmount = 0m;
            /// <summary>
            /// 开始交易时候的初始火币数量
            /// </summary>
            public decimal MaxSellAmount = 0m;
            /// <summary>
            /// 提交订单间隔
            /// </summary>
            public int TimeSpan = 3600;
            /// <summary>
            /// 本位币
            /// </summary>
            public string AmountSymbol = "BTC";
            /// <summary>
            /// A交易所加密串路径
            /// </summary>
            public string EncryptedFileA;
            /// <summary>
            /// redis连接数据
            /// </summary>
            public string RedisConfig = "localhost,password=l3h2p1w0*";
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


    }
}