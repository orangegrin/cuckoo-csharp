
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp;
using System.Threading;
using cuckoo_csharp.Tools;
using Newtonsoft.Json;

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
        private List<decimal> mDiffList = new List<decimal>();
        public IntertemporalLimit(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("INTERTEMPORAL:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
            mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mData.ExchangeNameA);
            mExchangeBAPI = ExchangeAPI.GetExchangeAPI(mData.ExchangeNameB);

        }
        public void Start()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
            mExchangeAAPI.LoadAPIKeys(mData.EncryptedFileA);
            mExchangeBAPI.LoadAPIKeys(mData.EncryptedFileB);
            mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
            //避免没有订阅成功就开始订单
            Thread.Sleep(3 * 1000);
            mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 20, mData.SymbolA);
            mExchangeBAPI.GetFullOrderBookWebSocket(OnOrderbookBHandler, 20, mData.SymbolB);
        }

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
            if (mExchangePending)
                return;
            if (mOrderBookA.Asks.Count == 0 || mOrderBookA.Bids.Count == 0 || mOrderBookB.Bids.Count == 0 || mOrderBookB.Asks.Count == 0)
                return;
            mExchangePending = true;
            mData = Options.LoadFromDB<Options>(mDBKey);
            await Execute();
            await Task.Delay(mData.IntervalMillisecond);
            mExchangePending = false;

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
                PrintInfo(buyPriceA, sellPriceA, sellPriceB, buyPriceB, a2bDiff, b2aDiff, buyAmount);

                //满足差价并且
                //只能BBuyASell来开仓，也就是说 ABuyBSell只能用来平仓
                if (a2bDiff > mData.A2BDiff && mData.CurAmount + mData.PerTrans <= mData.InitialExchangeBAmount) //满足差价并且当前A空仓
                {

                    mRunningTask = A2BExchange(sellPriceA);
                }
                else if (b2aDiff < mData.B2ADiff && -mCurAmount < mData.MaxAmount) //满足差价并且没达到最大数量
                {
                    //如果只是修改订单
                    if (mCurOrderA != null && !mCurOrderA.IsBuy)
                    {
                        mRunningTask = B2AExchange(buyPriceA);
                    }
                    //表示是新创建订单
                    else if (await SufficientBalance())
                    {
                        mRunningTask = B2AExchange(buyPriceA);
                    }
                    //保证金不够的时候取消挂单
                    else if (mCurOrderA != null)
                    {
                        Logger.Debug("mId:" + mId + "保证金不够的时候取消挂单：" + b2aDiff.ToString());
                        ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                        cancleRequestA.ExtraParameters.Add("orderID", mCurOrderA.OrderId);
                        mRunningTask = mExchangeAAPI.CancelOrderAsync(mCurOrderA.OrderId, mData.SymbolA);
                    }
                }
                else if (mCurOrderA != null && mData.B2ADiff <= a2bDiff && a2bDiff <= mData.A2BDiff)//如果在波动区间中，那么取消挂单
                {
                    Logger.Debug("mId:" + mId + "在波动区间中取消订单：" + b2aDiff.ToString());
                    ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                    cancleRequestA.ExtraParameters.Add("orderID", mCurOrderA.OrderId);
                    mRunningTask = mExchangeAAPI.CancelOrderAsync(mCurOrderA.OrderId, mData.SymbolA);
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

        private void PrintInfo(decimal buyPriceA, decimal sellPriceA, decimal sellPriceB, decimal buyPriceB, decimal a2bDiff, decimal b2aDiff, decimal buyAmount)
        {
            Logger.Debug("================================================");
            Logger.Debug("BA价差百分比1：" + a2bDiff.ToString());
            Logger.Debug("BA价差百分比2：" + b2aDiff.ToString());
            Logger.Debug("Bid A {0} Bid B {1}", buyPriceA, sellPriceB);
            Logger.Debug("Ask B {0} Ask A {1}", buyPriceB, sellPriceA);
            Logger.Debug("mCurAmount {0} buyAmount {1} ", mCurAmount, buyAmount);
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
                if (mCurOrderA != null)
                {
                    //方向相同，并且达到修改条件
                    if (mCurOrderA.IsBuy == requestA.IsBuy)
                    {
                        isAddNew = false;
                        //lastNum = mCurrentLimitOrder.Amount;
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
                Logger.Debug("mId:" + mId + "requestA：  " + requestA.ToString());
                Logger.Debug("mId:" + mId + "Add mCurrentLimitOrder：  " + mCurOrderA.ToExcleString() + "CurAmount:" + mData.CurAmount);
            }
            catch (Exception ex)
            {
                //如果是添加新单那么设置为null
                if (isAddNew)
                    mCurOrderA = null;
                Logger.Error("mId:" + mId + ex);
            }
            await Task.Delay(mData.IntervalMillisecond * 10);
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
                        //lastNum = -mCurrentLimitOrder.Amount;
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
                Logger.Debug("mId:" + mId + "requestA：  " + requestA.ToString());
                Logger.Debug("mId:" + mId + "Add mCurrentLimitOrder：  " + mCurOrderA.ToExcleString());
            }
            catch (Exception ex)
            {
                //如果是添加新单那么设置为null
                if (newOrder)
                    mCurOrderA = null;
                Logger.Error("mId:" + mId + ex);
            }
            await Task.Delay(mData.IntervalMillisecond * 10);
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
            var spend = mData.PerTrans * buyPrice * 1.3m;
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
        private void OnOrderFilled(ExchangeOrderResult order)
        {
            Logger.Debug("mId:" + mId + "  " + "-------------------- Order Filed ---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            lock (mCurOrderA)
            {
                // 如果 当前挂单和订单相同那么删除
                if (mCurOrderA != null && mCurOrderA.OrderId == order.OrderId)
                {
                    //重置数量
                    mCurOrderA = null;
                }
                ReverseOpenMarketOrder(order);//, completed, openedBuyOrderList, openedSellOrderList);
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
            Logger.Debug("mId:" + mId + "  " + "-------------------- Order Filed Partially---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            ReverseOpenMarketOrder(order);
        }
        /// <summary>
        /// 订单取消，删除当前订单
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderCanceled(ExchangeOrderResult order)
        {
            Logger.Debug("mId:" + mId + "  " + "-------------------- Order Canceled ---------------------------");
            Logger.Debug("mId:" + mId + "Canceled  " + order.ToExcleString() + "CurAmount" + mData.CurAmount);
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
            if (order.MarketSymbol != mData.SymbolA)
                return;
            if (!IsMyOrder(order.OrderId))
                return;
            switch (order.Result)
            {
                case ExchangeAPIOrderResult.Unknown:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Unknown ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Filled:
                    OnOrderFilled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartially:
                    OnFilledPartially(order);
                    break;
                case ExchangeAPIOrderResult.Pending:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Pending ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Error:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Error ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Canceled:
                    OnOrderCanceled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartiallyAndCancelled:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order FilledPartiallyAndCancelled ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.PendingCancel:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order PendingCancel ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                default:
                    Logger.Debug("mId:" + mId + "  " + "-------------------- Order Default ---------------------------");
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
            Logger.Debug("mId:" + mId + "  " + "-------------------- GetParTrans ---------------------------");
            decimal filledAmount = 0;
            mFilledPartiallyDic.TryGetValue(order.OrderId, out filledAmount);
            Logger.Debug("mId:" + mId + " filledAmount: " + filledAmount.ToStringInvariant());
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
        private async void ReverseOpenMarketOrder(ExchangeOrderResult order)//, bool completeOnce = false, List<ExchangeOrderResult> openedBuyOrderListA = null, List<ExchangeOrderResult> openedSellOrderListA = null)
        {
            var transAmount = GetParTrans(order);
            //只有在成交后才修改订单数量
            mCurAmount += order.IsBuy ? transAmount : -transAmount;
            Logger.Debug("mId:" + mId + "CurAmount:" + mData.CurAmount);
            Logger.Debug("mId:{0} {1}", mId, mCurAmount);
            var req = new ExchangeOrderRequest();
            req.Amount = transAmount;
            req.IsBuy = !order.IsBuy;
            req.OrderType = OrderType.Market;
            req.MarketSymbol = mData.SymbolB;
            Logger.Debug("mId:" + mId + "  " + "----------------------------ReverseOpenMarketOrder---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            Logger.Debug(req.ToStringInvariant());
            var ticks = DateTime.Now.Ticks;
            try
            {
                var res = await mExchangeBAPI.PlaceOrderAsync(req);
                Logger.Debug("mId:" + mId + "--------------------------------ReverseOpenMarketOrder Result-------------------------------------");
                Logger.Debug((DateTime.Now.Ticks - ticks).ToString());
                Logger.Debug(res.ToString());
                Logger.Debug(res.OrderId);
            }
            catch (Exception ex)
            {
                Logger.Error("mId:{0} {1}", mId, req.ToStringInvariant());
                Logger.Error("mId:" + mId + ex);
                throw ex;
            }
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
            public static T LoadFromDB<T>(string DBKey)
            {
                return RedisDB.Instance.StringGet<T>(DBKey);
            }
        }

    }
}