using System;
using System.Collections.Generic;
using System.Text;
using ExchangeSharp;
using System.Linq;
using System.Threading.Tasks;

namespace cuckoo_csharp.Strategy.Arbitrage
{
    public class CrossMarket
    {
        private CrossMarketConfig mConfig;
        private IExchangeAPI mExchangeAAPI;
        private IExchangeAPI mExchangeBAPI;
        /// <summary>
        /// A交易所的的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderBookA;
        /// <summary>
        /// B交易所的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderbookB;
        /// <summary>
        /// 当价的做空订单
        /// </summary>
        private ExchangeOrderResult mAskOrder;
        /// <summary>
        /// 当前的做多订单
        /// </summary>
        private ExchangeOrderResult mBidOrder;
        /// <summary>
        /// 当前的平仓订单
        /// </summary>
        private ExchangeOrderResult mCloseOrder;
        /// <summary>
        /// 当前已成交的订单
        /// </summary>
        private ExchangeOrderResult mFilledOrder;
        /// <summary>
        /// 是否为平仓状态
        /// </summary>
        private bool isClosePositionState = false;
        /// <summary>
        /// 正在操作订单
        /// </summary>
        private Task mRunningTask;

        public CrossMarket(CrossMarketConfig config)
        {

            mConfig = config;
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameA);
            mExchangeBAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameB);
        }
        #region handler
        bool isReady
        {
            get
            {
                if (smaA == null)
                    return false;
                if (smaB == null)
                    return false;
                return true;
            }
        }
        /// <summary>
        /// 当B交易所的订单发生改变时
        /// </summary>
        /// <param name="orderbook"></param>
        async void OnOrderbookBHandler(ExchangeOrderBook orderbook)
        {
            mOrderbookB = orderbook;
            if (!isReady)
                return;
            if (mRunningTask == null || mRunningTask.IsCompleted)
            {
                try
                {
                    if (!isClosePositionState)
                    {
                        mRunningTask = OpenPosition();
                    }
                    else if (mAskOrder == null && mBidOrder == null && mFilledOrder != null)
                    {
                        mRunningTask = ClosePosition();
                    }
                    if (mRunningTask != null)
                        await mRunningTask;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
        }

        void OnOrderbookAHandler(ExchangeOrderBook orderbook)
        {
            mOrderBookA = orderbook;

        }

        bool IsMyOrder(string orderId)
        {
            if (mAskOrder != null && mAskOrder.OrderId == orderId)
            {
                return true;
            }
            if (mBidOrder != null && mBidOrder.OrderId == orderId)
            {
                return true;
            }

            if (mCloseOrder != null && mCloseOrder.OrderId == orderId)
            {
                return true;
            }

            return false;
        }
        void OnOrderFilled(ExchangeOrderResult order)
        {
            Console.WriteLine("-------------------- Order Filed ---------------------------");
            Console.WriteLine(order.OrderId);
            mFilledOrder = order;
            if (mAskOrder != null && order.OrderId == mAskOrder.OrderId)
                mAskOrder = null;
            if (mBidOrder != null && order.OrderId == mBidOrder.OrderId)
                mBidOrder = null;
            if (mCloseOrder != null && mCloseOrder.OrderId == order.OrderId)
            {
                SwicthStateToOpenPosition();
            }
            else
            {
                SwitchStateToClosePosition();
            }
            ReverseOpenMarketOrder(order);
        }
        void OnOrderCanceled(ExchangeOrderResult order)
        {
            Console.WriteLine("-------------------- Order Canceled ---------------------------");
            Console.WriteLine(order.OrderId);
            if (mAskOrder != null && mAskOrder.OrderId == order.OrderId)
            {
                mAskOrder = null;
            }
            if (mBidOrder != null && mBidOrder.OrderId == order.OrderId)
            {
                mBidOrder = null;
            }
            if (mCloseOrder != null && mCloseOrder.OrderId == order.OrderId)
            {
                mCloseOrder = null;
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
            {
                return;
            }
            switch (order.Result)
            {
                case ExchangeAPIOrderResult.Unknown:
                    break;
                case ExchangeAPIOrderResult.Filled:
                    OnOrderFilled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartially:
                    // TODO 战且不处理部分成交的问题
                    break;
                case ExchangeAPIOrderResult.Pending:
                    break;
                case ExchangeAPIOrderResult.Error:
                    break;
                case ExchangeAPIOrderResult.Canceled:
                    OnOrderCanceled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartiallyAndCancelled:
                    break;
                case ExchangeAPIOrderResult.PendingCancel:
                    break;
                default:
                    break;
            }
        }

        #endregion


        #region private utils
        /// <summary>
        /// 反向市价开仓
        /// </summary>
        async void ReverseOpenMarketOrder(ExchangeOrderResult order)
        {
            var req = new ExchangeOrderRequest();
            req.Amount = order.Amount;
            req.Price = order.Price;
            req.IsBuy = !order.IsBuy;
            req.IsMargin = true;
            req.OrderType = OrderType.Market;
            req.MarketSymbol = mConfig.SymbolB;
            Console.WriteLine("----------------------------ReverseOpenMarketOrder---------------------------");
            var res = await mExchangeBAPI.PlaceOrderAsync(req);
            Console.WriteLine(res.ToString());
            Console.WriteLine(res.OrderId);
        }
        /// <summary>
        /// 开仓
        /// </summary>
        async Task OpenPosition()
        {
            if (mOrderBookA == null)
                return;
            var bidPrice = GetLimitBidOrderPair(mOrderbookB);
            var askPrice = GetLimitAskOrderPair(mOrderbookB);
            var bidReq = new ExchangeOrderRequest()
            {
                Amount = bidPrice.Amount,
                Price = bidPrice.Price,
                MarketSymbol = mConfig.SymbolA,
                IsBuy = true,
                ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
            };
            var askReq = new ExchangeOrderRequest()
            {
                Amount = askPrice.Amount,
                Price = askPrice.Price,
                MarketSymbol = mConfig.SymbolA,
                IsBuy = false,
                ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
            };

            if (mAskOrder != null)
            {
                askReq.ExtraParameters.Add("orderID", mAskOrder.OrderId);
            }
            if (mBidOrder != null)
            {
                bidReq.ExtraParameters.Add("orderID", mBidOrder.OrderId);
            }
            var requests = OrdersFilter(bidReq, askReq);
            if (requests.Length > 0 && !isClosePositionState)
            {
                Console.WriteLine("--------------- OpenPosition ------------------");
                if (mBidOrder != null)
                    Console.WriteLine(mBidOrder.OrderId);
                if (mAskOrder != null)
                    Console.WriteLine(mAskOrder.OrderId);
                var orders = await mExchangeAAPI.PlaceOrdersAsync(requests);
                foreach (var o in orders)
                {
                    if (o.IsBuy)
                    {
                        mBidOrder = o;
                    }
                    else
                    {
                        mAskOrder = o;
                    }
                }
                if (mBidOrder.Result == ExchangeAPIOrderResult.Canceled)
                    OnOrderCanceled(mBidOrder);
                if (mAskOrder.Result == ExchangeAPIOrderResult.Canceled)
                    OnOrderCanceled(mAskOrder);
            }
        }
        /// <summary>
        /// 平仓
        /// </summary>
        async Task ClosePosition()
        {
            var req = new ExchangeOrderRequest()
            {
                Amount = mFilledOrder.Amount,
                Price = mFilledOrder.IsBuy ? GetBidFirst(mOrderbookB).Price : GetAskFirst(mOrderbookB).Price,
                MarketSymbol = mConfig.SymbolA,
                IsBuy = !mFilledOrder.IsBuy,
                ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
            };
            if (mCloseOrder != null)
            {
                req.ExtraParameters.Add("orderID", mCloseOrder.OrderId);
            }
            var requests = OrdersFilter(req);
            if (requests.Length > 0)
            {
                if (!isClosePositionState)
                    return;
                Console.WriteLine("--------------- ClosePosition ------------------");
                if (mCloseOrder != null)
                    Console.WriteLine(mCloseOrder.OrderId);
                var orders = await mExchangeAAPI.PlaceOrdersAsync(requests);
                foreach (var o in orders)
                {
                    mCloseOrder = o;
                }
                if (mCloseOrder.Result == ExchangeAPIOrderResult.Canceled)
                    OnOrderCanceled(mCloseOrder);
            }
        }


        /// <summary>
        /// 价格过滤器
        /// </summary>
        /// <param name="requests"></param>
        /// <returns></returns>
        ExchangeOrderRequest[] OrdersFilter(params ExchangeOrderRequest[] requests)
        {
            List<ExchangeOrderRequest> list = new List<ExchangeOrderRequest>();
            var len = requests.Count();
            for (int i = 0; i < len; i++)
            {
                var req = requests[i];
                OrderFilter(req);
                if (req.Amount > mConfig.MaxQty)
                    req.Amount = mConfig.MaxQty;
                if (isClosePositionState)
                {
                    if (LimitOrderFilter2(req, mCloseOrder))
                        list.Add(req);
                }
                else if (LimitOrderFilter2(req, req.IsBuy ? mBidOrder : mAskOrder))
                {
                    list.Add(req);
                }
            }
            return list.ToArray();
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
        /// 检查是否需要修改
        /// </summary>
        /// <param name="request"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        bool LimitOrderFilter2(ExchangeOrderRequest request, ExchangeOrderResult result)
        {
            if (result == null)
                return true;
            var priceDiff = (result.Price - request.Price) / request.Price / mConfig.MinIRS;
            var amountDiff = (result.Amount - request.Amount) / request.Amount;
            if (Math.Abs(priceDiff) < 0.2m && Math.Abs(amountDiff) < 0.2m)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 获取价格深度合并后的买一价
        /// </summary>
        /// <param name="orderBook"></param>
        /// <returns></returns>
        ExchangeOrderPrice GetBidFirst(ExchangeOrderBook orderBook)
        {
            var first = orderBook.Bids.First();
            var price = first.Key - mConfig.MinPriceUnit;
            price = NormalizationMinUnit(price);
            var amount = orderBook.Bids.Where(op => op.Key > price).Select((op) => { return op.Value.Amount; }).Sum();
            return new ExchangeOrderPrice()
            {
                Price = price,
                Amount = amount
            };
        }
        /// <summary>
        /// 获取价格深度合并后的卖一价
        /// </summary>
        /// <param name="orderBook"></param>
        /// <returns></returns>
        ExchangeOrderPrice GetAskFirst(ExchangeOrderBook orderBook)
        {
            var first = orderBook.Asks.First();
            var price = first.Key + mConfig.MinPriceUnit;
            price = NormalizationMinUnit(price);
            var amount = orderBook.Asks.Where(op => op.Key < price).Select((op) => { return op.Value.Amount; }).Sum();
            return new ExchangeOrderPrice()
            {
                Price = price,
                Amount = amount
            };
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
        /// 根据orderbook数据计算对手交易所的限价单开仓价格与数量
        /// 买价 = 对手卖价 * （1 - 利润比） - 交易手续费 * 2 - 标准差
        /// </summary>
        /// <param name="orderBook"></param>
        /// <returns></returns>
        ExchangeOrderPrice GetLimitBidOrderPair(ExchangeOrderBook orderBook)
        {
            var orderPrice = new ExchangeOrderPrice();
            var askFirst = GetAskFirst(orderBook);
            var fees = mConfig.Fees * askFirst.Price;
            var price = askFirst.Price * (1 - mConfig.MinIRS) - fees * 2 + GetStandardDev();
            var amount = askFirst.Amount * mConfig.POR;
            price = mExchangeAAPI.PriceComplianceCheck(price);
            amount = mExchangeAAPI.AmountComplianceCheck(amount);
            orderPrice.Amount = amount;
            orderPrice.Price = NormalizationMinUnit(price);
            return orderPrice;
        }
        /// <summary>
        /// 根据orderbook数据计算对手交易所的限价单开仓价格与数量
        /// 卖价 = 对手买价 * （1 + 利润比） + 交易手续费 * 2 + 标准差
        /// </summary>
        /// <param name="orderBook"></param>
        /// <returns></returns>
        ExchangeOrderPrice GetLimitAskOrderPair(ExchangeOrderBook orderBook)
        {
            var orderPrice = new ExchangeOrderPrice();
            var bidFirst = GetBidFirst(orderBook);
            var fees = mConfig.Fees * bidFirst.Price;
            var price = bidFirst.Price * (mConfig.MinIRS + 1) + fees * 2 + GetStandardDev();
            var amount = bidFirst.Amount * mConfig.POR;
            orderPrice.Amount = amount;
            orderPrice.Price = NormalizationMinUnit(price);
            return orderPrice;
        }

        /// <summary>
        /// 获取两个交易所的MA的差
        /// </summary>
        /// <returns></returns>
        decimal GetStandardDev()
        {
            var dev = smaA[0] - smaB[0];
            return dev.ConvertInvariant<decimal>();
        }
        private IEnumerable<MarketCandle> marketCandleA;
        private IEnumerable<MarketCandle> marketCandleB;
        private double[] smaA;
        private double[] smaB;

        private async Task GetExchangeCandles(int periodSeconds)
        {
            marketCandleA = await mExchangeAAPI.GetCandlesAsync(mConfig.SymbolA, periodSeconds, limit: 100);
            marketCandleB = await mExchangeBAPI.GetCandlesAsync(mConfig.SymbolB, periodSeconds, limit: 100);
            var closeA = marketCandleA.Select((candle, index) => { return candle.ClosePrice.ConvertInvariant<float>(); }).Reverse();
            var closeB = marketCandleB.Select((candle, index) => { return candle.ClosePrice.ConvertInvariant<float>(); }).Reverse();
            int outBegIdx;
            int outNBElement;
            smaA = new double[closeA.Count()];
            smaB = new double[closeB.Count()];
            TicTacTec.TA.Library.Core.Sma(0, closeA.Count() - 1, closeA.ToArray(), mConfig.TimePeriod, out outBegIdx, out outNBElement, smaA);
            TicTacTec.TA.Library.Core.Sma(0, closeB.Count() - 1, closeB.ToArray(), mConfig.TimePeriod, out outBegIdx, out outNBElement, smaB);
            Console.WriteLine(GetStandardDev());
            await Task.Delay(periodSeconds * 1000);
        }
        private async void WhileGetExchangeCandles()
        {
            while (true)
            {
                await GetExchangeCandles(mConfig.PeriodFreq);
            }
        }
        /// <summary>
        /// 切换到开仓状态
        /// </summary>
        void SwicthStateToOpenPosition()
        {
            Console.WriteLine("---------------------Switch State to Open Position-----------------------------");
            isClosePositionState = false;
            mCloseOrder = null;
        }
        /// <summary>
        /// 切换到平仓状态
        /// </summary>
        async void SwitchStateToClosePosition()
        {
            Console.WriteLine("---------------------Switch State to Close Position-----------------------------");
            isClosePositionState = true;
            if (mBidOrder != null)
                mRunningTask = mExchangeAAPI.CancelOrderAsync(mBidOrder.OrderId, mConfig.SymbolA);
            if (mAskOrder != null)
                mRunningTask = mExchangeAAPI.CancelOrderAsync(mAskOrder.OrderId, mConfig.SymbolA);
            await mRunningTask;
            mAskOrder = null;
            mBidOrder = null;

        }

        #endregion



        public async void Start()
        {
            Console.WriteLine("Start");
            mExchangeAAPI.LoadAPIKeys(ExchangeName.BitMEX);
            mExchangeBAPI.LoadAPIKeys(ExchangeName.HBDM);
            WhileGetExchangeCandles();
            mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 25, mConfig.SymbolA);
            mExchangeBAPI.GetFullOrderBookWebSocket(OnOrderbookBHandler, 25, mConfig.SymbolB);
            mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
        }
    }
    public struct CrossMarketConfig
    {
        /// <summary>
        /// A交易所名称
        /// </summary>
        public string ExchangeNameA;
        /// <summary>
        /// B交易所名称
        /// </summary>
        public string ExchangeNameB;
        /// <summary>
        /// A交易所币种的Symbol
        /// </summary>
        public string SymbolA;
        /// <summary>
        /// B交易所币种的的Symbol
        /// </summary>
        public string SymbolB;
        /// <summary>
        /// 最大交易量
        /// maximum quantity
        /// </summary>
        public int MaxQty;
        /// <summary>
        /// Minimum Interest Rate Spread
        /// 要求的最小利差
        /// </summary>
        public decimal MinIRS;
        /// <summary>
        /// A交易所手续费
        /// </summary>
        public decimal FeesA;
        /// <summary>
        /// B交易所手续费
        /// </summary>
        public decimal FeesB;
        /// <summary>
        /// Pending order ratio
        /// 待定订单比例
        /// </summary>
        public decimal POR;
        /// <summary>
        /// 最小价格单位
        /// </summary>
        public decimal MinPriceUnit;
        /// <summary>
        /// 期间频率 单位为（秒）
        /// </summary>
        public int PeriodFreq;
        /// <summary>
        /// 时间长度
        /// </summary>
        public int TimePeriod;
        /// <summary>
        /// 两个交易所的总交易手续费率
        /// </summary>
        public decimal Fees
        {
            get
            {
                return FeesA + FeesB;
            }
        }

    }

}

