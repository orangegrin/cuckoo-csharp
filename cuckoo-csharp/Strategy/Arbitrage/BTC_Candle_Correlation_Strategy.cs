
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp;
using System.Threading;
using Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Globalization;
using Tools;
using Skender.Stock.Indicators;
using StrategyHelper.Tools;

namespace cuckoo_csharp.Strategy.Arbitrage
{
    /// <summary>
    ///  主流币种 带动其他币种移动
    /// </summary>
    public class BTC_Candle_Correlation_Strategy
    {
        private IExchangeAPI mExchangeAAPI;
        private IExchangeAPI mExchangeBAPI;
        private IWebSocket mOrderBookAws;
        private ExchangeOrderBook mOrderBookA;

        private int mOrderBookAwsCounter = 0;
        private int mId;
        /// <summary>
        /// 补漏数量
        /// </summary>
        private decimal mFreeCoinAmount = 0;

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
        private bool mExchangePending = false;
        private bool mOrderBookAwsConnect = false;
        private bool mOnCheck = false;
        private bool mOnTrade = false;//是否在交易中
        private bool mOnConnecting = false;
        private bool mBuyAState;

        private decimal mCurPrice = 0;
        public BTC_Candle_Correlation_Strategy(Options config, int id = -1, bool reset = false)
        {
            mId = id;
            KeyValuePair<string, string>[] pairs = new KeyValuePair<string, string>[] { new KeyValuePair<string, string> (config.ExchangeNameA , config.OtherCoinSymbol ) }; 
            mDBKey = Utils.GetDBKey(config.StrategyName,id.ToString(), pairs);  
            RedisDB.Init(config.RedisConfig);
            mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null || reset)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            else
            {
                mData.SaveToDB(mDBKey);
            }
        }

        public void Start()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
            IExchangeAPI intitApi(string apiName, string pasword = "", string subAccount = "")
            {
                var exchangeAPI = ExchangeAPI.GetExchangeAPI(apiName);
                if (!string.IsNullOrEmpty(pasword))
                {
                    exchangeAPI.LoadAPIKeys(pasword);
                }
                if (!string.IsNullOrEmpty(subAccount))
                {
                    exchangeAPI.SubAccount = subAccount;
                }
                return exchangeAPI;
            }
            mExchangeAAPI = intitApi(mData.ExchangeNameA, mData.EncryptedFileA, mData.SubAccount);
            mExchangeBAPI = intitApi(mData.ExchangeNameB);

            GetBalanceAndPosition(mExchangeAAPI).Sync();


            //_______________________________test_____________________________________
            //             var t = mExchangeAAPI.GetWalletSummaryAsync("USD");
            //             Task.WaitAll(t);
            //             var amount =(t.Result);
            // 
            //             Logger.Debug(amount.ToString());
            //_______________________________test_____________________________________
            //             var list1 = new List<decimal>() { 1, 2, 0, 1, 2, 3, 0 };
            //             var list2 = new List<decimal>() { 1, 1, 1, 1, 1, 1, 1 };
            //             Utils.Cross(list1,
            //                 list2, out var crossUpperU, out var crossLowerU);


            //UpdateAvgDiffAsync();
            SubWebSocket();
            WebSocketProtect();
            if (!mData.OnTest)
            {
                CheckPosition();
            }

            //ChangeMaxCount();
        }
        #region Connect
        private void SubWebSocket(ConnectState state = ConnectState.All)
        {
            mOnConnecting = true;
            Thread.Sleep(3 * 1000);
            if (state.HasFlag(ConnectState.A))
            {
                mOrderBookAws = mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 20, mData.OtherCoinSymbol);
                mOrderBookAws.Connected += async (socket) => { mOrderBookAwsConnect = true; Logger.Debug("GetFullOrderBookWebSocket A 连接"); OnConnect(); };
                mOrderBookAws.Disconnected += async (socket) =>
                {
                    mOrderBookAwsConnect = false;
                    WSDisConnectAsync("GetFullOrderBookWebSocket A 连接断开");
                };
            }

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
                await Task.Delay(1000 * delayTime);
                Logger.Debug(Utils.Str2Json("mOrderBookAwsCounter", mOrderBookAwsCounter, "mOrderBookBwsCounter"));
                bool detailConnect = true;
                if (mOrderBookAwsCounter < 1 || (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    await ReconnectWS();
                }
            }
        }
        private async Task ReconnectWS(ConnectState state = ConnectState.All)
        {
            //             if (mCurOrderA != null)
            //             {
            //                 await CancelCurOrderA();
            //             }
            await CloseWS(state);
            Logger.Debug("开始重新连接ws");
            SubWebSocket(state);
            await Task.Delay(5 * 1000);
        }
        private async Task CloseWS(ConnectState state = ConnectState.All)
        {
            await Task.Delay(5 * 1000);
            Logger.Debug("销毁ws");
            if (state.HasFlag(ConnectState.A))
            {
                mOrderBookAwsConnect = false;
                mOrderBookAws.Dispose();
            }
        }

        private bool OnConnect()
        {
            bool connect = mOrderBookAwsConnect;
            if (connect)
                mOnConnecting = false;
            return connect;
        }

        private async Task WSDisConnectAsync(string tag)
        {
            //             if (mCurOrderA != null)
            //             {
            //                 await CancelCurOrderA();
            //             }
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
                /*
                if (!OnConnect())
                {
                    await Task.Delay(5 * 1000);
                    continue;
                }
                if (mData.mCurOpenOrder.OpenOrder != null || mOnTrade || mExchangePending == true)//交易正在进行或者，准备开单。检查数量会出现问题
                {
                    await Task.Delay(200);
                    continue;
                }
                */
                mOnCheck = true;
                Logger.Debug("-----------------------CheckPosition-----------------------------------");

                decimal posA=0;
                try
                {
                    //等待10秒 避免ws虽然推送数据刷新，但是rest 还没有刷新数据
                    await Task.Delay(10 * 1000);
                    decimal amount = 0;//有正负的
                    posA = Utils.GetAmoutOrPosition( mExchangeAAPI,mData.OtherCoinSymbol,mData.IsCoin);
                    if (posA == null)
                    {
                        mOnCheck = false;
                        await Task.Delay(5 * 60 * 1000);
                        continue;
                    }
                    else
                    {
                        if (mData.CurAAmount != posA)
                        {
                            Logger.Error($"数量修正！mData.CurAAmount {mData.CurAAmount} posA.Amount {posA}");
                            mData.CurAAmount = posA;
                            mData.SaveToDB(mDBKey);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.Error(Utils.Str2Json("GetOpenPositionAsync ex", ex.ToString()));
                    mOnCheck = false;
                    await Task.Delay(1000);
                    continue;
                }
                mOnCheck = false;
                await Task.Delay(5 * 60 * 1000);
            }
        }
        #endregion
        private void OnProcessExit(object sender, EventArgs e)
        {
            Logger.Debug("------------------------ OnProcessExit ---------------------------");
            mExchangePending = true;

        }
        private async Task GetBalanceAndPosition(IExchangeAPI mExchangeAAPI)
        {
            try
            {
                mData.currentBalance = mExchangeAAPI.GetWalletSummaryAsync("").Sync();
                mData.SaveToDB(mDBKey);
                Logger.Error(" currentBalance  " + mData.currentBalance);
            }
            catch (Exception ex)
            {

                Logger.Error(" currentBalance error  " + ex.ToString());
                return;
            }
        }
        /// 获取开仓数量
        /// </summary>
        /// <param name="buyPriceA"></param>
        private decimal GetOpenAmount(decimal buyPriceA)
        {
            decimal amount = mData.Leverage * mData.currentBalance / buyPriceA;
            return NormalizationMinAmountUnit(amount);
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

                {
                    temp.CurAAmount = mData.CurAAmount;
                    mData = temp;
                }
                //                 if (last_mData.OpenPositionBuyA != mData.OpenPositionBuyA || last_mData.OpenPositionSellA != mData.OpenPositionSellA)//仓位修改立即刷新
                //                     CountDiffGridMaxCount();
                await Execute();
                await Task.Delay(mData.IntervalMillisecond);
                mExchangePending = false;
            }
        }
        private bool Precondition()
        {
            if (!OnConnect())
                return false;
            if (mOrderBookA == null)
                return false;
            if (mRunningTask != null)
                return false;
            if (mExchangePending)
                return false;
            if (mOnCheck)
                return false;
            if (mOrderBookA.Asks.Count == 0 || mOrderBookA.Bids.Count == 0)
                return false;
            return true;
        }

        /// <summary>
        /// 程序分为两种状态：挂单等待开仓， 检查是否能平仓
        /// 只有第一单没有止盈，所以有可能 没有开仓，也没有止盈完成，
        /// 后面订单 止盈和 开仓单必定成交完一个
        /// 所以第一单开仓单需要追盘口，其他开仓单不追盘口，如果第一单开仓单部分成交或者超过最大开仓价格就不追盘口
        /// 
        /// 1 检查挂单 是否完全成交修改当前仓位
        /// 2 判断仓位  修改平仓价格
        /// 3 检查亏损比例，是否该挂新单，如果需要挂新单，取消上次的挂单，用实际成交数量来算新单数量
        /// 4 如果是首单，判断价格满足条件否，满足开仓
        /// </summary>
        /// <returns></returns>
        private async Task Execute()
        {
            bool hadChange = false;
            //是否完全完成过交易
            bool hadClean = false;
            decimal buyPriceA;
            decimal sellPriceA;
            decimal bidAAmount, askAAmount, bidBAmount, askBAmount;
            decimal buyAmount = mData.currentBalance;
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
            //return;
            if (buyPriceA != 0 && sellPriceA != 0)
            {
                PrintInfo(buyPriceA, sellPriceA, buyAmount, bidAAmount, askAAmount);
                //如果盘口差价超过4usdt 不进行挂单，但是可以改单（bitmex overload 推送ws不及时）
                if (((sellPriceA <= buyPriceA) || (sellPriceA - buyPriceA >= 10)))
                {
                    Logger.Debug("范围更新不及时，不纳入计算");
                    return;
                }
                //return;
                //1 检查挂单 是否完全成交修改当前仓位
                decimal curPosition = 0;
                try
                {
                    curPosition =Utils.GetAmoutOrPosition( mExchangeAAPI,mData.OtherCoinSymbol,mData.IsCoin);
                    mData.LastCheckTime = DateTime.UtcNow.AddHours(0);
                    mData.SaveToDB(mDBKey);
                }
                catch (Exception ex)
                {

                    Logger.Error(" curPosition error  " + ex.ToString());
                    return;
                }
                await CheckTrade();
                if (hadChange)//数据改变，修改数据库
                {
                    mData.SaveToDB(mDBKey);
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
                        if (ex.ToString().Contains("Invalid orderID") || ex.ToString().Contains("Not Found") || ex.ToString().Contains("Order already closed"))

                            mRunningTask = null;
                    }
                }
            }
        }
        /// <summary>
        /// 获取蜡烛图
        /// </summary>
        /// <returns></returns>
        private async Task<List<MarketCandle>> GetCandleAvg(DateTime limitTime)
        {
            var candelA = (await mExchangeAAPI.GetCandlesAsync(mData.OtherCoinSymbol, mData.GetPerSeconds())).ToList();
            mCurPrice = candelA.Last().ClosePrice;
            var candelB = (await mExchangeBAPI.GetCandlesAsync(mData.MainCoinSymbolB, mData.GetPerSeconds())).ToList();
            candelA.Sort((a, b) => { return (int)(a.Timestamp.UnixTimestampFromDateTimeSeconds() - b.Timestamp.UnixTimestampFromDateTimeSeconds()); });
            candelB.Sort((a, b) => { return (int)(a.Timestamp.UnixTimestampFromDateTimeSeconds() - b.Timestamp.UnixTimestampFromDateTimeSeconds()); });

//             foreach (var item in candelB)//币安的时区为utc,ftx为cn
//             {
//                 item.Timestamp= item.Timestamp.AddHours(0);
//             }

            foreach (var item in candelA)//币安的时区为utc,ftx为cn
            {
                item.Timestamp = item.Timestamp.AddHours(-8);
            }
            int count = 200;
            Logger.Debug(candelA + "count " + candelA.Count);
            Logger.Debug(candelB + "count " + candelB.Count);
            //Logger.Debug("---------------------------count 0" );
            //删除没有完全形成的k线

            var lastA = candelA.Last();
            var lastB = candelB.Last();
            //Logger.Debug("---------------------------count 1");
            if (lastA.Timestamp> limitTime)
            {
                candelA.RemoveAt(candelA.Count - 1);
            }
            //Logger.Debug("---------------------------count 2");
            if (lastB.Timestamp > limitTime)
            {
                candelB.RemoveAt(candelB.Count - 1);
            }
            //Logger.Debug("---------------------------count 3");
            candelA = candelA.GetRange(candelA.Count - count, count);
            candelB = candelB.GetRange(candelB.Count - count, count);
            
            List<MarketCandle> candles = new List<MarketCandle>();
            for (int i = 0; i < candelA.Count; i++)
            {
                candelA[i].OpenPrice = (candelA[i].OpenPrice + candelB[i].OpenPrice) / 2;
                candelA[i].HighPrice = (candelA[i].HighPrice + candelB[i].HighPrice) / 2;
                candelA[i].LowPrice = (candelA[i].LowPrice + candelB[i].LowPrice) / 2;
                candelA[i].ClosePrice = (candelA[i].ClosePrice + candelB[i].ClosePrice) / 2;
                var candle = candelA[i];
                candles.Add(candelA[i]);
            }
            //Logger.Debug("---------------------------count 4");
            Logger.Debug(mData.OtherCoinSymbol +"最后的k"+candelA.Last().ToString());
            Logger.Debug(mData.MainCoinSymbolB + "最后的k" + candelB.Last().ToString());
            return candles;
        }

        private List<Quote> GetNewCandel(List<MarketCandle> candles)
        {
            /*
             h=pow(candle_high,2) / 2
            l=pow(candle_low,2) / 2
            o=pow(candle_open,2) /2
            c=pow(candle_close,2) /2
            x=(h+l+o+c) / 4
            y= sqrt(x)
             */


            List<Quote> sources = new List<Quote>();
            foreach (var item in candles)
            {
                var g = 2 ^ 2;
                var o = Math.Pow((double)item.OpenPrice, 2) / 2;
                var h = Math.Pow((double)item.HighPrice, 2) / 2;
                var l = Math.Pow((double)item.LowPrice, 2) / 2;
                var c = Math.Pow((double)item.ClosePrice, 2) / 2;
                var x = (o + h + l + c) / 4;
                var y = Math.Sqrt(x);
                sources.Add(new Quote()
                {
                    Close = (decimal)y,
                    Date = item.Timestamp

                });
            }
            return sources;
        }
        bool FirstGet = true;
        List<MarketCandle> lastZipAndSma;
        Task<ExchangeOrderResult> mRunningTask = null;
        private DateTime? LastGetSmaTime;
        //上次获取是否成功
        bool mLastSuccess = true;
        public async Task CheckTrade()
        {
            DateTime utcNow = DateTime.UtcNow;
            DateTime cnNow = utcNow.AddHours(0);
            int perTimeSeconds = mData.GetPerSeconds();
            IEnumerable<MarketCandle> smaCandles;

            int checkPerSeconds = mData.GetCheckPerSeconds();
            bool needCheck = !mLastSuccess;
            if (!needCheck)//如果上次检查成功，查看是否需要下次检查
            {
                //如果第一次获取，或者没有缓存值，或者没有最新一次获取时间 必须获取一次
                if (FirstGet || LastGetSmaTime == null)
                {
                    if (LastGetSmaTime == null)
                    {
                        LastGetSmaTime = TimeUnit.GetLastStartTime(cnNow, mData.TimeSpanUnit).AddSeconds(-perTimeSeconds);
                        Logger.Debug($"首次获取 LastGetSmaTime {LastGetSmaTime}");
                    }
                    FirstGet = false;
                }
                if ((cnNow - LastGetSmaTime.Value).TotalSeconds > (perTimeSeconds + checkPerSeconds))//上周期结束，需要操作
                {
                    LastGetSmaTime = LastGetSmaTime.Value.AddSeconds(perTimeSeconds);
                    Logger.Debug($"再次获取 LastGetSmaTime {LastGetSmaTime}");
                    needCheck = true;
                }
                 else
                {
                    needCheck = false;
                    Logger.Debug($"不用获取新的 LastGetSmaTime {LastGetSmaTime}");
                }
            }
            if (needCheck)//如果需要检查，开始检查
            {
                try
                {
                    var limitTime = TimeUnit.GetLastStartTime(cnNow, mData.TimeSpanUnit).AddSeconds(-1); //LastGetSmaTime.Value.AddSeconds(-perTimeSeconds);
                    var mainKLine = await GetCandleAvg(limitTime);
                    //删除最后一根,不完全的k线,已经删除过了
                    //mainKLine.RemoveAt(mainKLine.Count - 1);
                    var last = mainKLine.Last();
                    lastZipAndSma = mainKLine;
                    if ((LastGetSmaTime.Value - last.Timestamp).TotalSeconds >= perTimeSeconds)//上次获取的时间在本周期节点之后
                    {
                        try
                        {
                            await TradeCheck(lastZipAndSma, mCurPrice); 
                            //decimal lossPrice = isBuy ? (res.AveragePrice - otherCoinPrice) : (otherCoinPrice - res.AveragePrice);
                            //Logger.Debug($" 理论开仓价格{otherCoinPrice}  实际开仓价格 {res.AveragePrice} 亏损价差值 { lossPrice } 亏损价差比 {lossPrice / otherCoinPrice}");
                            await Task.Delay(5000);
                            await GetBalanceAndPosition(mExchangeAAPI);
                        }
                        catch (System.Exception ex)
                        {
                            Logger.Error(Utils.Str2Json("mRunningTask ex", ex));
                            if (mExchangeAAPI.ErrorNeedNotCareError(ex))
                            {
                                mLastSuccess = false;
                            }
                            else
                            {
                                //throw ex;
                            }
                        }
                    }
                    else
                    {
                        Logger.Debug($"最小小时线没有出现，等待再次获取");
                        mLastSuccess = false;
                    }
                    //await Task.Delay(1 * 1000);
                }
                catch (System.Exception ex)
                {
                    Logger.Error("获取蜡烛图抛错" + ex.ToString());
                    mLastSuccess = false;
                    return;
                }
            }
        }
        public async Task TradeCheck(List<MarketCandle> candles, decimal curPrice)
        {
            mLastSuccess = false;
            var source = GetNewCandel(candles);
            Logger.Error("candles____________________________________________________________________________________");
            Logger.Debug(candles.Last().ToString());
            Logger.Error("source____________________________________________________________________________________");
            Logger.Debug(source.Last().ToString());



            var ma = Indicator.GetSma(source, mData.Len).ToList();
            var maList = (from m in ma
                          where m.Sma != null
                          select m.Sma).ToList();




            var range = MarketCandle.Calculate(MarketCandle.CandleKind.H, candles, MarketCandle.CandleKind.L, candles, MarketCandle.Candlesymbolc.sub);
            List<Quote> historyRange = new List<Quote>();

            for (int i = 0; i < range.Count; i++)
            {
                var item = range[i];
                var date = candles[i].Timestamp;
                historyRange.Add(new Quote()
                {
                    Close = item
                    ,
                    Date = date
                });
            }
            var rangema = Indicator.GetSma(historyRange, mData.Len).ToList();

            var rangemaList = (from r in rangema
                               where r.Sma != null
                               select r.Sma
                              ).ToList();


            //ma + rangema * mData.Mult;

            List<decimal> upper = new List<decimal>();
            List<decimal> lower = new List<decimal>();
            for (int i = 0; i < maList.Count; i++)
            {
                var m = maList[i];
                var r = rangemaList[i];

                upper.Add(m.Value + r.Value * mData.Mult);
                lower.Add(m.Value - r.Value * mData.Mult);
            }

            var realSource = (from s in source
                              select s.Close).ToList().GetRange(mData.Len - 1, upper.Count);
            Utils.Cross(realSource,
                upper, out var crossUpperU, out var crossLowerU);
            Logger.Debug("crossUpperU:");
            foreach (var item in crossUpperU)
            {
                Logger.Debug("crossUpperU 交叉对应k:" + realSource[item] + "  " + source[item + mData.Len - 1]);
            }



            Utils.Cross(realSource,
                lower, out var crossUpperL, out var crossLowerL);
            Logger.Debug("crossLowerL:");
            foreach (var item in crossLowerL)
            {
                Logger.Debug("crossLowerL 交叉对应k:" + realSource[item] + "  " + source[item + mData.Len - 1]);
            }


            // 多仓条件，上穿
            bool crossBuyCond = crossUpperU.Count > 0 && (crossUpperU.Last() + 1 == realSource.Count);
            // 空仓条件，下穿
            bool crossSellCond = crossLowerL.Count > 0 && (crossLowerL.Last() + 1 == realSource.Count);
            //最近一个是多信号，上一个是空信号
            bool lastBuyLastTowSell = false;
            if (crossUpperU.Count > 1 && crossLowerL.Count > 0)
            {
                var lastBuy = source[crossUpperU.Last() + mData.Len - 1];
                var lastSencondBuy = source[crossUpperU[crossUpperU.Count - 2] + mData.Len - 1];
                var lastSell = source[crossLowerL.Last() + mData.Len - 1];
                lastBuyLastTowSell = lastBuy.Date > lastSell.Date && lastSell.Date > lastSencondBuy.Date;

                Logger.Debug($"lastSell {lastSell} lastBuy.Date {lastBuy.Date} lastSencondBuy {lastSencondBuy}");
            }
            // 平多仓条件，1有多仓，2 close < ma || candle_high> 开仓的close
            //bool cancelBuyCond = mData.CurAAmount > 0 && (realSource.Last() < maList.Last() || candles.Last().HighPrice >= mData.OpenPriceAvg);
            // 平空仓条件，1有空仓，2 close > ma || candle_high< 开仓的close
            //bool cancelSellCond = mData.CurAAmount<0 && (realSource.Last() > maList.Last() || candles.Last().LowPrice <= mData.OpenPriceAvg);

            string hadStr = crossSellCond ?" 空" : crossBuyCond ? " 多":"  ";

            Logger.Debug(hadStr);
            EmailHelper.SenMail(hadStr +"   "+ mData.TimeSpanUnit , hadStr + "   " + mData.TimeSpanUnit+" BTC_Candle_Correlation_Strategy", 10);
            if (mData.CurAAmount > 0)
            {//下穿平仓
                if (crossSellCond)
                {
                    //平多仓
                    Logger.Debug("平仓" + mData.CurAAmount);
                    //TODO test
                    mData.CurAAmount = 0;
                    mData.SaveToDB(mDBKey);

                    //EmailHelper.SenMail("平空仓操作"+ hadStr+mData.TimeSpanUnit+"BTC_Candle_Correlation_Strategy" , "平空仓操作 BTC_Candle_Correlation_Strategy", 10);
                    //TODO test
                    //await DoTrade(false, mData.CurAAmount);
                }
                else
                {
                    //EmailHelper.SenMail("本周期没有操作"+ hadStr+mData.TimeSpanUnit+"BTC_Candle_Correlation_Strategy", "平空仓操作 BTC_Candle_Correlation_Strategy", 10);
                }
            }//上穿，并且今日线上涨，则开仓
            else if (crossBuyCond )//&& candles.Last().ClosePrice > candles.Last().OpenPrice)
            {
                if (lastBuyLastTowSell)
                {
                    var amount = GetOpenAmount(curPrice);
                    Logger.Debug("开仓" + amount);
                    mData.CurAAmount = amount;
                    mData.SaveToDB(mDBKey);
                    //EmailHelper.SenMail("开多仓操作 "+ hadStr+mData.TimeSpanUnit+"BTC_Candle_Correlation_Strategy", "开多仓操作 BTC_Candle_Correlation_Strategy", 10);
                    //await DoTrade(true, amount);
                }
                else
                {
                    Logger.Debug("不满足上一个信号为平仓");
                    //EmailHelper.SenMail("不满足上一个信号为平仓"+ hadStr+mData.TimeSpanUnit+"BTC_Candle_Correlation_Strategy", "不满足上一个信号为平仓 BTC_Candle_Correlation_Strategy", 10);
                }
                    
            }
            else
            {
                //EmailHelper.SenMail("本周期没有操作"+ hadStr+mData.TimeSpanUnit+"BTC_Candle_Correlation_Strategy", "本周期没有操作 BTC_Candle_Correlation_Strategy", 10);
            }
            
            mData.LastUpdateTime = DateTime.UtcNow.AddHours(0);
            mData.LastPerSign = crossBuyCond ? Sign.Buy : crossSellCond ? Sign.Sell : Sign.None;
            mData.SaveToDB(mDBKey);
            mLastSuccess = true;
        }
        /// <summary>
        /// 开始开仓
        /// </summary>
        private async Task<ExchangeOrderResult> DoTrade(bool isBuy, decimal amount)
        {
            var req = new ExchangeOrderRequest();

            req.Amount = NormalizationMinAmountUnit(amount); ;
            req.IsBuy = isBuy;
            req.OrderType = OrderType.Market;
            req.MarketSymbol = mData.OtherCoinSymbol;
            Logger.Debug("----------------------------DoTrade---------------------------");
            Logger.Debug(req.ToStringInvariant());
            for (int i = 1; ; i++)//当B交易所也是bitmex， 防止bitmex overload一直提交到成功
            {
                try
                {
                    var res = await mExchangeAAPI.PlaceOrderAsync(req);
                    mCurAAmount += req.IsBuy ? amount : -amount;
                    Logger.Debug("--------------------------------DoTrade Result-------------------------------------");
                    Logger.Debug($"实际开仓价格：" + res.ToString());
                    return res;
                    break;
                }
                catch (Exception ex)
                {
                    if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("403 Forbidden") || ex.ToString().Contains("Not logged in") || ex.ToString().Contains("Rate limit exceeded") || ex.ToString().Contains("Please try again later") || mExchangeAAPI.ErrorTradingSyatemIsBusy(ex))
                    {
                        Logger.Error(Utils.Str2Json("req", req.ToStringInvariant(), "ex", ex));
                        await Task.Delay(2000);
                    }
                    else if (ex.ToString().Contains("RateLimitError"))
                    {
                        Logger.Error(Utils.Str2Json("req", req.ToStringInvariant(), "ex", ex));
                        await Task.Delay(5000);
                    }
                    else
                    {
                        Logger.Error(Utils.Str2Json("DoTrade抛错", ex.ToString()));
                        throw ex;
                    }
                }
            }
        }
        private void PrintInfo(decimal bidA, decimal askA, decimal buyAmount,
            decimal bidAAmount, decimal askAAmount)
        {
            Logger.Debug("================================================");
            Logger.Debug(Utils.Str2Json("Bid A", bidA, "bidAAmount", bidAAmount, "bidBAmount"));
            Logger.Debug(Utils.Str2Json("Ask A", askA, "askAAmount", askAAmount, "askBAmount"));
            Logger.Debug(Utils.Str2Json("mCurAmount", mCurAAmount, " buyAmount", buyAmount));
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
        decimal NormalizationMinPriceUnit(decimal price)
        {
            var s = 1 / mData.MinPriceUnit;
            return Math.Round(price * s) / s;
        }
        decimal NormalizationMinAmountUnit(decimal amount)
        {
            var s = 1 / mData.MinAmountUnit;
            return Math.Floor(amount * s) / s;
        }

        public class Options
        {
            public string ExchangeNameA = "FTX";
            public string ExchangeNameB = "BinanceDM";
            public string OtherCoinSymbol = "ETH-PERP";
            public string MainCoinSymbolA = "BTC-PERP";
            public string MainCoinSymbolB = "BTCUSDT";
            public string BalanceSymbol;
            public string StrategyName = "BTC_Candle_Correlation_Strategy";

            /// <summary>
            /// 最小价格单位
            /// </summary>
            public decimal MinPriceUnit = 0.5m;
            /// <summary>
            /// 最小订单总价格
            /// </summary>
            public decimal MinOrderAmount = 1m;
            /// <summary>
            /// 最小购买数量
            /// </summary>
            public decimal MinAmountUnit = 0.1m;



            /// <summary>
            /// 间隔时间
            /// </summary>
            public int IntervalMillisecond = 500;
            /// <summary>
            /// 自动计算最大开仓数量
            /// </summary>
            public bool AutoCalcMaxPosition = true;
            /// <summary>
            /// A交易所加密串路径
            /// </summary>
            public string EncryptedFileA = "FTX_balance8800";
            /// <summary>
            /// B交易所加密串路径
            /// </summary>
            public string EncryptedFileB = "FTX_balance8800";
            /// <summary>
            /// redis连接数据
            /// </summary>
            public string RedisConfig = "localhost,password=l3h2p1w0*";
            /// <summary>
            /// 子账号标识
            /// </summary>
            public string SubAccount = "";
            /// <summary>
            /// 是否在测试
            /// </summary>
            public bool OnTest = true;


            #region 策略参数
            /// <summary>
            /// 开仓倍率
            /// </summary>
            public decimal Leverage = 0.5m;
            /// <summary>
            /// ma len
            /// </summary>
            public int Len = 9;
            /// <summary>
            /// 乘数
            /// </summary>
            public decimal Mult = 0.9m;
            /// <summary>
            /// SMA 的时间单位
            /// </summary>
            public string TimeSpanUnit = TimeUnit.Min_1;
            /// <summary>
            /// 检查时间 > 这个值才开仓
            /// </summary>
            public int CheckMin = 20;
            /// <summary>
            /// 检查时间的单位
            /// </summary>
            public string CheckUnit = TimeUnit.Second;
            #endregion

            #region 程序运行参数
            /// <summary>
            /// 初始价值,单位usd，可能直接超过最大本金（相当于开高杠杆）
            /// </summary>
            public decimal currentBalance;
            /// <summary>
            /// 首单开仓时 k线close价格
            /// </summary>
            public decimal StartPrice;
            /// <summary>
            /// 当前开仓均价
            /// </summary>
            public decimal OpenPriceAvg;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurAAmount = 0;
            /// <summary>
            /// 上次周期信号
            /// </summary>
            public Sign LastPerSign = Sign.None;
            /// <summary>
            /// 买币，还是买合约
            /// </summary>
            public bool IsCoin = true;
            /// <summary>
            /// 上次成交时间
            /// </summary>
            public DateTime LastUpdateTime;
            /// <summary>
            /// 上次刷新
            /// </summary>
            public DateTime LastCheckTime;

            #endregion

            public void SaveToDB(string DBKey)
            {

                RedisDB.Instance.StringSet(DBKey, this);
            }
            public static T LoadFromDB<T>(string DBKey)
            {
                return RedisDB.Instance.StringGet<T>(DBKey);
            }
            public int GetPerSeconds()
            {
                return TimeUnit.GetAllSeconds(TimeSpanUnit);
            }
            public int GetCheckPerSeconds()
            {
                return TimeUnit.GetAllSeconds(CheckUnit) * CheckMin;
            }
        }

        [Flags]
        private enum ConnectState
        {
            All = 7,
            A = 1,
            B = 2,
            Orders = 4
        }

        public enum Sign
        {
            None,//没有信号
            Buy,//买
            Sell,//卖
        }

    }
}