﻿using System;
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

namespace cuckoo_csharp.Strategy.Arbitrage
{
    /// <summary>
    /// 期对期
    /// 要求 A价<B价，并且A限价开仓
    /// </summary>
    public class Perpetual2Futures_GetInterest
    {
        private IExchangeAPI mExchangeAAPI;
        private IExchangeAPI mExchangeBAPI;
        private IWebSocket mOrderws;
        private IWebSocket mOrderBookAws;
        private IWebSocket mOrderBookBws;

        private int mOrderBookAwsCounter = 0;
        private int mOrderBookBwsCounter = 0;
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
        private bool mOrderBookBwsConnect = false;
        private bool mOnTrade = false;//是否在交易中
        private bool mOnConnecting = false;
        private bool mBuyAState;
        private decimal mAllPosition;
        /// <summary>
        /// 每次购买数量
        /// </summary>
        private decimal mPerBuyAmount = 0;

        public Perpetual2Futures_GetInterest(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("Perpetual2Futures_GetInterest:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
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
            //             Task t = Task.Delay(1000);
            //             Task.WaitAll(t);
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
            //mExchangeAAPI.GetOpenPositionAsync("ETH-PERP");
            //==================================================

            //             var t1 = mExchangeAAPI.GetCandlesAsync("ETH-PERP",60,DateTime.UtcNow.AddDays(-24), DateTime.UtcNow,35000);
            //             var t2 = mExchangeAAPI.GetCandlesAsync("ETH-0626", 60, DateTime.UtcNow.AddDays(-24), DateTime.UtcNow, 35000);

            //             var t1 = mExchangeAAPI.GetCandlesAsync("ETH-PERP", 60, DateTime.UtcNow.AddDays(-24-87), DateTime.UtcNow.AddDays(-25), 35000);
            //             var t2 = mExchangeAAPI.GetCandlesAsync("ETH-0327", 60, DateTime.UtcNow.AddDays(-24-87), DateTime.UtcNow.AddDays(-25), 35000);
            //             Task.WaitAll(t1,t2);
            //             List<MarketCandle> list1 = new List<MarketCandle>( t1.Result);
            //             List<MarketCandle> list2 = new List<MarketCandle>(t2.Result);
            //             //list
            //             var csvList = new List<List<string>>();
            //             for (int i = 0; i < list1.Count; i++)
            //             {
            //                 
            //                 List<string> strList = new List<string>()
            //                     {
            //                         //i.ToString(),
            //                         (list1[i].HighPrice/list2[i].HighPrice-1).ToString(),
            //                        //list1[i].Timestamp.ToString("yyyy-MM-ddThh:mm:sszzzz", DateTimeFormatInfo.InvariantInfo)
            //                 //list1[i].Timestamp.ToString()
            //                     };
            //                 csvList.Add(strList);
            //             }
            // 
            //             
            //             Utils.AppendCSV(csvList, Path.Combine(Directory.GetCurrentDirectory(), "Candles"+DateTime.UtcNow.ToShortDateString()+".csv"), true);
            //*/

            UpdateAvgDiffAsync();
            SubWebSocket();
            WebSocketProtect();
            CheckPosition();
            ChangeMaxCount();
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
                int delayTime = 60;//保证次数至少要3s一次，否则重启
                mOrderBookAwsCounter = 0;
                mOrderBookBwsCounter = 0;
                mOrderDetailsAwsCounter = 0;
                await Task.Delay(1000 * delayTime);
                Logger.Debug(Utils.Str2Json("mOrderBookAwsCounter", mOrderBookAwsCounter, "mOrderBookBwsCounter", mOrderBookBwsCounter, "mOrderDetailsAwsCounter", mOrderDetailsAwsCounter));
                bool detailConnect = true;
                if (mOrderDetailsAwsCounter == 0)
                    detailConnect = await IsConnectAsync();
                Logger.Debug(Utils.Str2Json("mOrderDetailsAwsCounter", mOrderDetailsAwsCounter));
                if (mOrderBookAwsCounter < 1 || mOrderBookBwsCounter < 1 || (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    if (mCurOrderA != null)
                    {
                        await CancelCurOrderA();
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
            mOrderBookBwsConnect = false;
            await Task.Delay(5 * 1000);
            Logger.Debug("销毁ws");
            mOrderws.Dispose();
            mOrderBookAws.Dispose();
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
            bool connect = mOrderwsConnect & mOrderBookAwsConnect & mOrderBookBwsConnect;
            if (connect)
                mOnConnecting = false;
            return connect;
        }
        
        private async Task WSDisConnectAsync(string tag)
        {
            if (mCurOrderA != null)
            {
                await CancelCurOrderA();
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
                mExchangePending = true;
                Logger.Debug("-----------------------CheckPosition-----------------------------------");
                ExchangeMarginPositionResult posA ;
                ExchangeMarginPositionResult posB ;
                try
                {
                    //等待10秒 避免ws虽然推送数据刷新，但是rest 还没有刷新数据
                    await Task.Delay(10* 1000);
                    posA = await mExchangeAAPI.GetOpenPositionAsync(mData.SymbolA);
                    posB = await mExchangeBAPI.GetOpenPositionAsync(mData.SymbolB);
                    if (posA==null ||posB==null)
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
                    await Task.Delay(1000);
                    continue;
                }
                decimal realAmount = posA.Amount;
                if ((posA.Amount + posB.Amount) !=0)//如果没有对齐停止交易，市价单到对齐
                {
                    if (Math.Abs(posA.Amount + posB.Amount)>mData.PerTrans*10)
                    {
                        Logger.Error(Utils.Str2Json("CheckPosition ex", "A,B交易所相差过大 程序关闭，请手动处理"));
                        throw new Exception("A,B交易所相差过大 程序关闭，请手动处理");
                    }
                    for (int i=0; ;)
                    {
                        decimal count = posA.Amount + posB.Amount;
                        ExchangeOrderRequest requestA = new ExchangeOrderRequest();
                        requestA.Amount = Math.Abs(count);
                        requestA.MarketSymbol = mData.SymbolA;
                        if (count>0)//表示需要A卖count张 ，之和就==0
                        {
                            requestA.IsBuy = false;
                        }
                        else//表示需要A买count张 ，之和就==0
                        {
                            requestA.IsBuy = true;
                        }
                        requestA.OrderType = OrderType.Market;
                        try
                        {
                            Logger.Debug(Utils.Str2Json("差数量" , count));
                            Logger.Debug(Utils.Str2Json("requestA", requestA.ToString()));
                            var orderResults = await mExchangeAAPI.PlaceOrderAsync(requestA);
                            ExchangeOrderResult resultA = orderResults;
                            realAmount += requestA.IsBuy ? requestA.Amount : -requestA.Amount;
                            break; 
                        }
                        catch (System.Exception ex)
                        {
                            if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Not logged in"))
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
                if (realAmount!=mCurAAmount)
                {
                    Logger.Debug(Utils.Str2Json("Change curAmount", realAmount));
                    mCurAAmount = realAmount;
                }
                //==================挂止盈单==================如果止盈点价格>三倍当前价格那么不挂止盈单
                //一单为空，那么挂止盈多单，止盈价格为另一单的强平价格（另一单多+500，空-500）
                //一单为多 相反
                /*
                else if (realAmount != 0)
                {
                    Logger.Debug(Utils.Str2Json("挂止盈单", realAmount));
                    List<ExchangeOrderResult> profitA;
                    List<ExchangeOrderResult> profitB;
                    ExchangeOrderResult profitOrderA = null;
                    ExchangeOrderResult profitOrderB = null;
                    //下拉最新的值 ，来重新计算 改开多少止盈订单
                    try
                    {
                        profitA = new List<ExchangeOrderResult>(await mExchangeAAPI.GetOpenOrderDetailsAsync(mData.SymbolA));
                        profitB = new List<ExchangeOrderResult>(await mExchangeAAPI.GetOpenOrderDetailsAsync(mData.SymbolB));
                        int counter = 0;
                        foreach (ExchangeOrderResult re in profitA)
                        {
                            if (re.Result == ExchangeAPIOrderResult.Pending)
                            {
                                //profitOrderA = re;
                                await mExchangeAAPI.CancelOrderAsync(re.OrderId, re.MarketSymbol);
                                Logger.Debug("re.OrderId error:" + re.OrderId);
                                counter++;
                                //break;
                            }
                        }
                        foreach (ExchangeOrderResult re in profitB)
                        {
                            if (re.Result == ExchangeAPIOrderResult.Pending)
                            {
                                //profitOrderB = re;
                                await mExchangeAAPI.CancelOrderAsync(re.OrderId, re.MarketSymbol);
                                Logger.Debug("re.OrderId error:" + re.OrderId);
                                counter++;
                                //break;
                            }
                        }
                        if (counter != 6)
                        {
                            Logger.Debug("GetOpenProfitOrderDetailsAsync error");
                        }
                        mProfitOrderIds.Clear();
                    }
                    catch (System.Exception ex)
                    {
                        Logger.Error(Utils.Str2Json("GetOpenOrderDetailsAsync ex", ex.ToString()));
                        mExchangePending = false;
                        await Task.Delay(1000);
                        continue;
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
                        request.ExtraParameters.Add("execInst", "Close");
                        for (int i = 0; ;)
                        {
                            try
                            {
                                Logger.Debug(Utils.Str2Json("request profit", request.ToString()));
                                var orderResults = await mExchangeAAPI.PlaceOrderAsync(request);
                                ExchangeOrderResult result = orderResults[0];
                                Logger.Debug(Utils.Str2Json("result profit", result.ToString()));
                                return result;
                            }
                            catch (System.Exception ex)
                            {
                                if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Not logged in"))
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
                    bool aBuy = posA.Amount > 0;
                    decimal curPriceA = 0;
                    decimal curPriceB = 0;
                    lock (mOrderBookA)
                    {
                        curPriceA = mOrderBookA.Asks.FirstOrDefault().Value.Price;
                    }
                    lock (mOrderBookB)
                    {
                        curPriceB = mOrderBookB.Asks.FirstOrDefault().Value.Price;
                    }
                    bool isError = false;
                    if (aBuy)
                    {
                        if (posB.LiquidationPrice <= curPriceA)
                        {
                            Logger.Error(" winPrice标记价格错误: 当前价格A" + curPriceA + " 当前价格B:" + curPriceB + "  当前数量：" + mCurAAmount);
                            isError = true;
                        }
                        if (posA.LiquidationPrice >= curPriceA)
                        {
                            Logger.Error(" lostPrice标记价格错误: 当前价格A" + curPriceA + " 当前价格B:" + curPriceB + "  当前数量：" + mCurAAmount);
                            isError = true;
                        }
                        Logger.Error(" posA.LiquidationPrice:" + posA.LiquidationPrice + " posB.LiquidationPrice:" + posB.LiquidationPrice);
                    }
                    else
                    {
                        if (posB.LiquidationPrice >= curPriceA)
                        {
                            Logger.Error(" winPrice标记价格错误: 当前价格A" + curPriceA + " 当前价格B:" + curPriceB + "  当前数量：" + mCurAAmount);
                            isError = true;
                        }
                        if (posA.LiquidationPrice <= curPriceA)
                        {
                            Logger.Error(" lostPrice标记价格错误: 当前价格A" + curPriceA + " 当前价格B:" + curPriceB + "  当前数量：" + mCurAAmount);
                            isError = true;
                        }
                        Logger.Error(" posA.LiquidationPrice:" + posA.LiquidationPrice + " posB.LiquidationPrice:" + posB.LiquidationPrice);
                    }
                    if (!isError)
                    {
                        decimal lastAmount = Math.Abs(realAmount);
                        ExchangeOrderRequest orderA = new ExchangeOrderRequest()
                        {
                            MarketSymbol = mData.SymbolA,
                            IsBuy = !aBuy,
                            Amount = Math.Abs(realAmount),
                            //StopPrice = posB.LiquidationPrice + (aBuy == false ? 500 : -500),
                            OrderType = OrderType.Limit,
                        };
                        for (int i = 0; i < 3; i++)
                        {
                            decimal initPrice = 300 + i * 200;
                            if (i < 2)
                            {
                                orderA.Amount = Math.Floor(Math.Abs(realAmount) / 3);
                                lastAmount -= orderA.Amount;
                            }
                            else
                                orderA.Amount = lastAmount;
                            //orderA.StopPrice = posB.LiquidationPrice + (aBuy == false ? initPrice : -initPrice);
                            orderA.Price = posB.LiquidationPrice + (aBuy == false ? initPrice : -initPrice);
                            orderA.ExtraParameters.Clear();
                            profitOrderA = await doProfitAsync(orderA, null);
                            if (profitOrderA != null)
                                mProfitOrderIds.Add(profitOrderA.OrderId);
                        }
                        ExchangeOrderRequest orderB = new ExchangeOrderRequest()
                        {
                            MarketSymbol = mData.SymbolB,
                            IsBuy = aBuy,
                            Amount = Math.Abs(realAmount),
                            //StopPrice = posA.LiquidationPrice + (aBuy == true ? 500 : -500),
                            OrderType = OrderType.Limit,
                        };
                        lastAmount = Math.Abs(realAmount);
                        for (int i = 0; i < 3; i++)
                        {
                            decimal initPrice = 300 + i * 200;
                            if (i < 2)
                            {
                                orderB.Amount = Math.Floor(Math.Abs(realAmount) / 3);
                                lastAmount -= orderB.Amount;
                            }
                            else
                                orderB.Amount = lastAmount;
                            //orderB.StopPrice = posA.LiquidationPrice + (aBuy == true ? initPrice : -initPrice);
                            orderB.Price = posA.LiquidationPrice + (aBuy == true ? initPrice : -initPrice);
                            orderB.ExtraParameters.Clear();
                            profitOrderB = await doProfitAsync(orderB, null);
                            if (profitOrderB != null)
                                mProfitOrderIds.Add(profitOrderB.OrderId);
                        }
                    }
                }*/
                mExchangePending = false;
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
            if (mData.AutoCalcMaxPosition)
            {
                try
                {
                    decimal noUseBtc = await GetAmountsAvailableToTradeAsync(mExchangeAAPI, "");
                    decimal allCoin = noUseBtc;
                    decimal avgPrice = ((mOrderBookA.Bids.FirstOrDefault().Value.Price + mOrderBookB.Asks.FirstOrDefault().Value.Price) / 2);
                    mAllPosition = allCoin * mData.Leverage / avgPrice/2;//单位eth个数
                    mData.PerTrans = Math.Round(mData.PerBuyUSD / avgPrice /mData.MinAmountA) * mData.MinAmountA;
                    mData.ClosePerTrans = Math.Round(mData.ClosePerBuyUSD / avgPrice / mData.MinAmountA) * mData.MinAmountA;
                    decimal lastPosition = 0;
                    foreach (Diff diff in mData.DiffGrid)
                    {
                        lastPosition += mAllPosition * diff.Rate;
                        lastPosition = Math.Round(lastPosition / mData.PerTrans) * mData.PerTrans;
                        diff.MaxASellAmount = mData.OpenPositionSellA ? lastPosition : 0;
                        diff.MaxABuyAmount = mData.OpenPositionBuyA ? lastPosition : 0;
                    }
                    mData.SaveToDB(mDBKey);
                    Logger.Debug(Utils.Str2Json("noUseBtc", noUseBtc, "allPosition", mAllPosition));
                }
                catch (System.Exception ex)
                {
                    Logger.Error("ChangeMaxCount ex" + ex.ToString());
                }
            }
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
                Options temp = Options.LoadFromDB<Options>(mDBKey);
                Options last_mData = mData;
                if (mCurOrderA==null)//避免多线程读写错误
                    mData = temp;
                else
                {
                    temp.CurAAmount = mData.CurAAmount;
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
                buyPriceB = mOrderBookB.Bids.FirstOrDefault().Value.Price;
                sellPriceB = mOrderBookB.Asks.FirstOrDefault().Value.Price;
                bidBAmount = mOrderBookB.Bids.FirstOrDefault().Value.Amount;
                askBAmount = mOrderBookB.Asks.FirstOrDefault().Value.Amount;
            } 
            //有可能orderbook bids或者 asks没有改变
            if (buyPriceA != 0 && sellPriceA != 0 && sellPriceB != 0 && buyPriceB != 0 && buyAmount != 0)
            {
                a2bDiff = (buyPriceA/sellPriceB - 1);
                b2aDiff = (sellPriceA/buyPriceB - 1);
                Diff diff = GetDiff(a2bDiff, b2aDiff,out buyAmount);
                PrintInfo(buyPriceA, sellPriceA, sellPriceB, buyPriceB, a2bDiff, b2aDiff, diff.A2BDiff, diff.B2ADiff, buyAmount, bidAAmount, askAAmount, bidBAmount, askBAmount);
                //如果盘口差价超过4usdt 不进行挂单，但是可以改单（bitmex overload 推送ws不及时）
                if (mCurOrderA == null && ((sellPriceA <= buyPriceA) || (sellPriceA - buyPriceA >= 4) || (sellPriceB <= buyPriceB) || (sellPriceB - buyPriceB >= 4)))
                {
                    Logger.Debug("范围更新不及时，不纳入计算");
                    return;
                }
                //return;
                //满足差价并且
                //只能BBuyASell来开仓，也就是说 ABuyBSell只能用来平仓
                if (a2bDiff < diff.A2BDiff && mData.CurAAmount + mData.PerTrans <= diff.MaxABuyAmount) //满足差价并且当前A空仓
                {
                    mOnTrade = true;
                    mRunningTask = A2BExchange(buyPriceA, buyAmount);
                }
                else if (b2aDiff > diff.B2ADiff && -mCurAAmount < diff.MaxASellAmount) //满足差价并且没达到最大数量
                {
                    mOnTrade = true;
                    mRunningTask = B2AExchange(sellPriceA, buyAmount);
                }
                else if (mCurOrderA != null && diff.B2ADiff >= a2bDiff && a2bDiff >= diff.A2BDiff)//如果在波动区间中，那么取消挂单
                {
                    Logger.Debug(Utils.Str2Json("在波动区间中取消订单" , a2bDiff.ToString(),"cancleID", mCurOrderA.OrderId));
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
        /// 网格，分为n段。从小范围到大范围，如果当前方向为开仓，并且小于当前最大开仓数量 那么设置diff为本段值
        /// </summary>
        /// <param name="a2bDiff"></param>
        /// <param name="b2aDiff"></param>
        private Diff GetDiff(decimal a2bDiff,decimal b2aDiff,out decimal buyAmount)
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
                if (a2bDiff < diff.A2BDiff && mCurAAmount + mData.PerTrans <= diff.MaxABuyAmount)
                {
                    if ((mCurAAmount + mData.ClosePerTrans) <= 0)
                        buyAmount = mData.ClosePerTrans;
                    break;
                }
                else if (b2aDiff > diff.B2ADiff && -mCurAAmount < diff.MaxASellAmount)
                {
                    if((mCurAAmount - mData.ClosePerTrans) >= 0)
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
            string dataUrl = $"{"http://150.109.52.225:8006/arbitrage/process?programID="}{mId}{"&symbol="}{mData.Symbol}{"&exchangeB="}{mData.ExchangeNameB.ToLowerInvariant()}{"&exchangeA="}{mData.ExchangeNameA.ToLowerInvariant()}";
            dataUrl = "http://150.109.52.225:8006/arbitrage/process?programID=" + mId + "&symbol=BTCZH&exchangeB=bitmex&exchangeA=bitmex";
            Logger.Debug(dataUrl);
            decimal lastLeverage = mData.Leverage;
            while (true)
            {
                try
                {
                    bool lastOpenPositionBuyA = mData.OpenPositionBuyA;
                    bool lastOpenPositionSellA = mData.OpenPositionSellA;
                    JObject jsonResult = await Utils.GetHttpReponseAsync(dataUrl);
                    mData.DeltaDiff = jsonResult["deltaDiff"].ConvertInvariant<decimal>();
                    mData.Leverage = jsonResult["leverage"].ConvertInvariant<decimal>();
                    mData.OpenPositionBuyA = jsonResult["openPositionBuyA"].ConvertInvariant<int>() == 0 ? false : true;
                    mData.OpenPositionSellA = jsonResult["openPositionSellA"].ConvertInvariant<int>() == 0 ? false : true;
                    var rangeList = JArray.Parse(jsonResult["profitRange"].ToStringInvariant());
                    decimal avgDiff = jsonResult["maAvg"].ConvertInvariant<decimal>();
                    if (mData.AutoCalcProfitRange)
                        avgDiff = jsonResult["maAvg"].ConvertInvariant<decimal>();
                    else
                        avgDiff = mData.MidDiff;
                    avgDiff = Math.Round(avgDiff, 4);//强行转换
                    for (int i = 0; i < rangeList.Count; i++)
                    {
                        if (i < mData.DiffGrid.Count)
                        {
                            var diff = mData.DiffGrid[i];
                            diff.ProfitRange = rangeList[i].ConvertInvariant<decimal>();
                            diff.A2BDiff = avgDiff - diff.ProfitRange + mData.DeltaDiff;
                            diff.B2ADiff = avgDiff + diff.ProfitRange + mData.DeltaDiff;
                            mData.SaveToDB(mDBKey);
                        }
                    }
                    if (lastLeverage != lastLeverage || lastOpenPositionBuyA != mData.OpenPositionBuyA || lastOpenPositionSellA != mData.OpenPositionSellA)// 仓位修改立即刷新
                    {
                        CountDiffGridMaxCount();
                    }
                    Logger.Debug(Utils.Str2Json(" UpdateAvgDiffAsync avgDiff", avgDiff));
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
            Logger.Debug(Utils.Str2Json("mCurAmount", mCurAAmount, " buyAmount",  buyAmount));
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
                            if (Math.Abs(requestA.Amount / mCurOrderA.Amount - 1) < 0.1m)//如果数量变化小于百分之10 那么不修改订单
                                return;
                        }
                        await CancelCurOrderA();
                        mCurOrderA = null;
                        return;
                    }
                    else
                    {//如果方向相反那么直接取消
                        await CancelCurOrderA();
                        return;
                    }
                };
                var v = await mExchangeAAPI.PlaceOrderAsync(requestA);
                mCurOrderA = v;
                mOrderIds.Add(mCurOrderA.OrderId);
                Logger.Debug(Utils.Str2Json("requestA", requestA.ToString()));
                Logger.Debug(Utils.Str2Json("Add mCurrentLimitOrder", mCurOrderA.ToExcleString(), "CurAmount", mData.CurAAmount));
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
                Logger.Error(Utils.Str2Json("ex", ex));
                //如果是添加新单那么设置为null 
                if (isAddNew || ex.ToString().Contains("Order already closed") || ex.ToString().Contains("Not Found"))
                {
                    mCurOrderA = null;
                    mOnTrade = false;
                }
                if (ex.ToString().Contains("405 Method Not Allowed"))//删除订单
                {
                    mCurOrderA = null;
                    mOnTrade = false;
                }
                Logger.Error(Utils.Str2Json("ex", ex));
                if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Not logged in"))
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
            string orderIDA = mCurOrderA.OrderId;
            try
            {
                await mExchangeAAPI.CancelOrderAsync(mCurOrderA.OrderId, mData.SymbolA);

            }
            catch (Exception ex)
            {
                Logger.Debug(ex.ToString());
                if (ex.ToString().Contains("CancelOrderEx"))
                {
                    await Task.Delay(5000);
                    await mExchangeAAPI.CancelOrderAsync(mCurOrderA.OrderId, mData.SymbolA);
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
                    ExchangeOrderResult backResult = await ReverseOpenMarketOrder(order);
                    mExchangePending = false;
                    // 如果 当前挂单和订单相同那么删除
                    if (mCurOrderA != null && mCurOrderA.OrderId == order.OrderId)
                    {
                        mCurOrderA = null;
                        mOnTrade = false;
                    }
                    PrintFilledOrder(order, backResult);
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
            ExchangeOrderResult backOrder = await ReverseOpenMarketOrder(order);
            PrintFilledOrder(order, backOrder);
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
                        if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Not logged in"))
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
                    if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("403 Forbidden") || ex.ToString().Contains("Not logged in") )
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
                var orderResults = await mExchangeAAPI.PlaceOrderAsync(requestA);
                ExchangeOrderResult resultA = orderResults;
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
            /// 实际开仓usd
            /// </summary>
            public decimal PerBuyUSD = 0;
            /// <summary>
            /// 实际平仓usd
            /// </summary>
            public decimal ClosePerBuyUSD = 0;
            /// <summary>
            /// 最小价格单位
            /// </summary>
            public decimal MinPriceUnit = 0.5m;
            /// <summary>
            /// 最小订单总价格
            /// </summary>
            public decimal MinOrderPrice = 0.0011m;
            /// <summary>
            /// 最小购买数量
            /// </summary>
            public decimal MinAmountA = 0.001m;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurAAmount = 0;
            /// <summary>
            /// 当前B仓位数量
            /// </summary>
            public decimal CurBAmount = 0;
            /// <summary>
            /// 当前需要平仓到数量
            /// </summary>
            public decimal CloseToA1Amount = 0;
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
            public decimal MaxABuyAmount;
            /// <summary>
            /// 最大数量
            /// </summary>
            public decimal MaxASellAmount;

            public decimal Rate = 0.5m;
        }

    }
}