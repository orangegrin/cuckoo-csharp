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

namespace cuckoo_csharp.Strategy.Arbitrage
{
    /// <summary>
    /// 测试画k线
    /// 
    /// 1获取某交易所对应币种 orderbook数据  x
    /// 2根据 通过一定策略添加 copy  x 的orderbook 提交订单
    /// 
    /// </summary>
    public class RandomMarketMaker
    {

//         private int mOrderBookAwsCounter = 0;
//         private int mOrderBookBwsCounter = 0;
//         private int mOrderDetailsAwsCounter = 0;
        private int mId;
        private Options mData { get; set; }


        private List<IExchangeAPI> mExchangeAPIs = new List<IExchangeAPI>();

        private List<LimitMaker> limitMakers = new List<LimitMaker>();
        
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

      
        /// <summary>
        /// 
        /*
1 判断有无仓位
有仓位 => 2
无仓位,随机开多还是开空=>3
2判断是否满仓
满仓 :只平仓=>3
非满仓:随机开多还是开空=>3
3随机 叠加价格是加还是减
4 随机 挂单 叠加价格
5随机 挂单数量
6等待时间
7循环 */
        /// </summary>
        /// <returns></returns>
        private ExchangeOrderRequest RandomOrder( LimitMaker limitMaker)
        {
            Random random = new Random();

            bool isOpen = true;
            bool isBuy;
            decimal amount;
            decimal price;

            OrderType orderType = OrderType.Limit;
            if (limitMaker.mCurAAmount != 0)// 1 判断有无仓位
            {
                if (Math.Abs(limitMaker.mCurAAmount) >= limitMaker.limitMakerConfig.MaxPosition)//2判断是否满仓
                {
                    isBuy = limitMaker.mCurAAmount < 0;//满仓 :只平仓=>3
                }
                else
                {
                    isBuy = random.Next(0, 2) == 1;//非满仓:随机开多还是开空=>3
                }
            }
            else
                isBuy = random.Next(0, 2) == 1;//非满仓:随机开多还是开空=>3

            //3随机 叠加价格是加还是减
            //4 随机 挂单 叠加价格
            price = mIndexPrice + 10* (random.Next(0, 2) == 0 ? -1 : 1) * (random.Next(1, mData.RandomPriceMax) * mData.MinPriceUnit);
            //随机数量
            amount = limitMaker.limitMakerConfig.MaxPosition * Convert.ToDecimal(GetRandomNumber(Convert.ToDouble(limitMaker.limitMakerConfig.MinChangeAmountRate), Convert.ToDouble(limitMaker.limitMakerConfig.MaxChangeAmountRate), limitMaker.limitMakerConfig.PerRate));

            if ((isBuy &&  limitMaker.mCurAAmount<0) || //做多 当前空仓   表示要平仓
                (!isBuy && limitMaker.mCurAAmount>0) //做空 当前多仓      表示要平仓
                )
            {
                if (amount > Math.Abs(limitMaker.mCurAAmount) )//避免 平仓数量大于 持仓数量
                    amount = Math.Abs(limitMaker.mCurAAmount);
                isOpen = false;
            }

            if (!limitMaker.limitMakerConfig.IsLimit)
            {
                var task = limitMaker.GetOrderBook();
                Task.WaitAll(task);
                var orderBook = task.Result;
                var orderPrice = isBuy ? orderBook.Asks : orderBook.Bids;
                decimal allAmount = orderPrice.Select((lvAmount) => { return lvAmount.Value.Amount; }).Sum();
                amount = allAmount * Convert.ToDecimal(GetRandomNumber(Convert.ToDouble(limitMaker.limitMakerConfig.MinEat), Convert.ToDouble(limitMaker.limitMakerConfig.MaxEat), limitMaker.limitMakerConfig.PerEatRate));
                orderType = OrderType.Market;
                price = 0;
                amount = 10;//TODO 
            }

            var request = new ExchangeOrderRequest();
            request.MarketSymbol = mData.SymbolA;
            request.Price = price;
            request.Amount = amount;
            request.IsBuy = isBuy;
            request.OrderType = orderType;
            //request.ExtraParameters.Add("positionEffect", isOpen? 1:2);
            request.ExtraParameters.Add("positionEffect", 1);


            return request;
        }




        public RandomMarketMaker(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("RandomMarketMaker:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
            RedisDB.Init(config.RedisConfig);
            mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            //添加做市
            foreach (var item in mData.LimitMakers)
            {
                LimitMaker maker = new LimitMaker(item,mData.ExchangeNameA,mData.SymbolA);
                limitMakers.Add(maker);
            }



        }
        public void Start()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
            //             UpdateAvgDiffAsync();
                        

            foreach (var maker in limitMakers)
            {
                maker.Init();

            }
            //             CheckPosition();
            //             ChangeMaxCount();
            // CheckOrderBook();
            Update();



            /*redis subscriber
            ConnectionMultiplexer connection;
            string configStr = mData.RedisConfigData_ZBG;
            connection = ConnectionMultiplexer.Connect(configStr);
            Console.WriteLine("主线程：" + Thread.CurrentThread.ManagedThreadId);
            var sub = connection.GetSubscriber();
            sub.SubscribeAsync(mData.RedisChannle, (RedisChannel cnl, RedisValue val) =>
            {
                Console.WriteLine();
                Console.WriteLine("频道：" + cnl + "\t收到消息:" + val); ;
                Console.WriteLine("线程：" + Thread.CurrentThread.ManagedThreadId + ",是否线程池：" + Thread.CurrentThread.IsThreadPoolThread);
                BtcGlodIndex btcGlodIndex = JsonConvert.DeserializeObject<BtcGlodIndex>(val.ToString());
                Logger.Debug("Date:" + btcGlodIndex.sendDate);
                if (val == "close")
                    connection.GetSubscriber().Unsubscribe(cnl);
                if (val == "closeall")
                    connection.GetSubscriber().UnsubscribeAll();
            });
            Console.WriteLine("订阅了一个频道：" + mData.RedisChannle);
            */

        }
        #region Connect
 

     
        
#endregion
        private void OnProcessExit(object sender, EventArgs e)
        {
            Logger.Debug("------------------------ OnProcessExit ---------------------------");
            mExchangePending = true;

            foreach (var item in limitMakers)
            {
                item.CancelAllOrders();
            }
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
                foreach (var item in limitMakers)
                {
                    if (!item.OnConnect())
                    {
                        await Task.Delay(5 * 1000);
                        continue;
                    }
                    await item.CountDiffGridMaxCount();
                }

                await Task.Delay(2*3600 * 1000);
            }
        }
        /// <summary>
        ///刷新
        /// </summary>
        private async void Update()
        {
            while (true)
            {
                DoTrade();
                await Task.Delay(200);
            }
        }



        private ExchangeOrderBook CLone(ExchangeOrderBook order)
        {
            string str = JsonConvert.SerializeObject(order);
            return JsonConvert.DeserializeObject<ExchangeOrderBook>(str);
        }

        async void DoTrade()
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
                    temp.CurAAmount = mData.CurAAmount;
                    //mData = temp;
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
        private async Task Execute()
        {
            List<ExchangeOrderPrice> copyBookBBids;
            List<ExchangeOrderPrice> copyBookBAsks;
            if (Precondition())
                return;

            //有可能orderbook bids或者 asks没有改变
            //if (buyPriceA != 0 && sellPriceA != 0 && sellPriceB != 0 && buyPriceB != 0 && buyAmount != 0)
            if (true )
            {
               // PrintInfo(buyPriceA, sellPriceA, 0, 0, a2bDiff, b2aDiff, diff.A2BDiff, diff.B2ADiff, buyAmount, bidAAmount, askAAmount, 0, 0);

                //如果盘口差价超过4usdt 不进行挂单，但是可以改单（bitmex overload 推送ws不及时）
                //                 if (mCurOrderA == null && ((sellPriceA <= buyPriceA) || (sellPriceA - buyPriceA >= 10) || (sellPriceB <= buyPriceB) || (sellPriceB - buyPriceB >= 10)))
                //                 {
                //                     Logger.Debug("范围更新不及时，不纳入计算");
                //                     return;
                //                 }
                //return;


                //照抄 b交易所的盘口
                // await mExchangeAAPI.CancelOrderAsync("all", mData.SymbolA);
                //mRunningTask =  DrawKLine(copyBookBBids, copyBookBAsks);

                //初始化 
                mIndexPrice = 390;
                foreach (var item in limitMakers)
                {
                    if (!item.OnConnect())
                        continue;
                    await item.CheckMaxOrdersCountAsync();

                    var request = RandomOrder(item);
                    ExchangeOrderResult Result = null;
                    try
                    {
                        Logger.Debug("PlanceOrdersAsync requestA:::" + request.ToString());
                        Result = await AddOrder(request, item);
                        Logger.Debug("PlanceOrdersAsync Result:::" + Result.ToExcleString());
                        Task.Delay(50);
                    }
                    catch (System.Exception ex)
                    {
                        Logger.Error(Utils.Str2Json("mRunningTask ex", ex));
                        if (ex.ToString().Contains("Invalid orderID") || ex.ToString().Contains("Not Found") || ex.ToString().Contains("Order already closed")|| item.exchangeAPI.ErrorPlanceOrderPrice(ex))
                            Result = null;
                        mRunningTask = null;
                    }
                    if (Result!=null)
                    {
                        Logger.Debug("ResultResult:  " + Result.ToExcleString());

                        item.mOpenOrderDic.Add(Result.OrderId, Result);
                    }
                }
            }
        }
        /*
        private async Task CheckOrderBook()
        {
            ExchangeOrderPrice? lastA =null ;
            ExchangeOrderPrice?  lastB =null;
            ExchangeOrderPrice? curA;
            ExchangeOrderPrice? curB;
            while (true)
            {
                await Task.Delay(30 * 1000);
                if (mOrderBookA==null || mOrderBookB==null || !OnConnect() )
                    continue;
                else
                {
                    if (lastA==null)
                    {
                        lock (mOrderBookA)
                        {
                            lastA = mOrderBookA.Asks.FirstOrDefault().Value;
                        }
                        await Task.Delay(15 * 1000);
                    }
                    if (lastB == null)
                    {
                        lock (mOrderBookB)
                        {
                            lastB = mOrderBookB.Asks.FirstOrDefault().Value;
                        }
                        await Task.Delay(15 * 1000);
                    }

                    lock (mOrderBookA)
                    {
                        curA = mOrderBookA.Asks.FirstOrDefault().Value;
                    }
                    lock (mOrderBookB)
                    {
                        curB = mOrderBookB.Asks.FirstOrDefault().Value;
                    }
                    ConnectState? state = null;
                    if ((lastA.Value.Price == curA.Value.Price && lastA.Value.Amount == curA.Value.Amount))
                    {
                        state = ConnectState.A;
                       
                    }
                    if ((lastB.Value.Price == curB.Value.Price && lastB.Value.Amount == curB.Value.Amount))
                    {
                        if (state==null)
                        {
                            state = ConnectState.B;
                        }
                        else
                        {
                            state |= ConnectState.B;
                        }
                    }
                    if (state!=null)
                    {
                        Logger.Error("CheckOrderBook  orderbook 错误");
                        await ReconnectWS(state.Value);
                    }
                    lastA = curA;
                    lastB = curB;
                }
               
            }
        }
        */
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

        private void PrintInfo(decimal bidA, decimal askA, decimal bidB, decimal askB, decimal a2bDiff, decimal b2aDiff, decimal A2BDiff, decimal B2ADiff, decimal buyAmount, 
            decimal bidAAmount, decimal askAAmount, decimal bidBAmount, decimal askBAmount )
        {
            Logger.Debug("================================================");
            Logger.Debug(Utils.Str2Json("BA价差当前百分比↑", a2bDiff.ToString(), "BA价差百分比↑", A2BDiff.ToString() )) ;
            Logger.Debug(Utils.Str2Json("BA价差当前百分比↓" , b2aDiff.ToString(), "BA价差百分比↓" , B2ADiff.ToString()));
            Logger.Debug(Utils.Str2Json("Bid A", bidA, " Bid B", bidB, "bidAAmount", bidAAmount, "bidBAmount", bidBAmount));
            Logger.Debug(Utils.Str2Json("Ask A", askA, " Ask B", askB, "askAAmount", askAAmount, "askBAmount", askBAmount));
            //Logger.Debug(Utils.Str2Json("mCurAmount", mCurAAmount, " buyAmount",  buyAmount ));


            //var csvList = new List<List<string>>();
            //List<string> strList = new List<string>()
            //{
            //    (bidA-bidB).ToString(),
            //};
            //csvList.Add(strList);
            //Utils.AppendCSV(csvList, Path.Combine(Directory.GetCurrentDirectory(), "Data_" + mId + ".csv"), false);

            //if (mDiffHistory != null)
            //{
            //    lock (mDiffHistory)
            //    {
            //        //mDiffHistory.Add(a2bDiff);
            //        mDiffHistory.Add(-1);
            //        if (mDiffHistory.Count > (mData.PerTime + 100))
            //            mDiffHistory.RemoveRange(0, mDiffHistory.Count - mData.PerTime);
            //    }
            //}
        }
        /// <summary>
        /// 添加订单
        /// </summary>
        /// <param name="requestA"></param>
        /// <returns> 返回已经添加的订单详情 </returns>
        private async Task<ExchangeOrderResult> AddOrder(ExchangeOrderRequest requestA,LimitMaker maker)
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
                lock(maker.mOrderIds)
                {
                    maker.mOrderIds.Add(curResult.OrderId);
                }
                
                //检查如果已经成交的订单中有本订单号,那么启用成交逻辑
                lock(maker.mOrderResultsDic)
                {
                    if (maker.mOrderResultsDic.TryGetValue(curResult.OrderId, out ExchangeOrderResult filledOrder))
                    {
                        maker.OnOrderAHandler(filledOrder);
                        Logger.Debug("出现先ws返回 后rest返回");
                    }
                }
                Logger.Debug(Utils.Str2Json("requestA", requestA.ToString()));
                Logger.Debug(Utils.Str2Json("Add mCurrentLimitOrder", curResult.ToExcleString(), "CurAmount", mData.CurAAmount));
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
            /// <summary>
            /// 照抄orderbook 缩放比例
            /// </summary>
            public decimal CopyScale = 0.1m;
            /// <summary>
            /// 随机比例
            /// </summary>
            public decimal CopyRandom = 0.1m;
            /// <summary>
            /// 照抄深度
            /// </summary>
            public int CopyDepth = 5;
            /// <summary>
            /// 价格最大随机范围
            /// </summary>
            public int RandomPriceMax = 50;

            public List<LimitMakerConfig> LimitMakers = new List<LimitMakerConfig>();

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
            /// redis连接数据
            /// </summary>
            public string RedisConfigData_ZBG = "13.212.29.195:6379,password=orangepi123";//"localhost,password=l3h2p1w0*";

            public string RedisChannle = "bigold_index";
            /// <summary>
            /// 计算ma的时间段，单位s
            /// </summary>
            public int PerTime = 3600;
            public string DiffHistoryPath = "/home/ubuntu/p2fETHcoin/XBTUSD_BTC_CW.csv";
            /// <summary>
            /// 止损或者止盈的比例
            /// </summary>
            public decimal StopOrProftiRate = 0.5m;
            /// <summary>
            /// 是否开启交易
            /// </summary>
            public bool OpenProcess = true;
            /// <summary>
            /// redis连接数据
            /// </summary>
            public DateTime CloseDate = DateTime.Now.AddMinutes(1);
            /// <summary>
            /// 子账号标识
            /// </summary>
            public string SubAccountA = "";

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

            public decimal Rate = 1m;


            public Diff Clone()
            {
                return (Diff)this.MemberwiseClone();
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

        public class LimitMaker
        {
            public LimitMakerConfig limitMakerConfig;

            public IExchangeAPI exchangeAPI;

            public string SymbolA;

            /// <summary>
            /// 交易中的订单
            /// </summary>
            public Dictionary<string, ExchangeOrderResult> mOpenOrderDic = new Dictionary<string, ExchangeOrderResult>();

            public IWebSocket mOrderws;

            public int mOrderBookAwsCounter = 0;
            public int mOrderBookBwsCounter = 0;
            public int mOrderDetailsAwsCounter = 0;
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



                SubWebSocket(ConnectState.All, this);
                WebSocketProtect(this);


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
            }

            #region connect

            private void SubWebSocket(ConnectState state = ConnectState.All, LimitMaker limitMaker = null)
            {
                limitMaker = this;
                mOnConnecting = true;

                if (state.HasFlag(ConnectState.Orders))
                {
                    limitMaker.mOrderws = limitMaker.exchangeAPI.GetOrderDetailsWebSocket(limitMaker.OnOrderAHandler);
                    limitMaker.mOrderws.Connected += async (socket) => { limitMaker.mOrderwsConnect = true; Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"GetOrderDetailsWebSocket 连接"); OnConnect(); };
                    limitMaker.mOrderws.Disconnected += async (socket) =>
                    {
                        limitMaker.mOrderwsConnect = false;
                        WSDisConnectAsync("GetOrderDetailsWebSocket 连接断开");
                    };
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
                    if (limitMaker.mOrderBookAwsCounter < 0 || limitMaker.mOrderBookBwsCounter <0  || (!detailConnect))
                    {
                        Logger.Error(new Exception("ws 没有收到推送消息"));
                        await ReconnectWS();
                    }
                }
            }
            private async Task ReconnectWS(ConnectState state = ConnectState.All)
            {
                await CancelAllOrders();

                await CloseWS(state);
                Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"开始重新连接ws");
                SubWebSocket(state);
                await Task.Delay(5 * 1000);
            }

            private async Task CloseWS(ConnectState state = ConnectState.All)
            {
                await Task.Delay(5 * 1000);
                Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"销毁ws");
                
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
                bool connect = mOrderwsConnect;//& mOrderBookAwsConnect & mOrderBookBwsConnect;
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
                        SubWebSocket(ConnectState.All,this);
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
                return await exchangeAPI.GetOrderBookAsync(SymbolA,limitMakerConfig.EatLevel);
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
                    Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"pong");
                    return;
                }


                if (order.MarketSymbol != SymbolA)
                    return;
                if (order.Message.Equals("Position"))
                {
                    mCurAAmount = order.Amount;
                }

                if (!IsMyOrder(order.OrderId))
                {
                    AddFilledOrderResult(order);
                    return;
                }

                switch (order.Result)
                {
                    case ExchangeAPIOrderResult.Unknown:
                        Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"-------------------- Order Unknown ---------------------------");
                        Logger.Debug(order.ToExcleString());
                        break;
                    case ExchangeAPIOrderResult.Filled:
                        OnOrderFilled(order);
                        break;
                    case ExchangeAPIOrderResult.FilledPartially:
                        OnFilledPartiallyAsync(order);
                        break;
                    case ExchangeAPIOrderResult.Pending:
                        Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"-------------------- Order Pending ---------------------------");
                        Logger.Debug(order.ToExcleString());
                        break;
                    case ExchangeAPIOrderResult.Error:
                        Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"-------------------- Order Error ---------------------------");
                        Logger.Debug(order.ToExcleString());
                        break;
                    case ExchangeAPIOrderResult.Canceled:
                        OnOrderCanceled(order);
                        break;
                    case ExchangeAPIOrderResult.FilledPartiallyAndCancelled:
                        Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"-------------------- Order FilledPartiallyAndCancelled ---------------------------");
                        Logger.Debug(order.ToExcleString());
                        break;
                    case ExchangeAPIOrderResult.PendingCancel:
                        Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"-------------------- Order PendingCancel ---------------------------");
                        Logger.Debug(order.ToExcleString());
                        break;
                    default:
                        Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"-------------------- Order Default ---------------------------");
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
                        removeList = mOpenOrderDic.Take(count).ToList();
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
                    lock(mOpenOrderDic)
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
                Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"-------------------- Order Filed Partially---------------------------");
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
                    Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"--------------PrintFilledOrder--------------");
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
                Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"-------------------- Order Canceled ---------------------------");
                Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"Canceled  " + order.ToExcleString() + "CurAmount" + limitMakerConfig.CurAAmount);

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
                Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"mId{0} {1}", limitMakerConfig.Id, mCurAAmount);
                Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"----------------------------222ChangePositionByFiledOrder---------------------------");
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
                Logger.Debug("mId:"+limitMakerConfig.Id+"   "+"-------------------- GetParTrans ---------------------------");
                lock (mFilledPartiallyDic)//防止多线程并发
                {
                    decimal filledAmount = 0;
                    mFilledPartiallyDic.TryGetValue(order.OrderId, out filledAmount);
                    Logger.Debug("mId:"+limitMakerConfig.Id+"   "+" filledAmount: " + filledAmount.ToStringInvariant());
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

            public decimal MinChangeAmountRate = 0.05m;
            public decimal MaxChangeAmountRate = 0.1m;
            /// <summary>
            /// 最小一跳,小数点后多少位
            /// </summary>
            public int PerRate = 2;
            /// <summary>
            /// 最大挂单数量
            /// </summary>
            public int MaxOrderCount = 14;
            /// <summary>
            /// 当挂单满了每次取消的数量
            /// </summary>
            public int CancelCount = 6;
            /// <summary>
            /// 是否为市价单
            /// </summary>
            public bool IsLimit = true;

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
            /// 加密文件路径
            /// </summary>
            public string EncryptedFile;

            public decimal MaxPosition => MaxLeverage * CurMoney;
        }

    }
}