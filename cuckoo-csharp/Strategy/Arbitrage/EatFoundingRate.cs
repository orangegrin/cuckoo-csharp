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
using cuckoo_csharp.Entity;
using System.ComponentModel.Design;
using System.Net;

namespace cuckoo_csharp.Strategy.Arbitrage
{
    /// <summary>
    /// 测试画k线
    /// 
    /// 1获取某交易所对应币种 orderbook数据  x
    /// 2根据 通过一定策略添加 copy  x 的orderbook 提交订单
    /// 
    /// </summary>
    public class EatFoundingRate
    {
        private string url = "http://172.16.0.111:8000";
        private string apikey = "8d7d9eb310598869dd84f18f01a8fdb3";
        private string secret = "ebde29f473bd160a62fcd6096ee4459f";
        private int mId;
        private Options mData { get; set; }


        private LimitMaker mMaker ;

        private string mDBKey;
        private Task mRunningTask;
        private bool mExchangePending = false;
        private bool mOrderwsConnect = false;
        private bool mOrderBookAwsConnect = false;
        private bool mOrderBookBwsConnect = false;
        private bool mOnCheck = false;
        private bool mOnTrade = false;//是否在交易中
        private bool mOnConnecting = false;
        private bool mBuyAState;
        private decimal mAllPosition;
        /// <summary>
        /// 指数价格
        /// </summary>
        private decimal mIndexPrice;
        /// <summary>
        /// 每次购买数量
        /// </summary>
        private decimal mPerBuyAmount = 0;
        private DateTime mlastFillTime;
        //第一次提交接近标记价格订单
        private bool mIsFirst = true;

        private RedisDBSubscriber dBSubscriber;
        /// <summary>
        /// 当前挂单次数
        /// </summary>
        private int mCurAddOrderTimes = 0;
        /// <summary>
        /// 存放 结算前 分钟的（变化 orderbook）
        /// </summary>
        private List<List<ExchangeOrderBook>> mBookList = new List<List<ExchangeOrderBook>>();
        

        public EatFoundingRate(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("EatFoundingRate:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
            RedisDB.Init(config.RedisConfig);
            //TODO 取消注释
            //mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            //TODO
            mIndexPrice = 346.3m;
            //订阅 指数
            dBSubscriber = new RedisDBSubscriber(mData.RedisConfigData_ZBG);
            dBSubscriber.SubScribe(mData.RedisChannle, (indexString) =>
            {
                BtcGlodIndex index = JsonConvert.DeserializeObject<BtcGlodIndex>(indexString);
                mIndexPrice = index.index;
                Logger.Debug("mIndexPrice:" + mIndexPrice +" ts :"+ index.timestamp);
            });

            mlastFillTime = DateTime.UtcNow.AddDays(-1);

        }
        private void OnOrderBookChange(ExchangeOrderBook orderBook )
        {
            if (mFirstInDataConnection)
            {
                try
                {
                    List<KeyValuePair<decimal, ExchangeOrderPrice>> asks;
                    List<KeyValuePair<decimal, ExchangeOrderPrice>> bids;
                    lock (orderBook)
                    {
                        int countA = Math.Min(orderBook.Asks.Count, mData.LevelCount);
                        int countB = Math.Min(orderBook.Bids.Count, mData.LevelCount);
                        asks = orderBook.Asks.Take(countA).ToList();
                        bids = orderBook.Bids.Take(countB).ToList();
                    }
                    //0开始

                    int idx = mLastOhlcMinute - mData.BeginTime;
                    if (idx < mData.OhlcCount)//接受开始结算前10分钟数据变化
                    {
                        if (mBookList.Count < (idx + 1))
                        {
                            mBookList.Add(new List<ExchangeOrderBook>());
                        }
                        var bookList = mBookList[idx];
                        var changeBook = new ExchangeOrderBook();

                        foreach (var item in asks)
                        {
                            changeBook.Asks[item.Value.Price] = item.Value;
                        }

                        foreach (var item in bids)
                        {
                            changeBook.Bids[item.Value.Price] = item.Value;
                        }
                        bookList.Add(changeBook);
                    }
                }
                catch (Exception ex)
                {

                    throw ex;
                }
            }
        }

        public void Start()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
            /* TODO
            //             UpdateAvgDiffAsync();
            foreach (var maker in limitMakers)
            {
                maker.Init();
                maker.CancelAllOrders();
            }



            Update();
            */

            // 
            //             var v1 = QueryAndChangeMarketParamsAsync(true);
            //             Task.WaitAll(v1);
            //             Task.WaitAll(Task.Delay(2000));
            //             var v2 = ChangeParams(5, 0, 100, 0, 1, 1);
            //             Task.WaitAll(v2);
            //             Task.WaitAll(Task.Delay(10000));
            //             var v3 = ChangeParams(5, 0, 200, 0, 1, 1);
            //             Task.WaitAll(v3);
            //             Task.WaitAll(Task.Delay(10000));
            //             var v4 = QueryAndChangeMarketParamsAsync(false);
            //             Task.WaitAll(v4);
            //var v = mMaker.exchangeAPI.GetCandlesAsync(mData.SymbolA,60);
            //Task.WaitAll(v);

            Test();

            apikey = mData.Public;
            secret = mData.Private;
            mMaker = new LimitMaker(mData.limitMaker, mData.ExchangeNameA, mData.SymbolA);
            //mMaker.mOnOrderBookChange = OnOrderBookChange;
            mMaker.Init();
            Task.WaitAll(Task.Delay(3000));//等ws 连上
            Update();
            
            var isOpen =  QueryAndChangeMarketParamsAsync(true);
            Task.WaitAll(isOpen);

//             var dd = ChangeParams(1, 1, 0.6m, mData.xau_btc_weight[0], mData.xau_btc_weight[1], mData.xau_btc_weight[2]);
//             Task.WaitAll(dd);

        }

        private void Test()
        {
            //                         /// <summary>
            //                         /// 开始计算时间
            //                         /// </summary>
            //         public int BeginTime = 18;
            //         /// <summary>
            //         /// 修改参数停止时间,结算开始的小时 添加 这个分钟 就是完成时间
            //         /// </summary>
            //         public int EndEatTime = 26;
            //         /// <summary>
            //         /// 需要累积的开高低收数量
            //         /// </summary>
            //         public int OhlcCount = 3;
            //         /// <summary>
            //         /// 最小 高开低收 值。小于该值本次不进行套利
            //         /// </summary>
            //         public int MinOhloCount = 2;
            //         /// <summary>
            //         /// 开仓时间 58:01-58:59
            //         /// </summary>
            //         public int OpenPositionTime = 58;
            //         /// <summary>
            //         /// 开仓时间 59:01-59:59
            //         /// </summary>
            //         public int ClosePositionTime = 59;
            mData.BeginTime = (DateTime.UtcNow.Minute-3);
            mData.EndEatTime = mData.BeginTime + mData.OhlcCount + 5;
            mData.OpenPositionTime = mData.BeginTime + mData.OhlcCount ;
            mData.ClosePositionTime = mData.OpenPositionTime + 1;
            mData.SaveToDB(mDBKey);
            Logger.Debug("EndEatTime" + mData.EndEatTime + " OpenPositionTime" + mData.OpenPositionTime + " ClosePositionTime" + mData.ClosePositionTime);
        
    }

        #region 调整函数
        /// <summary>
        /// 查询函数原型
        /// 查询函数 参数和鉴权，json，post：private String  apiKey;
        ///         Integer sig;
        ///         String sign;//
        ///         String nonce;//时间戳或随机串
        ///sign = md5(apikey+nonce+secretkey+sig)
        /// </summary>
        private async Task<bool> QueryAndChangeMarketParamsAsync(bool? isOpen = null)
        {
            //return true;
            string nonce = DateTime.UtcNow.UnixTimestampFromDateTimeMilliseconds().ToString();
            int? sig = null;
            if (isOpen.HasValue)
            {
                sig = isOpen.Value ? 1 : 0;
            }
            else
                sig = null;
            string sigString = "";
            if (sig.HasValue)
            {
                sigString = sig.Value.ToString();
            }
            string signStr = apikey + nonce + secret+ sigString;
            string sign = CryptoUtility.MD5Sign(signStr).ToLower();
            Logger.Debug("signStr:" + signStr);
            Logger.Debug(sign);
            string query = "/whisper/queryfunc";
            Dictionary<string, object> paylod = new Dictionary<string, object>() { { "apiKey", apikey}, 
            { "sign", sign},
            { "nonce", nonce}};
            if (sig.HasValue)
            {
                paylod.Add("sig", sig.Value);
            }
            else
                paylod.Add("sig", " ");
            Dictionary<string, string> header = new Dictionary<string, string>() { { "Content-Type", "application/json" } };
            //Content-Type
            var jo = Utils.Post(mData.RestIp + query, paylod, header);
            Logger.Debug("QueryAndChangeMarketParamsAsync::"+jo.ToString());
            QueryAndChangeMarketReturn returns = JsonConvert.DeserializeObject<QueryAndChangeMarketReturn>(jo.ToString());
            return returns.makerStatus == 1;
        }
        /*
         BBSee:
修正函数：/whisper/adjustfunc

BBSee:
private  Integer  t_dur; private  Integer t_adj;private List<Double> adjParams;

BBSee:
private String  apiKey;
private String  sign;
private String  nonce;

        [p_adj,xau,btc,weight]：浮点型
         *//// <summary>
         /// /
         /// </summary>
         /// <param name="t_dur"></param> 持续时间
         /// <param name="t_adj"></param>延迟指数发布时间
         /// <param name="p_adj"></param>
         /// <param name="xau"></param>
         /// <param name="btc"></param>
         /// <param name="weight"></param>
         /// <returns></returns>
        public async Task ChangeParams(int t_dur,decimal t_adj, decimal p_adj =0, float xau =0, float btc =0, float weight =0 )
        {
            p_adj = Math.Ceiling(p_adj / mData.MinPriceUnit) * mData.MinPriceUnit;
            Logger.Debug($"ChangeParams: t_dur:{t_dur} t_adj:{t_adj} p_adj:{p_adj}");
            //return;
            string nonce = ((Int64)(DateTime.UtcNow.UnixTimestampFromDateTimeSeconds())).ToString();
            string signStr = apikey + JsonConvert.SerializeObject(btc) + nonce + JsonConvert.SerializeObject(p_adj) + secret + t_adj + t_dur + JsonConvert.SerializeObject( weight) + JsonConvert.SerializeObject(xau);//adjParamsJson + apikey + nonce + secret + adjustTime+ duringTimes;
            string sign = CryptoUtility.MD5Sign(signStr).ToLower();
            Dictionary<string, object> paylod = new Dictionary<string, object>() { { "apiKey", apikey},
            {"sign", sign },
            { "p_adj", p_adj},
            { "xau", xau},
            { "btc", btc},
            { "weight", weight},
            { "nonce", nonce},
            { "t_dur", t_dur},
            { "t_adj", t_adj}};
            paylod = CryptoUtility.AsciiSortDictionary(paylod);
            var str =CryptoUtility.GetJsonForPayload(paylod);
            Logger.Debug("signStr:" + signStr+ "str"+ str);
            Logger.Debug(sign);
            string query = "/whisper/adjustfunc";
            Logger.Debug("++++++++++++++++++++++++++++++++++++++++++++++sendsend!!!++++++++++++++++++++");
            Dictionary<string, string> header = new Dictionary<string, string>() { { "Content-Type", "application/json" } };
            //Content-Type
            var jo =  Utils.Post(mData.RestIp + query, paylod, header);
            Logger.Debug("++++++++++++++++++++++++++++++++++++++++++++++back!!!++++++++++++++++++++"+jo.ToString());
            //QueryAndChangeMarketReturn returns = JsonConvert.DeserializeObject<QueryAndChangeMarketReturn>(jo.ToString());

        }

        #endregion
        private void OnProcessExit(object sender, EventArgs e)
        {
            Logger.Debug("------------------------ OnProcessExit ---------------------------");
            mExchangePending = true;


                mMaker.CancelAllOrders();
            
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
                var item = mMaker;
                    if (!item.OnConnect())
                    {
                        await Task.Delay(5 * 1000);
                        continue;
                    }
                    await item.CountDiffGridMaxCount();
                

                await Task.Delay(2 * 3600 * 1000);
            }
        }
        /// <summary>
        ///刷新
        /// </summary>
        private async void Update()
        {
            //             var v1 = await QueryAndChangeMarketParamsAsync(true);
            //             await Task.Delay(2000);
            //             await ChangeParams(2, 0, 100, 0, 1, 1);
            //             await Task.Delay(3000);
            //             await ChangeParams(2, 0, 200, 0, 1, 1);
            //             await Task.Delay(3000);
            //             await QueryAndChangeMarketParamsAsync(false);


            while (true)
            {
                mCurAddOrderTimes++;
                await DoTrade();
                await Task.Delay(mData.IntervalMillisecond_1);

            }
        }

        async Task DoTrade()
        {
            if (Precondition())
            {
                mExchangePending = true;
                Options temp = Options.LoadFromDB<Options>(mDBKey);
                Options last_mData = mData;
                //                 if (mCurOrderA==null)//避免多线程读写错误
                //                     mData = temp;
                //                 else
                {
                    //mData = temp;
                }
                //                 if (last_mData.OpenPositionBuyA != mData.OpenPositionBuyA || last_mData.OpenPositionSellA != mData.OpenPositionSellA)//仓位修改立即刷新
                //                     CountDiffGridMaxCount();
                try
                {
                    await Execute();
                }
                catch (Exception ex)
                {

                    Logger.Error("DoTradeDoTrade ex:" + ex);
                }
                //await Execute();
                //await Task.Delay(mData.IntervalMillisecond);
                mExchangePending = false;
            }
        }
        private bool Precondition()
        {
            if (mRunningTask != null)
                return false;
            if (mExchangePending)
                return false;
            if (mOnCheck)
                return false;
            //             if (mOrderBookA.Asks.Count == 0 || mOrderBookA.Bids.Count == 0 || mOrderBookB.Bids.Count == 0 || mOrderBookB.Asks.Count == 0)
            //                 return false;
            if (!mData.OpenProcess)
                return false;
            return true;
        }
        /// <summary>
        /// 返回第 idx 分钟的，第level 档 均N价差
        /// </summary>
        /// <param name="idx"></param>
        /// <param name="level"></param>
        /// <returns></returns>
        private EatData GetMinuteOrderBookAvg(int idx, int level)
        {
            //N是指盘口N档的一分钟均值
            var _bookList = mBookList[idx];

            var allPrice = _bookList.Sum((book) =>
            {
                var asks = book.Asks.Take(level).ToList();

                var bids = book.Bids.Take(level).ToList();

                return asks[level].Value.Price - bids[level].Value.Price;
            });
            var avgPrice = allPrice / _bookList.Count;
            var allNum = _bookList.Sum((book) =>
            {
                var asks = book.Asks.Take(level).ToList();
                var bids = book.Bids.Take(level).ToList();
                //min（卖1-N挂单量之和，买1-N价挂单量之和）
                return Math.Min( asks.Take(level).Sum((ask)=> { return ask.Value.Amount; }), bids.Take(level).Sum((bid) => { return bid.Value.Amount; }));
            });
            var avgNum = allNum / _bookList.Count;

            return new EatData() { DiffAvgByLv= avgPrice , AmountAvgByLv= avgNum };
        }
        /// <summary>
        /// 均N价差均值=mean(过去数据的均N价差)
        /// 均M量均值=mean(过去数据的均M量)
        /// </summary>
        /// <param name="level"></param>
        /// <returns></returns>
        private EatData AllMinuteOrderBookAvg(int level)
        {
            EatData avgAll = new EatData();
            for (int i = 0; i < mBookList.Count; i++)
            {
                var oneMin = GetMinuteOrderBookAvg(i, level);
                avgAll.DiffAvgByLv += oneMin.DiffAvgByLv;
                avgAll.AmountAvgByLv = oneMin.AmountAvgByLv;
            }
            avgAll.DiffAvgByLv /= mBookList.Count;
            avgAll.AmountAvgByLv /= mBookList.Count;
            Logger.Debug("all" + avgAll);
            return avgAll;
        }

        /// <summary>
        /// 结算完成清理数据
        /// </summary>
        private void CleanDatas()
        {
            mOnFounding = false;
            mFirstInDataConnection = false;
            mLastOhlcMinute = 0;
            mBookList.Clear();
        }

        private List<MarketCandle> mMarketCandles = new List<MarketCandle>();
        /// <summary>
        /// 是否在 启动策略中
        /// </summary>
        private bool mOnFounding = false;
        /// <summary>
        /// 第一次进入 数据收集阶段
        /// </summary>
        private bool mFirstInDataConnection = false;
        /// <summary>
        /// 上次记录时间
        /// </summary>
        private int mLastOhlcMinute = 0;

        bool mIsFistChangeIndex = false;
        bool mIsSencondChangeIndex = false;
        bool mIsThirdChangeIndex = false;

        /// <summary>
        /// ChoiceTradeDirectionStatu 选择套利方向
        /// 根据套利方向开仓
        /// 根据套利方向平仓
        /// 超过结算时间 平掉所有仓位完成本次套利
        /// EndAllStatu
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task Execute()
        {
            if (Precondition())
                return;
            DateTime now = DateTime.UtcNow;
            //有可能orderbook bids或者 asks没有改变
            //if (buyPriceA != 0 && sellPriceA != 0 && sellPriceB != 0 && buyPriceB != 0 && buyAmount != 0)
            if (mIndexPrice != 0)
            {
                //本次 结算套利 起始时间
                bool isFirstInClose1 = true;
                bool isFirstInClose2 = true;
                bool isFirstInClose3 = true;
                bool hadSendKRange = false;//设置为波动最大值
                //判断当前时间是否 >= BeginTime ,并且 不能超过 结算时间
                if (now.Minute >= mData.BeginTime+mData.OhlcCount && (now.Minute<mData.BeginTime+mData.OhlcCount+1))
                {
                    DateTime startTime = DateTime.UtcNow;
                    if (!mFirstInDataConnection)
                    {
                        mFirstInDataConnection = true;
                        var candles = await mMaker.exchangeAPI.GetCandlesAsync(mData.SymbolA, 60);
                        mMarketCandles = candles.Take(mData.OhlcCount+1).TakeLast(mData.OhlcCount).ToList();
                        Logger.Debug("=============================获取 蜡烛图================================== startTime："+ startTime);
                        mMarketCandles.PrintList();
                        //获取数据
                        decimal KRangeAvg = mMarketCandles.Average((c) => { return c.HighPrice - c.LowPrice; });//波动最大值 均值 
                        decimal KOCAvg = mMarketCandles.Average((c) => { return Math.Abs(c.OpenPrice - c.ClosePrice); });//K线平均值 Kmean
                        decimal KCAvg = mMarketCandles.Average((c) => { return Math.Abs(c.ClosePrice); });//K线收盘价格平均值 Kavg
                        Logger.Debug($"before KRangeAvg:{KRangeAvg} KOCAvg:{KOCAvg} KCAvg:{KCAvg}");
                        KRangeAvg = Math.Max(KRangeAvg, mData.MinKRangeAvg);
                        KOCAvg = Math.Max(KOCAvg, mData.MinKOCAvg);
                        Logger.Debug($"after KRangeAvg:{KRangeAvg} KOCAvg:{KOCAvg} KCAvg:{KCAvg}");
                        if (KRangeAvg>mData.MaxRange)//如果波动剧烈那么本次不进行套利
                        {
                            Logger.Debug("波动太剧烈放弃本次套利");
                            goto EndAllStatu;
                        }

                    //选择套利方向
                    ChoiceTradeDirectionStatu:
                        Logger.Debug("==============================开始选套利方向==================================");
                        if (mMaker.mOrderBookA != null && mMaker.mOrderBookA.Asks.Count>0 && mMaker.mOrderBookA.Bids.Count>0)//有盘口
                        {
                            var bid1 = mMaker.mOrderBookA.Bids.First().Value;
                            var ask1 = mMaker.mOrderBookA.Asks.First().Value;

                            //检查盘口价格
                            decimal curPrice = (bid1.Price + ask1.Price) / 2;
                            Logger.Debug("curPrice:"+ curPrice+" bid1:" + bid1+"  ask1:"+ ask1 + " ask1.Price-bid1.Price:"+ (ask1.Price - bid1.Price));
                            bool isDoBuy;//买入，或者卖出套利
                            if (curPrice<KCAvg && (ask1.Price-bid1.Price)< KOCAvg)//如果当前价格位于1分钟的10日均线之下，且卖1价-买1价< Kmean 卖出套利
                            {
                                Logger.Debug("==============================卖出套利==================================");
                                while (true)
                                {
                                    isDoBuy = false;
                                    bid1 = mMaker.mOrderBookA.Bids.First().Value;
                                    ask1 = mMaker.mOrderBookA.Asks.First().Value;
                                    //获取仓位V 计算买入 bid1数量之后的总持仓均价CMP
                                    decimal curPosition = Math.Abs(mMaker.mCurAAmount);//多仓 正数，空仓负数
                                    decimal curAllPrice = mMaker.mCurAllPrice;
                                    decimal newAvgPrice = 0;
                                    newAvgPrice = (curAllPrice + bid1.Price * bid1.Amount) / (curPosition + bid1.Amount);
                                    decimal newAskPrice = newAvgPrice + 0.5m * KOCAvg;//我们可以承受的 亏损价格(通过控制指数 达到不亏钱)
                                    decimal newAskAmount = GetCanBuyAmount(newAskPrice);//算出 保证平仓不亏 能买入的最大仓位
                                    Logger.Debug("=======newAskPrice "+ newAskPrice+ "  newAskAmount:"+ newAskAmount + " bid1:" + bid1 + "  ask1:" + ask1);
                                    if (newAskAmount >= (bid1.Amount + Math.Abs(curPosition)))//如果平仓不亏 的数量 大于我们总开仓数量 那么可以继续开仓
                                    {
                                        decimal changeAmount = bid1.Amount;
                                        //用bid1 价格作为 作为空单的限价 吃对应价格的订单
                                        if ((bid1.Amount + Math.Abs(curPosition) > mData.MaxPostion))
                                        {
                                            changeAmount = mData.MaxPostion - Math.Abs(curPosition);
                                        }
                                        await PlaceOrder( false,changeAmount,OrderType.Limit, bid1.Price);
                                        await mMaker.CancelAllOrders();
                                        await Task.Delay(mData.IntervalMillisecond_2);
                                        curPosition = Math.Abs(mMaker.mCurAAmount);//获取新仓位
                                        if (curPosition>= mData.MaxPostion )//超过最大仓位 跳出
                                        {
                                            Logger.Debug("达到最大仓位 " + mData.MaxPostion + " curPosition+：" + curPosition);
                                            break;
                                        }
                                    }
                                    now = DateTime.UtcNow;
                                    if (now.Minute > mData.OpenPositionTime)//超过开仓时间没有开仓跳出 到 平仓阶段
                                    {
                                        Logger.Debug("==============================买入套利 超时跳出==================================");
                                        break;
                                    }
                                    await Task.Delay(mData.IntervalMillisecond_1);
                                }
                            }
                            else if (curPrice > KCAvg && (ask1.Price - bid1.Price) < KOCAvg)//如果当前价格位于1分钟的10日均线之上，且卖1价 - 买1价 < Kmean 买入套利
                            {
                                Logger.Debug("==============================买入套利==================================");
                                while (true)
                                {
                                    isDoBuy = true;
                                    bid1 = mMaker.mOrderBookA.Bids.First().Value;
                                    ask1 = mMaker.mOrderBookA.Asks.First().Value;
                                    //获取仓位V 计算买入 bid1数量之后的总持仓均价CMP
                                    decimal curPosition = Math.Abs(mMaker.mCurAAmount);//躲仓 正数，空仓负数
                                    decimal curAllPrice = mMaker.mCurAllPrice;
                                    decimal newAvgPrice = 0;
                                    newAvgPrice = (curAllPrice + ask1.Price * ask1.Amount) / (curPosition + ask1.Amount);
                                    decimal newBidPrice = newAvgPrice - 0.5m * KOCAvg;//我们可以承受的 亏损价格(通过控制指数 达到不亏钱)
                                    decimal newBIdAmount = GetCanSellAmount(newBidPrice);//算出 保证平仓不亏 能买入的最大仓位
                                    Logger.Debug("=======newBidPrice " + newBidPrice + "  newBIdAmount:" + newBIdAmount + " ask1.Price:"+ ask1.Price);
                                    if (newBIdAmount >= (ask1.Amount + Math.Abs(curPosition)))//如果平仓不亏 的数量 大于我们总开仓数量 那么可以继续开仓
                                    {
                                        decimal changeAmount = ask1.Amount;
                                        //用ask1 价格作为 作为多单的限价 吃对应价格的订单
                                        if ((ask1.Amount + Math.Abs(curPosition) > mData.MaxPostion))
                                        {
                                            changeAmount = mData.MaxPostion - Math.Abs(curPosition);
                                        }
                                        await PlaceOrder(true, changeAmount, OrderType.Limit, ask1.Price);
                                        await mMaker.CancelAllOrders();
                                        await Task.Delay(mData.IntervalMillisecond_2);
                                        curPosition = Math.Abs(mMaker.mCurAAmount);//获取新仓位
                                        if (curPosition >= mData.MaxPostion)//超过最大仓位 跳出
                                        {
                                            Logger.Debug("达到最大仓位 " + mData.MaxPostion + " curPosition+：" + curPosition);
                                            break;
                                        }
                                    }
                                    now = DateTime.UtcNow;
                                    if (now.Minute > mData.OpenPositionTime)//超过开仓时间没有开仓跳出 到 平仓阶段
                                    {
                                        Logger.Debug("==============================买入套利 超时跳出==================================");
                                        break;
                                    }
                                    await Task.Delay(mData.IntervalMillisecond_1);
                                }
                            }
                            else 
                            {
                                now = DateTime.UtcNow;
                                if (now.Minute>mData.OpenPositionTime)//超过开仓时间没有开仓跳出结算
                                {
                                    goto EndAllStatu;
                                }
                                else//在时间内 等待x 秒重新选择套利方向
                                {
                                    await Task.Delay(mData.IntervalMillisecond_1);
                                    goto ChoiceTradeDirectionStatu;
                                }
                            }
                            //如果 没有仓位 跳出本次 套利
                            if (mMaker.mCurAAmount==0)
                                goto EndAllStatu;
                            else//进入平仓阶段
                            {
                                Logger.Debug("==============================平仓阶段开始==================================");
                                decimal openPrice;
                                while (true)
                                {
                                    now = DateTime.UtcNow;
                                    //TODO 设置为
                                    int t_dur =(int)(now.AddMinutes(-now.Minute + mData.EndEatTime-3).AddSeconds(-now.Second) - now).TotalSeconds+5;
                                    //int t_dur = (int)(now.AddMinutes(-now.Minute + mData.EndEatTime).AddSeconds(-now.Second) - now).TotalSeconds;
                                    decimal t_adj = 0;
                                    decimal p_adj = 0;
                                    decimal isAdd = isDoBuy ? 1 : -1;//买入套利 需要涨价，反之降价
                                    

                                    DateTime close1 = now.AddMinutes(-now.Minute + mData.ClosePositionTime).AddSeconds(-now.Second + mData.ClosePositionTimeSeconds_1);
                                    Logger.Debug("平仓阶段: DateTime.UtcNow：" + now, "   isDoBuy" + isDoBuy + "   t_dur" + t_dur + "   close1" +close1);
                                    if (now.Hour != startTime.Hour) //小时不相等说明到下个小时了 ，
                                    {
                                        Logger.Debug("==============================超过平仓时间 调到结算 1 ==================================");
                                        break;//退出 调到结算阶段
                                    }
                                    else if ((now - startTime).TotalMinutes>=2)//TODO 测试用设置跳出时间 当前时间-起始时间  套利总持续时间2分钟
                                    {
                                        Logger.Debug("==============================超过平仓时间 调到结算 2 ==================================");
                                        break;//退出 调到结算阶段
                                    }
                                    else if (now <= close1)// 59:20 <=
                                    {
                                        if (isFirstInClose1)
                                        {
                                            isFirstInClose1 = false;
                                            p_adj = mData.ChangeKparam_1 * KOCAvg * isAdd;
                                            await ChangeParams(t_dur, t_adj, p_adj, mData.xau_btc_weight[0], mData.xau_btc_weight[1], mData.xau_btc_weight[2]);
                                        }
                                        Logger.Debug("==============================平仓阶段1 ==================================");
                                    }
                                    else if (now.Minute == mData.ClosePositionTime && now.Second <= mData.ClosePositionTimeSeconds_2)//59:21-59:40
                                    {
                                        if (isFirstInClose2)
                                        {
                                            isFirstInClose2 = false;
                                            p_adj = mData.ChangeKparam_2 * KOCAvg * isAdd;
                                            await ChangeParams(t_dur, t_adj, p_adj, mData.xau_btc_weight[0], mData.xau_btc_weight[1], mData.xau_btc_weight[2]);
                                        }
                                        Logger.Debug("==============================平仓阶段2 ==================================");
                                    }
                                    else if (now.Minute == mData.ClosePositionTime && now.Second <= mData.ClosePositionTimeSeconds_3)//59:41-60:00
                                    {
                                        Logger.Debug("==============================平仓阶段3 ==================================");
                                        if (!hadSendKRange)//没有调整到 KRang 波动最大平均值;
                                        {
                                            if (mMaker.mOrderBookA.Asks.Count > 0 && mMaker.mOrderBookA.Bids.Count > 0)//有盘口
                                            {
                                                bid1 = mMaker.mOrderBookA.Bids.First().Value;
                                                ask1 = mMaker.mOrderBookA.Asks.First().Value;
                                                if (isDoBuy)//买入套利
                                                {
                                                    decimal eatPrice = bid1.Price + (mData.ChangeKparam_3-mData.ChangeKparam_2) * KOCAvg;//假设改变价格后 ask1降价到的值
                                                    openPrice = mMaker.mCurAllPrice / Math.Abs(mMaker.mCurAAmount);
                                                    if (openPrice < eatPrice)//仓位盈利
                                                    {

                                                    }
                                                    else
                                                    {
                                                        hadSendKRange = true;
                                                        p_adj = KRangeAvg * isAdd;
                                                        await ChangeParams(t_dur, t_adj, p_adj, mData.xau_btc_weight[0], mData.xau_btc_weight[1], mData.xau_btc_weight[2]);
                                                    }
                                                }
                                                else//卖出套利
                                                {
                                                    decimal eatPrice = ask1.Price -(mData.ChangeKparam_3 - mData.ChangeKparam_2) * KOCAvg;//假设改变价格后 ask1降价到的值
                                                    openPrice = mMaker.mCurAllPrice / Math.Abs(mMaker.mCurAAmount);
                                                    if (openPrice > eatPrice)//仓位盈利
                                                    {

                                                    }
                                                    else
                                                    {
                                                        hadSendKRange = true;
                                                        p_adj = KRangeAvg * isAdd;
                                                        await ChangeParams(t_dur, t_adj, p_adj, mData.xau_btc_weight[0], mData.xau_btc_weight[1], mData.xau_btc_weight[2]);
                                                    }
                                                }
                                            }
                                        }
                                        if (isFirstInClose3 && hadSendKRange == false)//第一次进入并且 没有调整过指数值
                                        {
                                            p_adj = mData.ChangeKparam_3 * KOCAvg * isAdd;
                                            isFirstInClose3 = false;
                                            await ChangeParams(t_dur, t_adj, p_adj, mData.xau_btc_weight[0], mData.xau_btc_weight[1], mData.xau_btc_weight[2]);
                                        }
                                    }
                                    else //超出平仓时间 调到结算
                                    {
                                        Logger.Debug("==============================超过平仓时间 调到结算 3 ==================================");
                                        break;
                                    }

                                    //检查是否满足平仓条件，满足条件限价平仓
                                    bid1 = mMaker.mOrderBookA.Bids.First().Value;
                                    ask1 = mMaker.mOrderBookA.Asks.First().Value;
                                    openPrice = mMaker.mCurAllPrice / Math.Abs(mMaker.mCurAAmount);
                                    decimal ProfitPrice = Math.Min(mData.ProfitPrice, 0.5m*KOCAvg);
                                    Logger.Debug("======= "  + "  bid1:" + bid1 + " ask1:" + ask1);
                                    if (isDoBuy)//买入套利
                                    {
                                        if (bid1.Price >= openPrice + ProfitPrice)//满足盈利条件
                                        {
                                            decimal closeAmount = Math.Min(bid1.Amount, Math.Abs(mMaker.mCurAAmount));
                                            await PlaceOrder(false, closeAmount, OrderType.Limit, bid1.Price);//扫单bid1
                                            await mMaker.CancelAllOrders();//取消未成交订单
                                        }
                                    }
                                    else//卖出套利
                                    {
                                        if (ask1.Price <= openPrice - ProfitPrice)//满足盈利条件
                                        {
                                            decimal closeAmount = Math.Min(ask1.Amount, Math.Abs(mMaker.mCurAAmount));
                                            await PlaceOrder(true, closeAmount, OrderType.Limit, ask1.Price);//扫单ask1
                                            await mMaker.CancelAllOrders();//取消未成交订单
                                        }
                                    }
                                    await Task.Delay(mData.IntervalMillisecond_3);

                                    if (isDoBuy)
                                    {
                                        if (mMaker.mCurAAmount <= 0)//买入套利 平仓完毕
                                            break;
                                    }
                                    else
                                    {
                                        if (mMaker.mCurAAmount >= 0)//卖出入套利 平仓完毕
                                            break;
                                    }
                                }
                            }
                        }
                    }
                //结算 套利
                EndAllStatu:
                    {
                        Logger.Debug("==============================结算阶段==================================");
                        await CloseAllPositionAsync();
                        CleanDatas();
                        return;
                    }

                }


                //照抄 b交易所的盘口
                // await mExchangeAAPI.CancelOrderAsync("all", mData.SymbolA);
                //mRunningTask =  DrawKLine(copyBookBBids, copyBookBAsks);

                //初始化 

                /*
                var item = mMaker;

                    if (!item.OnConnect())
                        return;
                    await item.CheckMaxOrdersCountAsync();

                    var request = RandomOrder(item);
                    if (request == null)
                        return;
                    //限价单，会以市价成交的订单
                    var buyPrice = mIndexPrice;
                    var sellPrice = mIndexPrice;
                    if (item.mOrderBookA != null && item.mOrderBookA.Bids != null && item.mOrderBookA.Bids.Count > 0)
                    {
                        buyPrice = item.mOrderBookA.Bids.FirstOrDefault().Key;
                    }
                    if (item.mOrderBookA != null && item.mOrderBookA.Asks != null && item.mOrderBookA.Asks.Count > 0)
                    {
                        sellPrice = item.mOrderBookA.Asks.FirstOrDefault().Key;
                    }
                    ExchangeOrderResult Result = null;
                    try
                    {
                        Logger.Debug("item.limitMakerConfig.Id:" + item.limitMakerConfig.Id + "   PlanceOrdersAsync requestA:::" + request.ToString());
                        Result = await AddOrder(request, item);
                        if (Result != null)
                        {
                            Logger.Debug("item.limitMakerConfig.Id:" + item.limitMakerConfig.Id + "   PlanceOrdersAsync Result:::" + Result.ToExcleString());
                        }
                        Task.Delay(50);
                    }
                    catch (System.Exception ex)
                    {
                        Logger.Error(Utils.Str2Json("mRunningTask ex", ex));
                        if (ex.ToString().Contains("Invalid orderID") || ex.ToString().Contains("Not Found") || ex.ToString().Contains("Order already closed") || item.exchangeAPI.ErrorPlanceOrderPrice(ex))
                            Result = null;
                        mRunningTask = null;
                    }
                    if (Result != null)
                    {
                        Logger.Debug("ResultResult:  " + Result.ToExcleString());
                        item.mOpenOrderDic.Add(Result.OrderId, Result);
                    }
             */
            }
        }

        private async Task PlaceOrder(bool isBuy,decimal amount ,OrderType orderType, decimal price=0)
        {
            Logger.Debug("PlaceOrder");
            var request = new ExchangeOrderRequest();
            request.MarketSymbol = mData.SymbolA;
            if (orderType != OrderType.Market)
            {
                request.Price = price;
            }
            request.Amount = amount;
            request.IsBuy = isBuy;
            request.OrderType = orderType;
            request.ExtraParameters.Add("positionEffect", 1);
            ExchangeOrderResult Result = null;
            try
            {
                Logger.Debug("item.limitMakerConfig.Id:" + mMaker.limitMakerConfig.Id + "   PlanceOrdersAsync requestA:::" + request.ToString());
                Result = await AddOrder(request, mMaker);
                if (Result != null)
                {
                    Logger.Debug("item.limitMakerConfig.Id:" + mMaker.limitMakerConfig.Id + "   PlanceOrdersAsync Result:::" + Result.ToExcleString());
                }
                Task.Delay(50);
            }
            catch (System.Exception ex)
            {
                Logger.Error(Utils.Str2Json("mRunningTask ex", ex));
                if (ex.ToString().Contains("Invalid orderID") || ex.ToString().Contains("Not Found") || ex.ToString().Contains("Order already closed") || mMaker.exchangeAPI.ErrorPlanceOrderPrice(ex))
                    Result = null;
                mRunningTask = null;
            }
            if (Result != null)
            {
                Logger.Debug("ResultResult:  " + Result.ToExcleString());
                mMaker.mOpenOrderDic.Add(Result.OrderId, Result);
            }
        }

        /// <summary>
        /// 根据价格计算可以 限价 买到 的对手盘 数量
        /// </summary>
        /// <param name="price"></param>
        /// <returns></returns>
        private decimal GetCanBuyAmount(decimal price)
        {

            var Asks = mMaker.mOrderBookA.Asks;
            decimal buyAmount = 0;
            for (int i = 0; i < Asks.Count ; i++)
            {
                var ask = Asks.ElementAt(i).Value;
                if (ask.Price < price)
                {
                    buyAmount += ask.Amount;
                }
                else
                    break;
            }
            return buyAmount;
        }
        private decimal GetCanSellAmount(decimal price)
        {
            var Bids = mMaker.mOrderBookA.Bids;
            decimal buyAmount = 0;
            for (int i = 0; i < Bids.Count; i++)
            {
                var bid = Bids.ElementAt(i).Value;
                if (bid.Price > price)
                {
                    buyAmount += bid.Amount;
                }
                else
                    break;
            }
            return buyAmount;
        }

        private async Task CloseAllPositionAsync()
        {
            await Task.Delay(500);
            if (mMaker.mCurAAmount!=0)
            {
                bool isBuy = mMaker.mCurAAmount < 0;
                await PlaceOrder(isBuy, Math.Abs(mMaker.mCurAAmount), OrderType.Market);
            }
        }

        /// <summary>
        /// 刷新差价
        /// </summary>
        public async Task UpdateAvgDiffAsync()
        {
            /*
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
                    string remark = jsonResult["remark"].ToString().ToLower();
                    mData.OpenProcess = !remark.Contains("close");


                    if (rangeList.Count%2==0)
                    {
                        for (int i = 0; i < rangeList.Count / 2; i++)
                        {
                            int mid = rangeList.Count / 2;
                            Diff diff;
                            if (mData.DiffGrid.Count>i)
                            {
                                diff = mData.DiffGrid[i];
                            }
                            else
                            {
                                diff = new Diff()
                                {
                                    Rate=0.3m
                                };
                                mData.DiffGrid.Add(diff);
                            }
                            diff.A2BDiff = rangeList[mid - i - 1].ConvertInvariant<decimal>();
                            diff.B2ADiff = rangeList[mid + i].ConvertInvariant<decimal>();
                            mData.SaveToDB(mDBKey);
                        }
                         CountDiffGridMaxCount();
                    }
                    
                    Logger.Debug("SetDiffBuyMA:" + avgDiff);

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
            */
        }

        /// <summary>
        /// 添加订单
        /// </summary>
        /// <param name="requestA"></param>
        /// <returns> 返回已经添加的订单详情 </returns>
        private async Task<ExchangeOrderResult> AddOrder(ExchangeOrderRequest requestA, LimitMaker maker)
        {
            ExchangeOrderResult curResult = null;
            requestA.Price = NormalizationMinUnit(requestA.Price);
            bool isAddNew = true;
            try
            {
                var v = await maker.exchangeAPI.PlaceOrderAsync(requestA);
                curResult = v;
                //测试多等待10再添加订单
                //await Task.Delay(10000);
                lock (maker.mOrderIds)
                {
                    maker.mOrderIds.Add(curResult.OrderId);
                }

                //检查如果已经成交的订单中有本订单号,那么启用成交逻辑
                lock (maker.mOrderResultsDic)
                {
                    if (maker.mOrderResultsDic.TryGetValue(curResult.OrderId, out ExchangeOrderResult filledOrder))
                    {
                        maker.OnOrderAHandler(filledOrder);
                        Logger.Debug("出现先ws返回 后rest返回");
                    }
                }
                Logger.Debug(Utils.Str2Json("requestA", requestA.ToString()));
                Logger.Debug(Utils.Str2Json("Add mCurrentLimitOrder", curResult.ToExcleString(), "CurAmount", maker.mCurAAmount));
                if (curResult.Result == ExchangeAPIOrderResult.Canceled)
                {
                    curResult = null;
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
                    curResult = null;
                    mOnTrade = false;
                }
                if (ex.ToString().Contains("405 Method Not Allowed"))//删除订单
                {
                    curResult = null;
                    mOnTrade = false;
                }
                Logger.Error(Utils.Str2Json("ex", ex));
                if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("Not logged in"))
                    await Task.Delay(5000);
                if (ex.ToString().Contains("RateLimitError"))
                    await Task.Delay(30000);
            }
            return curResult;
        }


        public double GetRandomNumber(double minimum, double maximum, int Len)   //Len小数点保留位数
        {
            Random random = new Random();
            return Math.Round(random.NextDouble() * (maximum - minimum) + minimum, Len);
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
            public LimitMakerConfig limitMaker = new LimitMakerConfig();
            /*
            /// <summary>
            /// 开始计算时间
            /// </summary>
            public int BeginTime = 48;
            /// <summary>
            /// 开仓时间 58:01-58:59
            /// </summary>
            public int OpenPositionTime = 58;
            /// <summary>
            /// 开仓时间 59:01-59:59
            /// </summary>
            public int ClosePositionTime = 59;
            /// <summary>
            /// 开仓时间 59:01-59:20
            /// </summary>
            public int ClosePositionTimeSeconds_1 = 20;
            /// <summary>
            /// 开仓时间 59:21-59:40
            /// </summary>
            public int ClosePositionTimeSeconds_2 = 40;
            /// <summary>
            /// 开仓时间 59:21-59:59
            /// </summary>
            public int ClosePositionTimeSeconds_3 = 59;
            /// <summary>
            /// 调整指数 比例
            /// </summary>
            public decimal ChangeKparam_1 = 0.3m;
            /// <summary>
            /// 调整指数 比例
            /// </summary>
            public decimal ChangeKparam_2 = 0.6m;
            /// <summary>
            /// 调整指数 比例
            /// </summary>
            public decimal ChangeKparam_3 = 1m;
            /// <summary>
            /// 修改参数停止时间,结算开始的小时 添加 这个分钟 就是完成时间
            /// </summary>
            public int EndEatTime = 62;
            /// <summary>
            /// 需要累积的开高低收数量
            /// </summary>
            public int OhlcCount = 10;
            /// <summary>
            /// 最小 高开低收 值。小于该值本次不进行套利
            /// </summary>
            public int MinOhloCount = 8;
            */
            /// <summary>
            /// 开始计算时间
            /// </summary>
            public int BeginTime = 35;
            /// <summary>
            /// 修改参数停止时间,结算开始的小时 添加 这个分钟 就是完成时间
            /// </summary>
            public int EndEatTime = 26;
            /// <summary>
            /// 需要累积的开高低收数量
            /// </summary>
            public int OhlcCount = 3;
            /// <summary>
            /// 最小 高开低收 值。小于该值本次不进行套利
            /// </summary>
            public int MinOhloCount = 2;
            /// <summary>
            /// 开仓时间 58:01-58:59
            /// </summary>
            public int OpenPositionTime = 58;
            /// <summary>
            /// 开仓时间 59:01-59:59
            /// </summary>
            public int ClosePositionTime = 59;
            /// <summary>
            /// 开仓时间 59:01-59:20
            /// </summary>
            public int ClosePositionTimeSeconds_1 = 20;
            /// <summary>
            /// 开仓时间 59:21-59:40
            /// </summary>
            public int ClosePositionTimeSeconds_2 = 40;
            /// <summary>
            /// 开仓时间 59:21-59:59
            /// </summary>
            public int ClosePositionTimeSeconds_3 = 59;
            /// <summary>
            /// 调整指数 比例
            /// </summary>
            public decimal ChangeKparam_1 = 0.5m;
            /// <summary>
            /// 调整指数 比例
            /// </summary>
            public decimal ChangeKparam_2 = 0.8m;
            /// <summary>
            /// 调整指数 比例
            /// </summary>
            public decimal ChangeKparam_3 = 1m;

            /// <summary>
            /// 统计盘口档位
            /// </summary>
            public int LevelCount = 5;
            /// <summary>
            /// 盈利差价
            /// </summary>
            public decimal ProfitPrice = 1m;
            /// <summary>
            /// 最小 Kmean - 3跳
            /// 最大操作值 10块
            /// </summary>
            public decimal MinKOCAvg = 0.6m;
            /// <summary>
            /// 最小KRange = 5跳
            /// </summary>
            public decimal MinKRangeAvg = 1m;
            /// <summary>
            /// 最大波动值， 如果平均波动值超过了最大波动值 那么本次不进行套利
            /// </summary>
            public decimal MaxRange = 3m;

            public List<float> xau_btc_weight = new List<float>() { 0, 1, 1 };
            /// <summary>
            /// 最小价格单位
            /// </summary>
            public decimal MinPriceUnit = 0.5m;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurAAmount = 0;
            /// <summary>
            /// 间隔时间1
            /// </summary>
            public int IntervalMillisecond_1 = 500;
            /// <summary>
            /// 间隔时间2
            /// </summary>
            public int IntervalMillisecond_2 = 1500;
            /// <summary>
            /// 间隔时间3
            /// </summary>
            public int IntervalMillisecond_3 = 500;
            /// <summary>
            /// 最大下单数量
            /// </summary>
            public int MaxPostion = 300;
            /// <summary>
            /// 杠杆倍率
            /// </summary>
            public decimal Leverage = 3;
            /// <summary>
            /// redis连接数据
            /// </summary>
            public string RedisConfig = "localhost,password=l3h2p1w0*";
            /// <summary>
            /// redis连接数据
            /// </summary>
            public string RedisConfigData_ZBG = "13.212.29.195:6379,password=orangepi123";//"localhost,password=l3h2p1w0*";

            public string RestIp = "http://www.birativepro.com";//"http://172.16.0.111:8000";
            public string Private = "c45772d45e8a49752c6478c51805c4e6";
            public string Public = "8b45f5fb46c8d1db84bd57be3ede3498";

            public string RedisChannle = "maker_index_chanel";
            /// <summary>
            /// 是否开启交易
            /// </summary>
            public bool OpenProcess = true;



            public void SaveToDB(string DBKey)
            {
                RedisDB.Instance.StringSet(DBKey, this);
            }
            public static T LoadFromDB<T>(string DBKey)
            {
                return RedisDB.Instance.StringGet<T>(DBKey);
            }
        }


        //{"timestamp": 1596524742784, "index": 997.56, "btc_price": 11259.9, "btc_price_5min": 11238.9, "gold_price": 1974.6, "gold_price_5min": 1973.53}
        private class BtcGlodIndex
        {
            public long timestamp;

            public decimal index;

            public decimal btc_price;

            public decimal btc_price_5min;

            public decimal gold_price;

            public decimal gold_price_5min;

            public DateTime sendDate => timestamp.ToDateTime();
        }
        /// <summary>
        /// 随机配置参数
        /// </summary>
        public class RandomConfig
        {
            public decimal Max;
            public decimal Min;
            public decimal PerTime;

            public decimal GetRandm()
            {
                return Convert.ToDecimal(GetRandomNumber(Convert.ToDouble(Min / PerTime), Convert.ToDouble(Max / PerTime), 0)) * PerTime;
            }

            private double GetRandomNumber(double minimum, double maximum, int Len)   //Len小数点保留位数
            {
                Random random = new Random();
                return Math.Round(random.NextDouble() * (maximum - minimum) + minimum, Len);
            }
        }

        private class QueryAndChangeMarketReturn
        {
            public int status;
            public String message;
            /// <summary>
            /// 1 表示正在做市，0表示关闭做市
            /// </summary>
            public int makerStatus;
        }

        
    }
}