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
        private ExchangeOrderBook mOrderbookB;

        public CrossMarket(CrossMarketConfig config)
        {
            mConfig = config;
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameA);
            mExchangeBAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameB);
        }
        #region private utils

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
        

        decimal NormalizationMinUnit(decimal price)
        {
            var s = 1 / mConfig.MinUnit;
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
            Console.WriteLine("bid：" + bidFirst.ToString() + " ask:" + askFirst.ToString());
        }


        /// <summary>
        /// 当A交易所的仓位发生改变时触发
        /// </summary>
        /// <param name="position"></param>
        void OnPositionAHandler(ExchangeMarginPositionResult position)
        {

        }
        /// <summary>
        /// 当A交易所的订单发生改变时候触发
        /// </summary>
        /// <param name="order"></param>
        void OnOrderAHandler(ExchangeOrderResult order)
        {

        }


        #endregion
        public void Start()
        {
            Console.WriteLine("Start");
            mExchangeAAPI.LoadAPIKeys(ExchangeName.BitMEX);
            //mExchangeBAPI.LoadAPIKeys(ExchangeName.HBDM);
            //mExchangeBAPI.GetOrderBookWebSocket(OnOrderbookBHandler, 25, mConfig.SymbolB);
            mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
            mExchangeAAPI.GetPositionDetailsWebSocket(OnPositionAHandler);

        }
    }
    public struct CrossMarketConfig
    {
        public string ExchangeNameA;
        public string ExchangeNameB;
        public string SymbolA;
        public string SymbolB;
        public int MaxQty;
        public decimal MinGapRate;
        public decimal FeesA;
        public decimal FeesB;
        public decimal PendingOrderRatio;
        public decimal MinUnit;

    }
}
