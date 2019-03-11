using System;
using System.Collections.Generic;
using System.Text;
using ExchangeSharp;
using System.Linq;

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

        public CrossMarket(CrossMarketConfig config)
        {
            mConfig = config;
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameA);
            mExchangeBAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameB);
        }
        #region handler
        /// <summary>
        /// 当B交易所的订单发生改变时
        /// </summary>
        /// <param name="orderbook"></param>
        void OnOrderbookBHandler(ExchangeOrderBook orderbook)
        {
            mOrderbookB = orderbook;
            if (!isClosePositionState)
            {
                OpenPosition();
            }
            else
            {
                ClosePosition();
            }
        }

        void OnOrderbookAHandler(ExchangeOrderBook orderbook)
        {
            mOrderBookA = orderbook;
        }

        /// <summary>
        /// 当A交易所的订单发生改变时候触发
        /// </summary>
        /// <param name="order"></param>
        void OnOrderAHandler(ExchangeOrderResult order)
        {
            if (order.MarketSymbol != mConfig.SymbolA)
                return;
            Console.WriteLine(order.ToString());

            // TODO 战且不处理部分成交的问题
            //if (ExchangeAPIOrderResult.FilledPartially == order.Result)
            //{
            //    mExchangeAAPI.CancelOrderAsync(order.OrderId);
            //}


            if (order.Result == ExchangeAPIOrderResult.Filled)
            {
                mFilledOrder = order;
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

            // 如果订单状态是取消则清空ask和bid
            if (order.Result == ExchangeAPIOrderResult.Canceled)
            {
                if (mAskOrder != null && mAskOrder.OrderId == order.OrderId)
                {
                    mAskOrder = null;
                }
                if (mBidOrder != null && mBidOrder.OrderId == order.OrderId)
                {
                    mBidOrder = null;
                }
            }
        }

        #endregion


        #region private utils
        /// <summary>
        /// 反向市价开仓
        /// </summary>
        void ReverseOpenMarketOrder(ExchangeOrderResult order)
        {
            var req = new ExchangeOrderRequest();
            req.Amount = order.Amount;
            req.Price = order.Amount;
            req.IsBuy = !order.IsBuy;
            req.IsMargin = true;
            req.OrderType = ExchangeSharp.OrderType.Market;
        }
        /// <summary>
        /// 开仓
        /// </summary>
        void OpenPosition()
        {
            if (mOrderBookA == null)
                return;
            var bidPrice = GetLimitBidOrderPair(mOrderbookB);
            var askPrice = GetLimitAskOrderPair(mOrderbookB);
            var bidReq = new ExchangeOrderRequest()
            {
                Amount = bidPrice.Amount,
                Price = bidPrice.Price,
                IsBuy = true
            };
            var askReq = new ExchangeOrderRequest()
            {
                Amount = askPrice.Amount,
                Price = askPrice.Price,
                IsBuy = false
            };
            OpenLimitOrder(bidReq, askReq);
        }
        /// <summary>
        /// 平仓
        /// </summary>
        void ClosePosition()
        {
            var req = new ExchangeOrderRequest()
            {
                Amount = mFilledOrder.Amount,
                Price = mFilledOrder.IsBuy ? GetBidFirst(mOrderbookB).Price : GetAskFirst(mOrderbookB).Price,
                IsBuy = !mFilledOrder.IsBuy,
            };
            OpenLimitOrder(req);
        }


        void OpenLimitOrder(params ExchangeOrderRequest[] requests)
        {
            requests = OrderFilter(requests);
            Logger.Debug("OpenLimitOrder");
            //mExchangeAAPI.PlaceOrdersAsync(requests);
        }
        /// <summary>
        /// 价格过滤器
        /// </summary>
        /// <param name="requests"></param>
        /// <returns></returns>
        ExchangeOrderRequest[] OrderFilter(ExchangeOrderRequest[] requests)
        {
            List<ExchangeOrderRequest> list = new List<ExchangeOrderRequest>();
            var len = requests.Count();
            for (int i = 0; i < len; i++)
            {
                var req = requests[i];
                OrderFilter(req);
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
            var priceDiff = (result.Price - request.Price) / request.Price;
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
            var price = askFirst.Price * (1 - mConfig.MinIRS) - fees * 2 - GetStandardDev();
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
            string periodfreq = mConfig.PeriodFreq;
            float timeperiod = mConfig.TimePeriod;
            //TODO 计算两个交易所MA的价差
            return 0m;
        }
        /// <summary>
        /// 切换到开仓状态
        /// </summary>
        void SwicthStateToOpenPosition()
        {
            isClosePositionState = false;
            mCloseOrder = null;
        }
        /// <summary>
        /// 切换到平仓状态
        /// </summary>
        void SwitchStateToClosePosition()
        {
            isClosePositionState = true;
            mExchangeAAPI.CancelOrderAsync(mAskOrder.OrderId);
            mExchangeAAPI.CancelOrderAsync(mBidOrder.OrderId);
        }

        #endregion



        public void Start()
        {
            Console.WriteLine("Start");
            mExchangeAAPI.LoadAPIKeys(ExchangeName.BitMEX);
            mExchangeBAPI.LoadAPIKeys(ExchangeName.HBDM);
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
        /// 期间频率 Min,H,D,M
        /// </summary>
        public string PeriodFreq;
        /// <summary>
        /// 时间长度
        /// </summary>
        public float TimePeriod;
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

    #region Enum
    enum Side
    {
        Buy = 0,
        Sell,
    }
    enum OrderType
    {
        Limit = 0,
        Market,
    }
    enum OrderResultType
    {
        Unknown = 0,
        Filled,
        FilledPartially,
        Pending,
        Error,
        Canceled,
        PendingCancel,
    }
    #endregion
}

