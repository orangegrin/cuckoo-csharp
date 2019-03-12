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
        private ExchangeOrderResult mBidPrder;
        /// <summary>
        /// 当前的平仓订单
        /// </summary>
        private ExchangeOrderResult mCloseOrder;
        /// <summary>
        /// 当前已成交的订单
        /// </summary>
        private ExchangeOrderResult mFilledOrder;
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
            var bidFirst = GetBidFirst(orderbook);
            var askFirst = GetAskFirst(orderbook);
            Console.WriteLine("bid：" + bidFirst.ToString() + " ask:" + askFirst.ToString());
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
            }

            // 如果订单状态是取消则清空ask和bid
            if (order.Result == ExchangeAPIOrderResult.Canceled)
            {
                if (mAskOrder != null && mAskOrder.OrderId == order.OrderId)
                {
                    mAskOrder = null;
                }
                if (mBidPrder != null && mBidPrder.OrderId == order.OrderId)
                {
                    mBidPrder = null;
                }
            }
        }

        #endregion


        #region private utils
        /// <summary>
        /// 获取价格深度合并后的买一价
        /// </summary>
        /// <param name="orderBook"></param>
        /// <returns></returns>
        ExchangeOrderPrice GetBidFirst(ExchangeOrderBook orderBook)
        {
            var first = orderBook.Bids.First();
            var price = first.Key * (1 - mConfig.MinIRS);
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
            var price = first.Key * (1 + mConfig.MinIRS);
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
            orderPrice.Amount = amount;
            orderPrice.Price = price;
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
            orderPrice.Price = price;
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
            mExchangeAAPI.CancelOrderAsync(mBidPrder.OrderId);
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

}

