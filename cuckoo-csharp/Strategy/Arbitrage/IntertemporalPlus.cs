using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp;
using System.Threading;
using cuckoo_csharp.Tools;

namespace cuckoo_csharp.Strategy.Arbitrage
{        //TODO 添加币本位
         //TODO 添加日志
         //TODO 添加输入比例
    public class IntertemporalPlus
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


        public IntertemporalConfig mConfig
        {
            get
            {
                return RedisDB.Instance.StringGet<IntertemporalConfig>(mDBKey);
            }
            set
            {
                RedisDB.Instance.StringSet(mDBKey, value);
            }
        }

        private string mDBKey;

        private string mRDKey;

        public IntertemporalPlus(IntertemporalConfig config, int id)
        {
            mId = id;
            mDBKey = string.Format("INTERTEMPORAL:CONFIG:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
            mRDKey = string.Format("INTERTEMPORAL:RUNTIMEDATA:{0}:{1}:{2}:{3}:{4}", config.ExchangeNameA, config.ExchangeNameB, config.SymbolA, config.SymbolB, id);
            if (mConfig == null)
                mConfig = config;
            if (mConfig.CurAmount == default(decimal))
                mConfig.CurAmount = config.CurAmount;

            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameA);
            mExchangeBAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameB);

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
            var key = string.Format("AMOUNT:{0}", exchange.Name);
            decimal value = 0;
            foreach (var amount in amounts)
            {
                RedisDB.Instance.HashSetAsync(key, amount.Key, amount.Value.ToString());
                if (amount.Key == symbol)
                    value = amount.Value;
            }
            return value;
        }
        /// <summary>
        /// 计算范围
        /// </summary>
        /// <param name="avgDiff"></param>
        private async void AppendAvgDiff(decimal avgDiff)
        {
            var config = mConfig;
            mDiffList.Add(avgDiff);
            //获取一天的数据
            int maxCount = 86400000 / config.IntervalMillisecond;
            if (mDiffList.Count > maxCount)
                mDiffList.RemoveRange(0, maxCount / 5);
            //优化性能，每一千次计算一次
            if (config.AutoCalcProfitRange && mDiffList.Count % 1000 == 0)
            {
                CalcProfitRange(config, maxCount);
            }
        }

        private void CalcProfitRange(IntertemporalConfig config, int maxCount)
        {
            var avg = mDiffList.Average();
            var range = config.ProfitRange / 2;
            config.A2BDiff = avg + range;
            config.B2ADiff = avg - range;
            if (mDiffList.Count >= maxCount / 2)
                mConfig = config;
            Console.WriteLine("A2BDF {0} B2ADF {1}", config.A2BDiff, config.B2ADiff);
        }

        public void Start()
        {
            mExchangeAAPI.LoadAPIKeys(mConfig.ExchangeNameA);
            mExchangeBAPI.LoadAPIKeys(mConfig.ExchangeNameB);
            mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
            //避免没有订阅成功就开始订单
            Thread.Sleep(4 * 1000);
            mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 20, mConfig.SymbolA);
            mExchangeBAPI.GetFullOrderBookWebSocket(OnOrderbookBHandler, 20, mConfig.SymbolB);
        }

        private void OnOrderbookAHandler(ExchangeOrderBook order)
        {
            mOrderBookA = order;
            //OnOrderBookHandler();
        }

        private void OnOrderbookBHandler(ExchangeOrderBook order)
        {
            mOrderBookB = order;
            OnOrderBookHandler();
        }

        private Task mRunningTask;
        private List<decimal> mDiffList = new List<decimal>();

        async void OnOrderBookHandler()
        {
            if (mOrderBookA == null || mOrderBookB == null)
                return;
            if (mRunningTask != null && !mRunningTask.IsCompleted)
                return;
            mRunningTask = Task.Delay(1000);
            decimal buyPriceA;
            decimal sellPriceA;
            decimal sellPriceB;
            decimal buyPriceB;
            decimal a2bDiff = 0;
            decimal b2aDiff = 0;
            decimal buyAmount = 0;
            buyPriceA = mOrderBookA.Asks.ElementAt(0).Value.Price;
            sellPriceB = mOrderBookB.GetPriceToSell(mConfig.PerTrans);
            mOrderBookB.GetPriceToBuy(mConfig.PerTrans, out buyAmount, out buyPriceB);
            sellPriceA = mOrderBookA.Bids.ElementAt(0).Value.Price;
            //有可能orderbook bids或者 asks没有改变
            if (buyPriceA == 0 || sellPriceA == 0 || sellPriceB == 0 || buyPriceB == 0 || buyAmount == 0)
                return;
            a2bDiff = (sellPriceB / buyPriceA - 1);
            b2aDiff = (buyPriceB / sellPriceA - 1);
            var avgDiff = (a2bDiff + b2aDiff) / 2;
            AppendAvgDiff(avgDiff);

            Logger.Debug("================================================");
            Logger.Debug("BA价差百分比1：" + a2bDiff.ToString());
            Logger.Debug("BA价差百分比2：" + b2aDiff.ToString());
            Logger.Debug("{0} {1} {2} CurAmount:{3}", buyPriceA, sellPriceB, buyAmount, mConfig.CurAmount);
            //满足差价并且
            //只能BBuyASell来开仓，也就是说 ABuyBSell只能用来平仓
            if (a2bDiff > mConfig.A2BDiff && mConfig.CurAmount < mConfig.InitialExchangeBAmount) //满足差价并且当前A空仓
            {
                mRunningTask = A2BExchange(sellPriceA);
            }
            else if (b2aDiff < mConfig.B2ADiff) //满足差价并且没达到最大数量
            {
                //保证不并发
                mRunningTask = Task.Delay(5000);
                if (await SufficientBalance())
                {
                    Logger.Debug("mId:" + mId + "================================================");
                    //Logger.Debug("mId:" + mId + "BA价差百分比2：" + cha2.ToString());
                    Logger.Debug("mId:" + mId + "{0} {1}", buyPriceB, sellPriceA);
                    mRunningTask = B2AExchange(buyPriceA);
                }
                else
                {
                    mRunningTask = null;
                }
            }
            else if (mCurrentLimitOrder != null && mConfig.B2ADiff <= a2bDiff && a2bDiff <= mConfig.A2BDiff)//如果在波动区间中，那么取消挂单
            {
                Logger.Debug("mId:" + mId + "在波动区间中取消订单：" + b2aDiff.ToString());
                ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                cancleRequestA.ExtraParameters.Add("orderID", mCurrentLimitOrder.OrderId);
                mRunningTask = mExchangeAAPI.CancelOrderAsync(mCurrentLimitOrder.OrderId, mConfig.SymbolA);
            }

            if (mRunningTask != null)
            {
                try
                {
                    await mRunningTask;

                }
                catch (System.Exception ex)
                {
                    Logger.Error("mId:" + mId + ex);
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
            requestA.Amount = mConfig.PerTrans;
            requestA.MarketSymbol = mConfig.SymbolA;
            requestA.IsBuy = true;
            requestA.OrderType = mOrderType;
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
                        await mExchangeAAPI.CancelOrderAsync(mCurrentLimitOrder.OrderId, mConfig.SymbolA);
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
                Logger.Debug("mId:" + mId + "Add mCurrentLimitOrder：  " + mCurrentLimitOrder.ToExcleString() + "CurAmount:" + mConfig.CurAmount);
            }
            catch (Exception ex)
            {
                Logger.Debug("mId:" + mId + "数据回滚 ABuyBSell：  CurAmount" + mConfig.CurAmount);
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
            requestA.Amount = mConfig.PerTrans;
            requestA.MarketSymbol = mConfig.SymbolA;
            requestA.IsBuy = false;
            requestA.OrderType = mOrderType;
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
                        if (requestA.Price != mCurrentLimitOrder.Price)
                        {
                            return;
                        }
                    }
                    else
                    {   //如果方向相反那么直接取消
                        ExchangeOrderRequest cancleRequestA = new ExchangeOrderRequest();
                        cancleRequestA.ExtraParameters.Add("orderID", mCurrentLimitOrder.OrderId);
                        //在onOrderCancle的时候处理
                        await mExchangeAAPI.CancelOrderAsync(mCurrentLimitOrder.OrderId, mConfig.SymbolA);
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
                Logger.Debug("mId:" + mId + "数据回滚  BBuyASell CurAmount:" + mConfig.CurAmount);
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
            var bAmount = await GetAmountsAvailableToTradeAsync(mExchangeBAPI, mConfig.AmountSymbol);
            decimal buyPrice;
            decimal exchangeAmount;
            mOrderBookB.GetPriceToBuy(mConfig.PerTrans, out exchangeAmount, out buyPrice);
            var spend = mConfig.PerTrans * buyPrice * 1.5m;
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
                //只有在成交后才修改订单数量
                mConfig.CurAmount += mCurrentLimitOrder.IsBuy ? +mCurrentLimitOrder.Amount : -mCurrentLimitOrder.Amount;
                Logger.Debug("mId:" + mId + "CurAmount:::" + mConfig.CurAmount);
                mOpenAndCloseOrderA.Add(order);
                bool completed = false;
                List<ExchangeOrderResult> openedBuyOrderList = new List<ExchangeOrderResult>();
                List<ExchangeOrderResult> openedSellOrderList = new List<ExchangeOrderResult>();
                if (mConfig.CurAmount == 0)
                {
                    completed = true;
                    Logger.Debug("mId:" + mId + "  completed once trade");
                    foreach (var item in mOpenAndCloseOrderA)
                    {
                        Logger.Debug(item.ToExcleString());
                        if (item.IsBuy)
                        {
                            openedBuyOrderList.Add(item);
                        }
                        else
                        {
                            openedSellOrderList.Add(item);
                        }
                    }
                    mOpenAndCloseOrderA.Clear();
                }
                // 如果 当前挂单和订单相同那么删除
                if (mCurrentLimitOrder != null && mCurrentLimitOrder.OrderId == order.OrderId)
                {
                    //重置数量
                    mCurrentLimitOrder = null;
                }
                ReverseOpenMarketOrder(order, completed, openedBuyOrderList, openedSellOrderList);
            }
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
            Logger.Debug("mId:" + mId + "Canceled  " + order.ToExcleString() + "CurAmount" + mConfig.CurAmount);
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
            if (order.MarketSymbol != mConfig.SymbolA)
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
                    // TODO 战且不处理部分成交的问题
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

        /// <summary>
        /// 反向市价开仓
        /// </summary>
        async void ReverseOpenMarketOrder(ExchangeOrderResult order, bool completeOnce = false, List<ExchangeOrderResult> openedBuyOrderListA = null, List<ExchangeOrderResult> openedSellOrderListA = null)
        {
            var req = new ExchangeOrderRequest();
            req.Amount = mConfig.PerTrans;
            req.IsBuy = !order.IsBuy;
            req.IsMargin = true;
            req.OrderType = OrderType.Market;
            req.MarketSymbol = mConfig.SymbolB;
            Logger.Debug("mId:" + mId + "  " + "----------------------------ReverseOpenMarketOrder---------------------------");
            Logger.Debug(order.ToString());
            Logger.Debug(order.ToExcleString());
            var ticks = DateTime.Now.Ticks;
            try
            {
                var res = await mExchangeBAPI.PlaceOrderAsync(req);
                mOpenAndCloseOrderB.Add(res);
                if (completeOnce)
                {
                    List<ExchangeOrderResult> closeedBuyOrderListB = new List<ExchangeOrderResult>();
                    List<ExchangeOrderResult> closeedSellOrderListB = new List<ExchangeOrderResult>();
                    Logger.Debug("mId:" + mId + "B  completed once trade");
                    foreach (var item in mOpenAndCloseOrderB)
                    {
                        Logger.Debug(item.ToExcleString());
                        if (item.IsBuy)
                        {
                            closeedBuyOrderListB.Add(item);
                        }
                        else
                        {
                            closeedSellOrderListB.Add(item);
                        }
                    }
                    //计算本次获利币数量
                    CountRewardRate(mConfig.PerTrans, openedBuyOrderListA, openedSellOrderListA, closeedBuyOrderListB, closeedSellOrderListB);
                }
                Logger.Debug("mId:" + mId + "--------------------------------ReverseOpenMarketOrder Result-------------------------------------");
                Logger.Debug((DateTime.Now.Ticks - ticks).ToString());
                Logger.Debug(res.ToString());
                Logger.Debug(res.OrderId);
                mRunningTask = Task.Delay(5 * 1000);
                await mRunningTask;

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
        /// 检查当前价格是否会形成市价单
        /// </summary>
        /// <param name="request"></param>
        void OrderFilter(ExchangeOrderRequest request)
        {
            lock (mOrderBookA)
            {
                if (request.IsBuy)
                {
                    var bidFirst = mOrderBookA.Bids.First().Value;
                    if (request.Price > bidFirst.Price)
                    {
                        request.Price = bidFirst.Price;
                    }
                }
                else
                {
                    var askFirst = mOrderBookA.Asks.First().Value;
                    if (request.Price < askFirst.Price)
                    {
                        request.Price = askFirst.Price;
                    }
                }
            }
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
            var s = 1 / mConfig.MinPriceUnit;
            return Math.Round(price * s) / s;
        }
        /// <summary>
        /// 设置当前的订单类型
        /// </summary>
        OrderType mOrderType { get => mConfig.UseLimit ? OrderType.Limit : OrderType.Market; }


    }
}
public class IntertemporalConfig
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
    /// true 限价，false市价
    /// </summary>
    public bool UseLimit = true;
    /// <summary>
    /// 当前仓位数量
    /// </summary>
    public decimal CurAmount = 0;
    /// <summary>
    /// 间隔时间
    /// </summary>
    public int IntervalMillisecond = 5000;
    /// <summary>
    /// 利润范围
    /// 当AutoCalcProfitRange 开启时有效
    /// </summary>
    public decimal ProfitRange = 0.005m;
    /// <summary>
    /// 自动计算利润范围
    /// </summary>
    public bool AutoCalcProfitRange = false;

    public string AmountSymbol = "BTC";
    /// <summary>
    /// 开始交易时候的初始火币数量
    /// </summary>
    public decimal InitialExchangeBAmount = 0m;

    public decimal FeesA;

    public decimal FeesB;
}