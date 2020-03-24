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
    public class EarnDiff
    {
        private IExchangeAPI mExchangeAAPI;
        private IExchangeAPI mExchangeBAPI;
        private IWebSocket mOrderws;
        private IWebSocket mOrderBookAws;
        private IWebSocket mOrderBookBws;

        private int mOrderBookAwsCounter = 0;
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
        /// 当前挂出去的订单多仓
        /// </summary>
        private OpenOrder mCurOrderA;
        /// <summary>
        /// 当前挂出去的订单空仓
        /// </summary>
        private OpenOrder mCurOrderB;
        /// <summary>
        /// A交易所的历史订单ID
        /// </summary>
        private List<string> mOrderIds = new List<string>();
        /// <summary>
        /// A交易所止损止盈订单ID
        /// </summary>
        private List<string> mProfitOrderIds = new List<string>();
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
        private decimal mCurAAmount
        {
            get
            {
                return mData.CurAAmount;
            }
            set
            {
                mData.CurAAmount = value;
                mData.SaveToDB(mDBKey);
            }
        }
        private string mDBKey;
        private Task mRunningTask;
        private bool mExchangePending = false;
        private bool mOrderwsConnect = false;
        private bool mOrderBookAwsConnect = false;
        private bool mOnCheck = false;//是否检查仓位中
        private bool mOnConnecting = false;
        private bool mBuyAState;

        public EarnDiff(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("EarnDiff:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
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
            
           // /*
            //==================================================*tset
            Task t = Task.Delay(1000);
            Task.WaitAll(t);
            //添加或者修改订单
            //             var order = new ExchangeOrderRequest()
            //             {
            //                 MarketSymbol = "ETH-PERP",
            //                 Amount = 0.1m,
            //                 Price = 112,
            //                 IsBuy = true,
            //                 OrderType = OrderType.Limit,
            // 
            //             };
            //             order.ExtraParameters.Add("orderID", "1584440423629_0");
            //             mExchangeAAPI.PlaceOrderAsync(order);
            //删除订单
            //mExchangeAAPI.CancelOrderAsync("1584425561076_0");

            //             mExchangeAAPI.GetOrderDetailsWebSocket((ExchangeOrderResult order) => {
            //                 Logger.Debug(order.ToExcleString());
            //             });


            //仓位
            //mExchangeAAPI.GetOpenPositionAsync("USDT-PERP");
            //==================================================
            //*/

            ///*
            CheckPosition();
            mExchangeAAPI.CancelOrderAsync("", mData.SymbolA);
            UpdateAvgDiffAsync();
            SubWebSocket();
            WebSocketProtect();
            ChangeMaxCount();

            //*/
        }
        /// <summary>
        /// 倒计时平仓
        /// </summary>
        private async Task ClosePosition()
        {
            double deltaTime = (mData.CloseDate - DateTime.Now).TotalSeconds;
            Logger.Debug(Utils.Str2Json("deltaTime", deltaTime));
            await Task.Delay((int)(deltaTime * 1000));
            Logger.Debug("关闭策略只平仓不开仓");
            lock (mData)
            {
                foreach (var diff in mData.DiffGrid)
                {
                    diff.MaxABuyAmount = 0;
                    diff.MaxASellAmount = 0;
                }
                mData.SaveToDB(mDBKey);
            }
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
                if (mOrderBookAwsCounter < 1  || (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    CancelAll();
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
            CancelAll();
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
        /// <summary>
        /// 删除全部挂单
        /// </summary>
        private void CancelAll()
        {
            if (mCurOrderA != null)
            {
                if (mCurOrderA.mOnUse)
                {
                    CancelCurOrderA(mCurOrderA.MOrderResult.OrderId);
                    mCurOrderA.CancelOrder();
                }
            }
            if (mCurOrderB != null)
            {
                if (mCurOrderB.mOnUse)
                {
                    CancelCurOrderA(mCurOrderB.MOrderResult.OrderId);
                    mCurOrderB.CancelOrder();
                }
            }
        }

        private async Task CancelAllAsync()
        {
            if (mCurOrderA != null)
            {
                if (mCurOrderA.mOnUse)
                {
                    await CancelCurOrderA(mCurOrderA.MOrderResult.OrderId);
                    mCurOrderA.CancelOrder();
                }
            }
            if (mCurOrderB != null)
            {
                if (mCurOrderB.mOnUse)
                {
                    await CancelCurOrderA(mCurOrderB.MOrderResult.OrderId);
                    mCurOrderB.CancelOrder();
                }
            }
        }

        private async void UpdatePosition()
        {
            try
            {
                ExchangeMarginPositionResult posA;
                //等待10秒 避免ws虽然推送数据刷新，但是rest 还没有刷新数据
                //await Task.Delay(10 * 1000);
                posA = await mExchangeAAPI.GetOpenPositionAsync(mData.SymbolA);
               
                if (posA != null )
                {
                    if (mCurAAmount!=posA.Amount)
                        Logger.Error("UpdatePosition 检查仓位发现数量不相等 mCurAAmount：" + mCurAAmount+ "  posA.Amount:"+ posA.Amount);
                    
                    mCurAAmount = posA.Amount;
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error(Utils.Str2Json("GetOpenPositionAsync ex", ex.ToString()));
                mExchangePending = false;
                await Task.Delay(1000);
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
                mOnCheck = true;
                await CancelAllAsync();
                await Task.Delay(10 * 1000);
                UpdatePosition();
                await Task.Delay(5*1000);

                mOnCheck = false;
                await Task.Delay(5 * 60 * 1000);
            }
        }
#endregion
        private void OnProcessExit(object sender, EventArgs e)
        {
            Logger.Debug("------------------------ OnProcessExit ---------------------------");
            mExchangePending = true;
            CancelAll();
            Thread.Sleep(5 * 1000);
           
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
            if (mData.AutoCalcMaxPosition)
            {
                try
                {
                    decimal noUseBtc = await GetAmountsAvailableToTradeAsync(mExchangeAAPI,"");
                    decimal allBtc = noUseBtc;
                    //总仓位 = 总btc数量*（z19+u19）/2 *3倍杠杆/2种合约 
                    decimal allPosition = Math.Floor(noUseBtc * mData.Leverage *1.5m);
                    decimal lastPosition = 0;
                    foreach (Diff diff in mData.DiffGrid )
                    {
                        lastPosition += allPosition* diff.Rate;
                        //lastPosition = Math.Round(lastPosition / mData.PerTrans) * mData.PerTrans;
                        diff.MaxASellAmount = mData.OpenPositionSellA ? lastPosition : 0;
                        diff.MaxABuyAmount = mData.OpenPositionBuyA ? lastPosition : 0;
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
                if(temp.CurAAmount!=mData.CurAAmount)
                {
                    await CancelAllAsync();
                    temp.CurAAmount = mData.CurAAmount;
                    mData = temp;
                }
                if (last_mData.OpenPositionBuyA != mData.OpenPositionBuyA || last_mData.OpenPositionSellA != mData.OpenPositionSellA)//仓位修改立即刷新
                    CountDiffGridMaxCount();
                await Execute();
                await Task.Delay(mData.IntervalMillisecond+1000*5);
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
            if (mOnCheck)
                return false;
            if (mOrderBookA.Asks.Count == 0 || mOrderBookA.Bids.Count == 0 )
                return false;
            return true;
        }
        private async Task Execute()
        {
            decimal buyPriceA;
            decimal sellPriceA;
            decimal bidAAmount, askAAmount;
            decimal buyPrice = 0;
            decimal sellPrice = 0;
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
                buyPrice = buyPriceA;
                sellPrice = sellPriceA;


                //如果盘口差价超过4usdt 不进行挂单，但是可以改单（bitmex overload 推送ws不及时）
                if (mCurOrderA == null && ((sellPriceA <= buyPriceA) || (sellPriceA - buyPriceA >= 4)))
                {
                    Logger.Debug("范围更新不及时，不纳入计算");
                    return;
                }
                //return;

                //如果当前 仓位为0 那么两倍挂单理想价位仓位
                //2 有仓位，反向挂单 2倍到理想价位
                mRunningTask = DoDoubleOpen(buyPrice, sellPrice,bidAAmount, askAAmount);

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
                        if (ex.ToString().Contains("Order already closed") || ex.ToString().Contains("Not Found"))
                            mCurOrderA = null;
                        mRunningTask = null;
                    }
                }
            }
        }

        /// <summary>
        /// 依次检查所有梯队
        /// 计算多仓开仓值和仓位是否满足挂单条件 ，计算平仓值和仓位是否满足挂单
        ///多仓位挂单：  当前仓位（空仓负数）＜ 多仓数量
        ///多仓挂单数量 = 多仓数量 - 当前仓位
        ///空仓挂单数量 = 多仓数量- ab（当前仓位）
        /// </summary>
        private async Task DoDoubleOpen(decimal curBuyPrice, decimal curSellPrice, decimal bidAAmount, decimal askAAmount)
        {

            bool hadBuy = false;
            bool hadSell = false;
            decimal realBuyPrice = curBuyPrice;
            decimal realSellPrice = curSellPrice;
            decimal realBuyAmount = 0;
            decimal realSellAmount = 0;
            foreach (var _diff in mData.DiffGrid)
            {
                decimal curbuyAmount = _diff.MaxABuyAmount - mData.CurAAmount;
                if (!hadBuy && curbuyAmount > 0)//多仓挂单：多仓准备开仓数量 -  当前仓位（空仓负数）> 0
                {
                    realBuyAmount = curbuyAmount;
                    realBuyPrice = Math.Min(_diff.BuyPrice, curBuyPrice);
                    await A2BExchange(realBuyPrice, curbuyAmount, mCurOrderA);
                    hadBuy = true;
                }
                decimal curSellAmount = _diff.MaxASellAmount + mData.CurAAmount;
                if (!hadSell && curSellAmount > 0)//空仓挂单：空仓准备开仓数量 +  当前仓位（空仓负数）> 0
                {
                    realSellAmount = curSellAmount;
                    realSellPrice = Math.Max(_diff.SellPrice, curSellPrice);
                    await B2AExchange(realSellPrice, curSellAmount, mCurOrderB);
                    hadSell = true;
                }
            }
            PrintInfo(curBuyPrice, curSellPrice, realBuyPrice, realSellPrice, realBuyAmount, realSellAmount,bidAAmount, askAAmount);
            if (!hadBuy && mCurOrderA.mOnUse)//如果没有满足买单的那么取消当前买单
            {
                await CancelCurOrderA(mCurOrderA.MOrderResult.OrderId);
            }
            if (!hadSell && mCurOrderB.mOnUse)//如果没有满足卖单的那么取消当前卖单
            {
                await CancelCurOrderA(mCurOrderB.MOrderResult.OrderId);
            }
        }

    
        /// <summary>
        /// 刷新差价
        /// </summary>
        public async Task UpdateAvgDiffAsync()
        {
            string dataUrl = $"{"http://150.109.52.225:8006/arbitrage/process?programID="}{mId}{"&symbol="}{mData.Symbol}{"&exchangeB="}{mData.ExchangeNameB.ToLowerInvariant()}{"&exchangeA="}{mData.ExchangeNameA.ToLowerInvariant()}";
            dataUrl = "http://150.109.52.225:8006/arbitrage/process?programID=" + mId + "&symbol=BTCZH&exchangeB=bitmex&exchangeA=bitmex";
            bool first = true;
            Logger.Debug(dataUrl);
            while (true)
            {
                try
                {
                    bool lastOpenPositionBuyA = mData.OpenPositionBuyA;
                    bool lastOpenPositionSellA = mData.OpenPositionSellA;
                    JObject jsonResult = await Utils.GetHttpReponseAsync(dataUrl);
                    mData.DeltaDiff = jsonResult["deltaDiff"].ConvertInvariant<decimal>();
                    var newLeverage = jsonResult["leverage"].ConvertInvariant<decimal>();
                    mData.OpenPositionBuyA = jsonResult["openPositionBuyA"].ConvertInvariant<int>() == 0 ? false : true;
                    mData.OpenPositionSellA = jsonResult["openPositionSellA"].ConvertInvariant<int>() == 0 ? false : true;
                    var rangeList = JArray.Parse(jsonResult["profitRange"].ToStringInvariant());
                    decimal avgDiff = jsonResult["maAvg"].ConvertInvariant<decimal>();
                    if (mData.AutoCalcProfitRange)
                        avgDiff = jsonResult["maAvg"].ConvertInvariant<decimal>();
                    else
                        avgDiff = mData.MidDiff;
                    avgDiff = Math.Round(avgDiff, 4);//强行转换
                    
                    for (int i = 0; i < rangeList.Count; i+=2)
                    {
                        var diff = mData.DiffGrid[i];
                        diff.BuyPrice = rangeList[i].ConvertInvariant<decimal>();
                        diff.SellPrice = rangeList[rangeList.Count-1].ConvertInvariant<decimal>();
                    }
                    if (mData.Leverage!= newLeverage || first)
                    {
                        first = false ;
                        mData.Leverage = newLeverage;
                        CountDiffGridMaxCount();
                    }

                    //if (lastOpenPositionBuyA != mData.OpenPositionBuyA || lastOpenPositionSellA != mData.OpenPositionSellA)//仓位修改立即刷新
                                //CountDiffGridMaxCount();
                            Logger.Debug(Utils.Str2Json(" UpdateAvgDiffAsync avgDiff", avgDiff));
                }
                catch (Exception ex)
                {
                    Logger.Debug(" UpdateAvgDiffAsync avgDiff:" + ex.ToString());
                }
                
                await Task.Delay(60 * 1000);
            }
        }
        private void PrintInfo(decimal bidA, decimal askA,  decimal buyPrice, decimal sellPrice, decimal buyAmount, decimal sellAmount,
            decimal bidAAmount, decimal askAAmount)
        {
            Logger.Debug("================================================");
            Logger.Debug(Utils.Str2Json("当前应做多价格", buyPrice.ToString(), "实时买一", bidA.ToString() )) ;
            Logger.Debug(Utils.Str2Json("当前应做空价格" , sellPrice.ToString(), "实时卖一" , askA.ToString()));
            Logger.Debug(Utils.Str2Json("Bid A", bidA, "bidAAmount", bidAAmount ));
            Logger.Debug(Utils.Str2Json("Ask A", askA,  "askAAmount", askAAmount));
            Logger.Debug(Utils.Str2Json("mCurAmount", mCurAAmount, " buyAmount",  buyAmount, " sellAmount", sellAmount));
        }
        /// <summary>
        /// 当curAmount 小于 0的时候就是平仓
        /// A买B卖
        /// </summary>
        private async Task A2BExchange(decimal buyPrice,decimal buyAmount,OpenOrder order)
        {
            await AddOrder2Exchange(true, mData.SymbolA, buyPrice, buyAmount, order );
        }
        /// <summary>
        /// 当curAmount大于0的时候就是开仓
        /// B买A卖
        /// </summary>
        /// <param name="exchangeAmount"></param>
        private async Task B2AExchange(decimal sellPrice, decimal buyAmount, OpenOrder order )
        {
            await AddOrder2Exchange(false, mData.SymbolA, sellPrice, buyAmount,order);
        }
        private async Task AddOrder2Exchange(bool isBuy,string symbol,decimal buyPrice, decimal buyAmount, OpenOrder openOrder)
        {

            ExchangeOrderResult order =null;
            if (openOrder.mOnUse)
            {
                order = openOrder.MOrderResult;
            }
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
                if (order != null)
                {
                    //方向相同，并且达到修改条件
                    if (order.IsBuy == requestA.IsBuy)
                    {
                        isAddNew = false;
                        requestA.ExtraParameters.Add("orderID", order.OrderId);
                        //检查是否有改动必要
                        //做多涨价则判断
                        if (requestA.Price == order.Price )
                        {
                            if (Math.Abs(requestA.Amount / order.Amount-1)<0.1m)//如果数量变化小于百分之10 那么不修改订单
                                return;
                        }
                        await CancelCurOrderA(order.OrderId);
                        openOrder.CancelOrder();
                        return;
                    }
                    else
                    {//如果方向相反那么直接取消
                        await CancelCurOrderA(order.OrderId);
                        openOrder.CancelOrder();
                        return;
                    }
                    //如果已出现部分成交并且需要修改价格，则取消部分成交并重新创建新的订单
                    if (mFilledPartiallyDic.Keys.Contains(order.OrderId))
                    {
                        await CancelCurOrderA(order.OrderId);
                        if (requestA.ExtraParameters.Keys.Contains("orderID"))
                            requestA.ExtraParameters.Remove("orderID");
                    }
                };
                var v = await mExchangeAAPI.PlaceOrderAsync(requestA);
                order = v;
                mOrderIds.Add(order.OrderId);
                Logger.Debug(Utils.Str2Json("requestA", requestA.ToString()));
                Logger.Debug(Utils.Str2Json("Add mCurrentLimitOrder", order.ToExcleString(), "CurAmount", mData.CurAAmount));
                if (order.Result == ExchangeAPIOrderResult.Canceled)
                {
                    await Task.Delay(2000);
                    openOrder.CancelOrder();
                    return;
                }
                else
                {
                    openOrder.MOrderResult = order;
                }
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Logger.Error(Utils.Str2Json("ex", ex));
                //如果是添加新单那么设置为null 
                if (isAddNew || ex.ToString().Contains("Order already closed") || ex.ToString().Contains("Not Found"))
                {
                    openOrder.CancelOrder();
                    return;
                }
                if (ex.ToString().Contains("405 Method Not Allowed"))//删除订单
                {
                    await CancelCurOrderA(order.OrderId);
                    openOrder.CancelOrder();
                    return;
                }
                
                if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Bad Gateway"))
                    await Task.Delay(5000);
                if (ex.ToString().Contains("RateLimitError"))
                    await Task.Delay(30000);
                openOrder.MOrderResult = order;
            }
        }
        private void SetOrderNull(string orderId)
        {
            if (mCurOrderA != null && mCurOrderA.MOrderResult.OrderId == orderId)
            {
                mCurOrderA.CancelOrder();
            }
            if (mCurOrderB != null && mCurOrderB.MOrderResult.OrderId == orderId)
            {
                mCurOrderB.CancelOrder();
            }
        }

        private async Task CancelCurOrderA( string orderId)
        {
            ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
            cancleRequestA.ExtraParameters.Add("orderID", orderId);
            //在onOrderCancle的时候处理
            Logger.Debug("CancelCurOrderA" + orderId);
            await mExchangeAAPI.CancelOrderAsync(orderId, mData.SymbolA);
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
                    var transAmount = GetParTrans(order);
                    mCurAAmount += order.IsBuy ? transAmount : -transAmount;
                    //ExchangeOrderResult backResult = await ReverseOpenMarketOrder(order);
                    mExchangePending = false;
                    // 如果 当前挂单和订单相同那么删除
                    SetOrderNull(order.OrderId);
                    //PrintFilledOrder(order, backResult);
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

            var transAmount = GetParTrans(order);
            mCurAAmount += order.IsBuy ? transAmount : -transAmount;
            //ExchangeOrderResult backOrder = await ReverseOpenMarketOrder(order);
            //PrintFilledOrder(order, backOrder);
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
                decimal lastAmount = mCurAAmount + (order.IsBuy? -backOrder.Amount : backOrder.Amount);
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
            Logger.Debug("Canceled  " + order.ToExcleString() + "CurAmount" + mData.CurAAmount);
            SetOrderNull(order.OrderId);
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
                if ((order.StopPrice > 0 && order.Amount > 0) || mProfitOrderIds.Contains(order.OrderId))
                {
                    Logger.Error("止盈触发停止运行程序");
                    Environment.Exit(0);
                    throw new Exception("止盈触发停止运行程序");
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
        /// 反向市价开仓
        /// </summary>
        private async Task<ExchangeOrderResult> ReverseOpenMarketOrder(ExchangeOrderResult order)
        {
            
            var transAmount = GetParTrans(order);
            if (transAmount <= 0)//部分成交返回两次一样的数据，导致第二次transAmount=0
                return null;
            ExchangeOrderResult backResult = null;
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
                        if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Bad Gateway"))
                        {
                            await Task.Delay(2000);
                        }
                        else
                        {
                            Logger.Error(Utils.Str2Json("最小成交价抛错" , ex.ToString()));
                            throw ex;
                        }
                        
                    }
                }
            }
            //只有在成交后才修改订单数量
            mCurAAmount += order.IsBuy ? transAmount : -transAmount;
            Logger.Debug(Utils.Str2Json(  "CurAmount:" , mData.CurAAmount));
            Logger.Debug("mId{0} {1}", mId, mCurAAmount);
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
                    backResult = res;
                    break;
                }
                catch (Exception ex)
                {
                    if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("403 Forbidden") || ex.ToString().Contains("Bad Gateway") )
                    {
                        Logger.Error(Utils.Str2Json( "req", req.ToStringInvariant(), "ex", ex));
                        await Task.Delay(2000);
                    }
                    else if (ex.ToString().Contains("RateLimitError"))
                    {
                        Logger.Error(Utils.Str2Json("req", req.ToStringInvariant(), "ex", ex));
                        await Task.Delay(5000);
                    }
                    else
                    {
                        Logger.Error(Utils.Str2Json("ReverseOpenMarketOrder抛错" , ex.ToString()));
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
        /*
        /// <summary>
        /// 改变状态为只开仓或者只平仓
        /// </summary>
        /// <param name="isBuy"></param>
        private void ChangeBuyOrSell(bool isBuy)
        {
            lock (mData)
            {
                //如果状态想通不需要重新计算
                if (isBuy == mBuyAState)
                    return;
                mBuyAState = isBuy;
                if (isBuy)
                {
                    mData.OpenPositionBuyA = true;
                    mData.OpenPositionSellA = false;
                    foreach (var diff in mData.DiffGrid)
                    {
                        diff.A2BDiff = mData.BuyDiff;
                        diff.B2ADiff = -2;
                        mData.SaveToDB(mDBKey);
                    }
                    Logger.Debug("修改为只多仓不平仓");
                }
                else
                {
                    mData.CloseToA1Amount = Math.Floor(mCurAAmount * mData.CloseRate);//设置平仓到的数量
                    mData.OpenPositionBuyA = false;
                    mData.OpenPositionSellA = false;
                    foreach (var diff in mData.DiffGrid)
                    {
                        diff.A2BDiff = 2;
                        diff.B2ADiff = mData.SellDiff;
                        diff.MaxABuyAmount = 0;
                        diff.MaxASellAmount = 0;
                        mData.SaveToDB(mDBKey);
                    }
                    Logger.Debug("修改为只平仓不再开仓");
                }
                mData.SaveToDB(mDBKey);
                Task.WaitAll(CountDiffGridMaxCount());
            }
        }
        */
        public class Options
        {
            public string ExchangeNameA;
            public string ExchangeNameB;
            public string SymbolA;
            public string SymbolB;
            public string Symbol;
            public decimal DeltaDiff = 0m;
            public decimal MidDiff = 0.0m;
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
            public decimal MinPriceUnit = 0.5m;
            /// <summary>
            /// 最小订单总价格
            /// </summary>
            public decimal MinOrderPrice = 0.0011m;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurAAmount = 0;
            /// <summary>
            /// 当前B仓位数量
            /// </summary>
            public decimal CurBAmount = 0;
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
            public decimal Leverage = 3;
            /// <summary>
            /// 本位币
            /// </summary>
            public string AmountSymbol = "BTC";
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
            /// <summary>
            /// 止损或者止盈的比例
            /// </summary>
            public decimal StopOrProftiRate = 0.5m;
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
            /// 开仓值
            /// </summary>
            public decimal BuyPrice;
            /// <summary>
            /// 平仓值
            /// </summary>
            public decimal SellPrice;
            /// <summary>
            /// 最大数量
            /// </summary>
            public decimal MaxABuyAmount;
            /// <summary>
            /// 最大数量
            /// </summary>
            public decimal MaxASellAmount;

            /// <summary>
            /// 满仓占比
            /// </summary>
            public decimal Rate = 0.5m;
            public Diff Clone()
            {
                return (Diff)this.MemberwiseClone();
            }
        }
        /// <summary>
        /// 交易状态
        /// </summary>
        private class OpenOrder
        {
            public bool mOnUse = false;

            private ExchangeOrderResult mOrderResult;

            public OpenOrder()
            {
                mOnUse = false;
            }

            public ExchangeOrderResult MOrderResult
            {
                get {return mOrderResult; } 
                set
                {
                    if (value == null)
                    {
                        mOnUse = false;
                    }
                    mOrderResult = value;
                } 
            }

            public void CancelOrder()
            {
                mOrderResult = null;
                mOnUse = false;
            }


        }

    }
}