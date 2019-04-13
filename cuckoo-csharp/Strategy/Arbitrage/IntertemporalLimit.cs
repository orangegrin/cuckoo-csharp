
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp;
using System.Threading;
using cuckoo_csharp.Tools;

namespace cuckoo_csharp.Strategy.Arbitrage
{
    public class IntertemporalLimit
    {
        private IExchangeAPI mExchangeAAPI;
        private IExchangeAPI mExchangeBAPI;
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
        private ExchangeOrderResult mCurrentLimitOrder;
        /// <summary>
        /// A交易所的历史订单ID
        /// </summary>
        private List<string> mOrderIds = new List<string>();
        /// <summary>
        /// 完成了一次开仓，到完全平仓的订单记录A交易所
        /// </summary>
        private List<ExchangeOrderResult> mOpenAndCloseOrderA = new List<ExchangeOrderResult>();
        /// <summary>
        /// 完成了一次开仓，到完全平仓的订单记录B交易所
        /// </summary>
        private List<ExchangeOrderResult> mOpenAndCloseOrderB = new List<ExchangeOrderResult>();
        private Options mData { get; set; }
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
        private bool mOrderBookPending = false;
        private List<decimal> mDiffList = new List<decimal>();

        public IntertemporalLimit(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("INTERTEMPORAL:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
            if (mData == null)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mData.ExchangeNameA);
            mExchangeBAPI = ExchangeAPI.GetExchangeAPI(mData.ExchangeNameB);

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
        /// <summary>
        /// 计算范围
        /// </summary>
        /// <param name="avgDiff"></param>
        private void AppendAvgDiff(decimal avgDiff)
        {
            mDiffList.Add(avgDiff);
            //获取一天的数据
            int maxCount = 86400000 / mData.IntervalMillisecond;
            if (mDiffList.Count > maxCount)
                mDiffList.RemoveRange(0, maxCount / 5);
            //优化性能，每一千次计算一次
            if (mData.AutoCalcProfitRange && mDiffList.Count % 1000 == 0)
            {
                CalcProfitRange(mData, maxCount);
            }
        }

        private void CalcProfitRange(Options config, int maxCount)
        {
            lock (mDiffList)
            {
                var avg = mDiffList.Average();
                var range = config.ProfitRange / 2;
                config.A2BDiff = avg + range;
                config.B2ADiff = avg - range;
                if (mDiffList.Count >= maxCount / 2)
                    config.SaveToDB(mDBKey);
                Console.WriteLine("A2BDF {0} B2ADF {1}", config.A2BDiff, config.B2ADiff);
            }
        }

        public void Start()
        {
            mExchangeAAPI.LoadAPIKeys(mData.EncryptedFileA);
            mExchangeBAPI.LoadAPIKeys(mData.EncryptedFileB);
            mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
            //避免没有订阅成功就开始订单
            Thread.Sleep(4 * 1000);
            mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 20, mData.SymbolA);
            mExchangeBAPI.GetFullOrderBookWebSocket(OnOrderbookBHandler, 20, mData.SymbolB);
        }

        private void OnOrderbookAHandler(ExchangeOrderBook order)
        {
            mOrderBookA = order;
            OnOrderBookHandler();
        }

        private void OnOrderbookBHandler(ExchangeOrderBook order)
        {
            mOrderBookB = order;
            OnOrderBookHandler();
        }

        async void OnOrderBookHandler()
        {
            if (mOrderBookA == null || mOrderBookB == null)
                return;
            if (mRunningTask != null)
                return;
            if (mOrderBookPending)
                return;
            if (mOrderBookA.Asks.Count == 0 || mOrderBookA.Bids.Count == 0 || mOrderBookB.Bids.Count == 0 || mOrderBookB.Asks.Count == 0)
                return;
            mOrderBookPending = true;
            mData.LoadFromDB(mDBKey);
            await Execute();
            await Task.Delay(mData.IntervalMillisecond);
            mOrderBookPending = false;

        }

        private async Task Execute()
        {
            decimal buyPriceA;
            decimal sellPriceA;
            decimal sellPriceB;
            decimal buyPriceB;
            decimal a2bDiff = 0;
            decimal b2aDiff = 0;
            decimal buyAmount = 0;
            lock (mOrderBookA)
            {
                buyPriceA = mOrderBookA.Asks.ElementAt(0).Value.Price;
                sellPriceA = mOrderBookA.Bids.ElementAt(0).Value.Price;
            }
            lock (mOrderBookB)
            {
                sellPriceB = mOrderBookB.GetPriceToSell(mData.PerTrans);
                mOrderBookB.GetPriceToBuy(mData.PerTrans * buyPriceA, out buyAmount, out buyPriceB);
            }

            //有可能orderbook bids或者 asks没有改变
            if (buyPriceA != 0 && sellPriceA != 0 && sellPriceB != 0 && buyPriceB != 0 && buyAmount != 0)
            {
                a2bDiff = (sellPriceB / buyPriceA - 1);
                b2aDiff = (buyPriceB / sellPriceA - 1);
                var avgDiff = (a2bDiff + b2aDiff) / 2;
                AppendAvgDiff(avgDiff);
                Logger.Debug("================================================");
                Logger.Debug("BA价差百分比1：" + a2bDiff.ToString());
                Logger.Debug("BA价差百分比2：" + b2aDiff.ToString());
                Logger.Debug("{0} {1} {2} CurAmount:{3}", buyPriceA, sellPriceB, buyAmount, mData.CurAmount);
                Logger.Debug("{0} {1} {2} CurAmount:{3}", buyPriceB, sellPriceA, buyAmount, mData.CurAmount);
                //满足差价并且
                //只能BBuyASell来开仓，也就是说 ABuyBSell只能用来平仓
                if (a2bDiff > mData.A2BDiff && mData.CurAmount + mData.PerTrans <= mData.InitialExchangeBAmount) //满足差价并且当前A空仓
                {
                    mRunningTask = A2BExchange(sellPriceA);
                }
                else if (b2aDiff < mData.B2ADiff && -mCurAmount < mData.MaxAmount) //满足差价并且没达到最大数量
                {
                    Logger.Debug("mId:" + mId + "================================================");
                    Logger.Debug("mId:" + mId + "{0} {1}", buyPriceB, sellPriceA);
                    //如果只是修改订单
                    if (mCurrentLimitOrder != null && !mCurrentLimitOrder.IsBuy)
                    {
                        mRunningTask = B2AExchange(buyPriceA);
                    }
                    //表示是新创建订单
                    else if (await SufficientBalance())
                    {
                        mRunningTask = B2AExchange(buyPriceA);
                    }
                    //保证金不够的时候取消挂单
                    else if (mCurrentLimitOrder != null)
                    {
                        Logger.Debug("mId:" + mId + "保证金不够的时候取消挂单：" + b2aDiff.ToString());
                        ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                        cancleRequestA.ExtraParameters.Add("orderID", mCurrentLimitOrder.OrderId);
                        mRunningTask = mExchangeAAPI.CancelOrderAsync(mCurrentLimitOrder.OrderId, mData.SymbolA);
                    }
                }
                else if (mCurrentLimitOrder != null && mData.B2ADiff <= a2bDiff && a2bDiff <= mData.A2BDiff)//如果在波动区间中，那么取消挂单
                {
                    Logger.Debug("mId:" + mId + "在波动区间中取消订单：" + b2aDiff.ToString());
                    ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                    cancleRequestA.ExtraParameters.Add("orderID", mCurrentLimitOrder.OrderId);
                    mRunningTask = mExchangeAAPI.CancelOrderAsync(mCurrentLimitOrder.OrderId, mData.SymbolA);
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
                        Logger.Error("mId:" + mId + ex);
                    }
                }
            }
        }

        /// <summary>
        /// 当curAmount 小于 0的时候就是平仓
        /// A买B卖
        /// </summary>
        async Task A2BExchange(decimal buyPrice)
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
            //避免市价成交
            //buyPrice -= mConfig.MinPriceUnit;

            requestA.Price = NormalizationMinUnit(buyPrice);
            //加上手续费btc卖出数量，买不考虑
            //mCurrentBChangeCoinAmount = exchangeAmount*1.0011m;
            //如果当前有限价单，并且方向不相同，那么取消
            //如果方向相同那么改价，

            //             decimal oldCount = CurAmount;
            //             decimal lastNum = 0;
            bool isAddNew = true;
            try
            {
                if (mCurrentLimitOrder != null)
                {
                    //方向相同，并且达到修改条件
                    if (mCurrentLimitOrder.IsBuy == requestA.IsBuy)
                    {
                        isAddNew = false;
                        //lastNum = mCurrentLimitOrder.Amount;
                        requestA.ExtraParameters.Add("orderID", mCurrentLimitOrder.OrderId);
                        //检查是否有改动必要
                        //做多涨价则判断
                        if (requestA.Price == mCurrentLimitOrder.Price)
                        {
                            return;
                        }
                    }
                    else
                    {//如果方向相反那么直接取消
                        ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                        cancleRequestA.ExtraParameters.Add("orderID", mCurrentLimitOrder.OrderId);
                        //在onOrderCancle的时候处理
                        await mExchangeAAPI.CancelOrderAsync(mCurrentLimitOrder.OrderId, mData.SymbolA);
                    }
                };
                //市价不设置价格
                if (requestA.OrderType == OrderType.Market)
                {
                    requestA.Price = 0;
                    requestA.ExtraParameters.Remove("execInst");
                }
                var v = await mExchangeAAPI.PlaceOrdersAsync(requestA);
                mCurrentLimitOrder = v[0];
                mOrderIds.Add(mCurrentLimitOrder.OrderId);
                Logger.Debug("mId:" + mId + "requestA：  " + requestA.ToString());
                Logger.Debug("mId:" + mId + "Add mCurrentLimitOrder：  " + mCurrentLimitOrder.ToExcleString() + "CurAmount:" + mData.CurAmount);
            }
            catch (Exception ex)
            {
                Logger.Debug("mId:" + mId + "数据回滚 ABuyBSell：  CurAmount" + mData.CurAmount);
                //如果是添加新单那么设置为null
                if (isAddNew)
                    mCurrentLimitOrder = null;
                Logger.Error("mId:" + mId + ex);
            }
        }
        /// <summary>
        /// 当curAmount大于0的时候就是开仓
        /// B买A卖
        /// </summary>
        /// <param name="exchangeAmount"></param>
        async Task B2AExchange(decimal sellPrice)
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
                if (mCurrentLimitOrder != null)
                {
                    if (mCurrentLimitOrder.IsBuy == requestA.IsBuy)
                    {
                        newOrder = false;
                        //lastNum = -mCurrentLimitOrder.Amount;
                        requestA.ExtraParameters.Add("orderID", mCurrentLimitOrder.OrderId);
                        //检查是否有改动必要
                        //做空涨价则判断
                        if (requestA.Price == mCurrentLimitOrder.Price)
                        {
                            return;
                        }
                    }
                    else
                    {   //如果方向相反那么直接取消
                        ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                        cancleRequestA.ExtraParameters.Add("orderID", mCurrentLimitOrder.OrderId);
                        //在onOrderCancle的时候处理
                        await mExchangeAAPI.CancelOrderAsync(mCurrentLimitOrder.OrderId, mData.SymbolA);
                    }
                };
                //市价不设置价格
                if (requestA.OrderType == OrderType.Market)
                {
                    requestA.Price = 0;
                    requestA.ExtraParameters.Remove("execInst");
                }
                var v = await mExchangeAAPI.PlaceOrdersAsync(requestA);
                mCurrentLimitOrder = v[0];
                mOrderIds.Add(mCurrentLimitOrder.OrderId);
                Logger.Debug("mId:" + mId + "requestA：  " + requestA.ToString());
                Logger.Debug("mId:" + mId + "Add mCurrentLimitOrder：  " + mCurrentLimitOrder.ToExcleString());
            }
            catch (Exception ex)
            {
                Logger.Debug("mId:" + mId + "数据回滚  BBuyASell CurAmount:" + mData.CurAmount);
                //如果是添加新单那么设置为null
                if (newOrder)
                    mCurrentLimitOrder = null;
                Logger.Error("mId:" + mId + ex);
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
            mOrderBookB.GetPriceToBuy(mData.PerTrans, out exchangeAmount, out buyPrice);
            //避免挂新单之前，上一笔B的市价没有成交完
            var spend = mData.PerTrans * buyPrice * 3m;
            if (bAmount < spend)
            {
                Logger.Debug("Insufficient exchange balance {0} ,need spend {1}", bAmount, spend);
                return false;
            }
            else
            {
                Logger.Debug("current balance {0} ,need spend {1}", bAmount, spend);
            }
            return true;
        }

        /// <summary>
        /// 订单成交 ，修改当前仓位和删除当前订单
        /// </summary>
        /// <param name="order"></param>
        void OnOrderFilled(ExchangeOrderResult order)
        {
            Logger.Debug("mId:" + mId + "  " + "-------------------- Order Filed ---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            lock (mCurrentLimitOrder)
            {
                // 如果 当前挂单和订单相同那么删除
                if (mCurrentLimitOrder != null && mCurrentLimitOrder.OrderId == order.OrderId)
                {
                    //重置数量
                    mCurrentLimitOrder = null;
                }
                ReverseOpenMarketOrder(order);//, completed, openedBuyOrderList, openedSellOrderList);
            }
        }
        /// <summary>
        /// 订单部分成交
        /// </summary>
        /// <param name="order"></param>
        void OnFilledPartially(ExchangeOrderResult order)
        {
            if (order.Amount == order.AmountFilled)
                return;
            Logger.Debug("mId:" + mId + "  " + "-------------------- Order Filed Partially---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            ReverseOpenMarketOrder(order);
        }



        /// <summary>
        /// 订单取消，删除当前订单
        /// </summary>
        /// <param name="order"></param>
        void OnOrderCanceled(ExchangeOrderResult order)
        {
            Logger.Debug("mId:" + mId + "  " + "-------------------- Order Canceled ---------------------------");

            //重置数量
            //CurAmount += order.IsBuy ? -order.Amount : +order.Amount;
            Logger.Debug("mId:" + mId + "Canceled  " + order.ToExcleString() + "CurAmount" + mData.CurAmount);
            if (mCurrentLimitOrder != null && mCurrentLimitOrder.OrderId == order.OrderId)
            {
                mCurrentLimitOrder = null;
            }
        }
        /// <summary>
        /// 当A交易所的订单发生改变时候触发
        /// </summary>
        /// <param name="order"></param>
        void OnOrderAHandler(ExchangeOrderResult order)
        {
            if (order.MarketSymbol != mData.SymbolA)
                return;
            if (!IsMyOrder(order.OrderId))
                return;
            switch (order.Result)
            {
                case ExchangeAPIOrderResult.Unknown:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Filled:
                    OnOrderFilled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartially:
                    OnFilledPartially(order);
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Pending:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Error:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Canceled:
                    OnOrderCanceled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartiallyAndCancelled:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.PendingCancel:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                default:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
            }
        }

        public Dictionary<string, decimal> mFilledPartiallyDic = new Dictionary<string, decimal>();

        /// <summary>
        /// 计算反向开仓时应当开仓的数量（如果部分成交）
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        public decimal GetParTrans(ExchangeOrderResult order)
        {
            decimal filledAmount = 0;
            mFilledPartiallyDic.TryGetValue(order.OrderId, out filledAmount);
            if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount == 0)
            {
                mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                return order.AmountFilled;
            }
            if (order.Result == ExchangeAPIOrderResult.FilledPartially && filledAmount != 0)
            {
                mFilledPartiallyDic[order.OrderId] = order.AmountFilled;
                return order.AmountFilled - filledAmount;
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
        async void ReverseOpenMarketOrder(ExchangeOrderResult order)//, bool completeOnce = false, List<ExchangeOrderResult> openedBuyOrderListA = null, List<ExchangeOrderResult> openedSellOrderListA = null)
        {
            var transAmount = GetParTrans(order);
            //只有在成交后才修改订单数量
            mCurAmount += order.IsBuy ? +transAmount : -transAmount;
            Logger.Debug("mId:" + mId + "CurAmount:::" + mData.CurAmount);
            var req = new ExchangeOrderRequest();
            req.Amount = transAmount;
            req.IsBuy = !order.IsBuy;
            req.IsMargin = true;
            req.OrderType = OrderType.Market;
            req.MarketSymbol = mData.SymbolB;
            Logger.Debug("mId:" + mId + "  " + "----------------------------ReverseOpenMarketOrder---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            var ticks = DateTime.Now.Ticks;
            try
            {
                var res = await mExchangeBAPI.PlaceOrderAsync(req);
                mOpenAndCloseOrderB.Add(res);
                Logger.Debug("mId:" + mId + "--------------------------------ReverseOpenMarketOrder Result-------------------------------------");
                Logger.Debug((DateTime.Now.Ticks - ticks).ToString());
                Logger.Debug(res.ToString());
                Logger.Debug(res.OrderId);
            }
            catch (Exception ex)
            {
                Logger.Error(req.ToString());
                Logger.Error("mId:" + mId + ex);
                throw ex;
            }
        }
        /// <summary>
        /// 计算赚的比例
        /// </summary>
        /// <param name="openedBuyOrderListA"></param>
        /// <param name="openedSellOrderListA"></param>
        /// <param name="closeedBuyOrderListB"></param>
        /// <param name="closeedSellOrderListB"></param>
        public static void CountRewardRate(decimal count, List<ExchangeOrderResult> openedBuyOrderListA, List<ExchangeOrderResult> openedSellOrderListA, List<ExchangeOrderResult> closeedBuyOrderListB, List<ExchangeOrderResult> closeedSellOrderListB)
        {
            decimal changeCoinCount = 0;
            if (openedBuyOrderListA.Count > 0 && openedSellOrderListA.Count > 0 && closeedBuyOrderListB.Count > 0 && closeedSellOrderListB.Count > 0)
            {
                for (int i = 0; i < openedBuyOrderListA.Count; i++)
                {
                    ExchangeOrderResult buyA = openedBuyOrderListA[i];
                    ExchangeOrderResult buyB = closeedBuyOrderListB[i];
                    ExchangeOrderResult sellA = openedSellOrderListA[i];
                    ExchangeOrderResult sellB = closeedSellOrderListB[i];

                    decimal AChange = 0;
                    decimal BChange = 0;
                    decimal openCoinCount = 0;

                    AChange = count / buyA.Price - count / sellA.Price;
                    BChange = buyB.Amount - sellB.Amount;
                    //用开仓数量计算
                    if (sellB.FillDate < buyB.FillDate)
                    {
                        openCoinCount = sellB.Amount;
                    }
                    else
                    {
                        openCoinCount = buyB.Amount;
                    }



                    changeCoinCount += (AChange + BChange) / openCoinCount;
                }
                decimal rewardRate = changeCoinCount / openedBuyOrderListA.Count;
                Logger.Debug("rewardRate" + rewardRate + "--------------------------------the reward-------------------------------------");
            }

        }

        bool IsMyOrder(string orderId)
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
            /// <summary>
            /// 开仓差
            /// </summary>
            public decimal A2BDiff;
            /// <summary>
            /// 平仓差
            /// </summary>
            public decimal B2ADiff;
            public decimal PerTrans;
            /// <summary>
            /// 最小价格单位
            /// </summary>
            public decimal MinPriceUnit = 0.5m;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurAmount = 0;
            /// <summary>
            /// 间隔时间
            /// </summary>
            public int IntervalMillisecond = 500;
            /// <summary>
            /// 利润范围
            /// 当AutoCalcProfitRange 开启时有效
            /// </summary>
            public decimal ProfitRange = 0.003m;
            /// <summary>
            /// 自动计算利润范围
            /// </summary>
            public bool AutoCalcProfitRange = false;
            /// <summary>
            /// 本位币
            /// </summary>
            public string AmountSymbol = "BTC";
            /// <summary>
            /// 开始交易时候的初始火币数量
            /// </summary>
            public decimal InitialExchangeBAmount = 0m;
            /// <summary>
            /// A交易所手续费
            /// </summary>
            public decimal FeesA;
            /// <summary>
            /// B交易所手续费
            /// </summary>
            public decimal FeesB;
            /// <summary>
            /// 最大数量
            /// </summary>
            public decimal MaxAmount;
            /// <summary>
            /// A交易所加密串路径
            /// </summary>
            public string EncryptedFileA;
            /// <summary>
            /// B交易所加密串路径
            /// </summary>
            public string EncryptedFileB;

            public void SaveToDB(string DBKey)
            {
                RedisDB.Instance.StringSet(DBKey, this);
            }
            public Options LoadFromDB(string DBKey)
            {
                return RedisDB.Instance.StringGet<Options>(DBKey);
            }
        }

    }
}