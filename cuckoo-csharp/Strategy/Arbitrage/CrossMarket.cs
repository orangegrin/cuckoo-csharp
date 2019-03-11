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
        /// B交易所的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderbookB;
        /// <summary>
        /// 当前A交易所的仓位
        /// </summary>
        private ExchangeMarginPositionResult mPosition;

        public CrossMarket(CrossMarketConfig config)
        {
            mConfig = config;
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameA);
            mExchangeBAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameB);
        }
        #region private utils
        /// <summary>
        /// 获取价格深度合并后的买一价
        /// </summary>
        /// <param name="orderBook"></param>
        /// <returns></returns>
        ExchangeOrderPrice GetBidFirst(ExchangeOrderBook orderBook)
        {
            var first = orderBook.Bids.First();
            var price = first.Key * (1 - mConfig.MinGapRate);
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
            var price = first.Key * (1 + mConfig.MinGapRate);
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
        #endregion
        #region Handler
        /// <summary>
        /// 当B交易所的订单发生改变时
        /// </summary>
        /// <param name="orderbook"></param>
        void OnOrderbookBHandler(ExchangeOrderBook orderbook)
        {
            mOrderbookB = orderbook;
            var bidFirst = GetBidFirst(orderbook);
            var askFirst = GetAskFirst(orderbook);
            if (mPosition != null)
            {
                if (mPosition.Amount != 0)
                {
                    // 先平仓
                    Console.WriteLine("准备平仓");
                    ClosePosition(orderbook);
                }
                else
                {
                    Console.WriteLine("双向开仓");
                    //双向开仓
                    ConvergeOrders(orderbook);
                }
            }
            else//表示刚刚开始程序，先开仓
            {   //双向开仓
                Console.WriteLine("首次开仓");
                ConvergeOrders(orderbook);
            }
            Console.WriteLine("bid：" + bidFirst.ToString() + " ask:" + askFirst.ToString());
        }


        /// <summary>
        /// 当A交易所的仓位发生改变时触发
        /// </summary>
        /// <param name="position"></param>
        void OnPositionAHandler(ExchangeMarginPositionResult position)
        {
            mPosition = position;
        }
        /// <summary>
        /// 当A交易所的订单发生改变时候触发
        /// </summary>
        /// <param name="order"></param>
        void OnOrderAHandler(ExchangeOrderResult order)
        {
            Console.WriteLine(order.ToString());
        }
        #endregion
        #region  
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
            var price = askFirst.Price * (1 - mConfig.MinGapRate) - fees * 2 - GetStandardDev();
            var amount = askFirst.Amount * mConfig.PendingOrderRatio;
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
            var price = bidFirst.Price * (mConfig.MinGapRate + 1) + fees * 2 + GetStandardDev();
            var amount = bidFirst.Amount * mConfig.PendingOrderRatio;
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

        void ClosePosition(ExchangeOrderBook orderBook)
        {


        }
        void ConvergeOrders(ExchangeOrderBook orderbook)
        {

        }
        #endregion


        public void Start()
        {
            Console.WriteLine("Start");
            mExchangeAAPI.LoadAPIKeys(ExchangeName.BitMEX);
            mExchangeBAPI.LoadAPIKeys(ExchangeName.HBDM);
            mExchangeBAPI.GetOrderBookWebSocket(OnOrderbookBHandler, 25, mConfig.SymbolB);
            mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
            //mExchangeAAPI.GetPositionDetailsWebSocket(OnPositionAHandler);

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
        public int MaxQty;
        public decimal MinGapRate;
        /// <summary>
        /// A交易所手续费
        /// </summary>
        public decimal FeesA;
        /// <summary>
        /// B交易所手续费
        /// </summary>
        public decimal FeesB;
        /// <summary>
        /// 待定订单比例
        /// </summary>
        public decimal PendingOrderRatio;
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

