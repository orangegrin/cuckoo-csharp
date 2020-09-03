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
        private DateTime mlastFillTime;
        //第一次提交接近标记价格订单
        private bool mIsFirst = true;


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
                isBuy = random.Next(0, 2) == 1;//非满仓:随机开多还是开空=>

            //3随机 叠加价格是加还是减
            //4 随机 挂单 叠加价格
            //price = mIndexPrice + (random.Next(0, 2) == 0 ? -1 : 1) * (random.Next(1, mData.RandomPriceMax) * mData.MinPriceUnit);
            int idx =  Utils.GetRandomList(mData.RandomPriceWeight, 1)[0]+1;
            //随机增幅
            idx = idx * random.Next(0, mData.MaxRandomDiffTimes);
            price = mIndexPrice + +(random.Next(0, 2) == 0 ? -1 : 1) * idx * mData.MinPriceUnit;
            if (mIsFirst)
            {
                price = mIndexPrice;
                mIsFirst = false;
            }

            if (idx ==0 )
            {
                throw new Exception("sadfafa");
            }

            //随机数量
            amount = limitMaker.limitMakerConfig.MaxPosition * Convert.ToDecimal(GetRandomNumber(Convert.ToDouble(limitMaker.limitMakerConfig.MinChangeAmountRate), Convert.ToDouble(limitMaker.limitMakerConfig.MaxChangeAmountRate), limitMaker.limitMakerConfig.PerRate));
            amount = Math.Max(Math.Ceiling( (3*amount/4 / mData.RandomPriceMax) * Math.Abs((price - mIndexPrice) / mData.MinPriceUnit)+ amount/4),1);


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
                Logger.Debug("！！！！！！！！！！！！！！！！marketmarketmarketmarketmarketmarket！！！！！！！！！！！！！！！！！！！！！！！！！！！！");
                //                 var task = limitMaker.GetOrderBook();
                //                 Task.WaitAll(task);
                //                 var orderBook = task.Result;
                
                var orderBook = limitMaker.mOrderBookA;
                if (orderBook == null)
                    return null;
                var orderPrice = isBuy ? orderBook.Asks : orderBook.Bids;
                decimal allAmount = orderPrice.Take(5).Select((lvAmount) => { return lvAmount.Value.Amount; }).Sum();
                amount = Math.Max(1, Math.Ceiling(allAmount * Convert.ToDecimal(GetRandomNumber(Convert.ToDouble(limitMaker.limitMakerConfig.MinEat), Convert.ToDouble(limitMaker.limitMakerConfig.MaxEat), limitMaker.limitMakerConfig.PerEatRate))));
                orderType = OrderType.Market;
                price = 0;
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
            mlastFillTime = DateTime.UtcNow.AddDays(-1);
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
                maker.CancelAllOrders();
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
                await Task.Delay(mData.IntervalMillisecond);
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
                Execute();
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

                Logger.Debug("________________________DOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO");
                //照抄 b交易所的盘口
                // await mExchangeAAPI.CancelOrderAsync("all", mData.SymbolA);
                //mRunningTask =  DrawKLine(copyBookBBids, copyBookBAsks);

                //初始化 
                mIndexPrice = 465.5m;
                foreach (var item in limitMakers)
                {
                    if (!item.OnConnect())
                        continue;
                    await item.CheckMaxOrdersCountAsync();

                    var request = RandomOrder(item);
                    if (request == null)
                        continue;
                    //限价单，会以市价成交的订单
                    var buyPrice = mIndexPrice;
                    var sellPrice = mIndexPrice;
                    if (item.mOrderBookA!=null && item.mOrderBookA.Bids!=null && item.mOrderBookA.Bids.Count>0)
                    {
                        buyPrice = item.mOrderBookA.Bids.FirstOrDefault().Key;
                    }
                    if (item.mOrderBookA != null && item.mOrderBookA.Asks != null && item.mOrderBookA.Asks.Count >0)
                    {
                        sellPrice = item.mOrderBookA.Asks.FirstOrDefault().Key;
                    }
                    if (request.OrderType == OrderType.Limit && (request.IsBuy ? request.Price >= sellPrice : request.Price <= buyPrice))
                    {
                        //如果超过最大成交间隔时间强制成交
                        bool canFiil = (DateTime.UtcNow - mlastFillTime).TotalSeconds > mData.MaxFillTimeSpan;
                        if (!canFiil)
                        {
                            continue;
                        }
                        Random r = new Random();
                        int rNum = r.Next(0, 1000);
                        if (rNum < Convert.ToInt32(item.limitMakerConfig.RandomCancelRate * 1000))//一定概率撤单
                        {
                            continue;
                        }
                        mlastFillTime = DateTime.UtcNow;

                    }
                    ExchangeOrderResult Result = null;
                    try
                    {
                        Logger.Debug("item.limitMakerConfig.Id:" + item.limitMakerConfig.Id+ "   PlanceOrdersAsync requestA:::" + request.ToString());
                        Result = await AddOrder(request, item);
                        if (Result!=null)
                        {
                            Logger.Debug("item.limitMakerConfig.Id:" + item.limitMakerConfig.Id + "   PlanceOrdersAsync Result:::" + Result.ToExcleString());
                        }
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
            /// 价格最大随机范围
            /// </summary>
            public int RandomPriceMax = 30;
            /// <summary>
            /// 随机价格加权
            /// 有多少个就把上面最大范围平分成多少份来做加权 最大个数小于RandomPriceMax
            /// </summary>
            public List<int> RandomPriceWeight;

            public List<LimitMakerConfig> LimitMakers = new List<LimitMakerConfig>();
            /// <summary>
            /// 最小价格单位
            /// </summary>
            public decimal MinPriceUnit = 0.5m;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurAAmount = 0;
            /// <summary>
            /// 间隔时间
            /// </summary>
            public int IntervalMillisecond = 500;
            /// <summary>
            /// 杠杆倍率
            /// </summary>
            public decimal Leverage = 3;
            /// <summary>
            /// 本位币
            /// </summary>
            public string AmountSymbol = "BTC";
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
            /// 最大成交间隔
            /// 如果超过时间强制成交 单位s
            /// </summary>
            public int MaxFillTimeSpan = 60;
            /// <summary>
            /// 随机增大跳动幅度
            /// </summary>
            public int MaxRandomDiffTimes = 10;
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
    }
}