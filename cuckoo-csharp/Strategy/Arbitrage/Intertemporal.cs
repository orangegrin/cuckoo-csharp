using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp;

namespace cuckoo_csharp.Strategy.Arbitrage
{
    public class Intertemporal
    {
        public IntertemporalConfig mConfig;
        private IExchangeAPI mExchangeAAPI;
        private IExchangeAPI mExchangeBAPI;
        private ExchangeOrderBook mOrderBookA;
        private ExchangeOrderBook mOrderBookB;
        private decimal curAmount = 0;
        public Intertemporal(IntertemporalConfig config)
        {
            mConfig = config;
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameA);
            mExchangeBAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameB);

        }

        public void Start()
        {
            mExchangeAAPI.LoadAPIKeys(mConfig.ExchangeNameA);
            mExchangeBAPI.LoadAPIKeys(mConfig.ExchangeNameB);
            mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 20, mConfig.SymbolA);
            mExchangeBAPI.GetFullOrderBookWebSocket(OnOrderbookBHandler, 20, mConfig.SymbolB);
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

        private Task mRunningTask;

        async void OnOrderBookHandler()
        {
            if (mOrderBookA == null || mOrderBookB == null)
                return;
            if (mRunningTask != null && !mRunningTask.IsCompleted)
                return;
            mRunningTask = Task.Delay(1000 * 5);
            decimal exchangeAmount;
            decimal buyPrice;
            decimal sellPrice;
            mOrderBookA.GetPriceToBuy(mConfig.PerTrans, out exchangeAmount, out buyPrice);
            exchangeAmount = Math.Round(exchangeAmount, 6);
            sellPrice = mOrderBookB.GetPriceToSell(exchangeAmount);
            if (buyPrice == 0 || sellPrice == 0 || exchangeAmount == 0)
                return;
            Console.WriteLine("================================================");
            Console.WriteLine(sellPrice / buyPrice - 1);
            Console.WriteLine("{0} {1} {2}", buyPrice, sellPrice, exchangeAmount);
            if (sellPrice / buyPrice - 1 > mConfig.OPDF && Math.Abs(curAmount) < mConfig.MaxQty)
            {
                OpenPosition(exchangeAmount);
            }
            mOrderBookB.GetPriceToBuy(mConfig.PerTrans, out exchangeAmount, out buyPrice);
            sellPrice = mOrderBookA.GetPriceToSell(exchangeAmount);
            Console.WriteLine(1 - sellPrice / buyPrice);
            Console.WriteLine("{0} {1} {2}", buyPrice, sellPrice, exchangeAmount);
            if (1 - sellPrice / buyPrice < mConfig.CPDF && Math.Abs(curAmount) < mConfig.MaxQty)
            {
                ClosePosition(exchangeAmount);
            }
            await mRunningTask;
        }
        private async void OpenPosition(decimal exchangeAmount)
        {

            try
            {
                //开仓
                ExchangeOrderRequest requestA = new ExchangeOrderRequest();
                requestA.Amount = mConfig.PerTrans;
                requestA.MarketSymbol = mConfig.SymbolA;
                requestA.IsBuy = true;
                requestA.OrderType = OrderType.Market;
                ExchangeOrderRequest requestB = new ExchangeOrderRequest();
                requestB.Amount = exchangeAmount;
                requestB.MarketSymbol = mConfig.SymbolB;
                requestB.IsBuy = false;
                requestB.OrderType = OrderType.Market;

                var orderA = await mExchangeAAPI.PlaceOrderAsync(requestA);
                if (orderA.Result == ExchangeAPIOrderResult.Filled)
                {
                    curAmount += mConfig.PerTrans;
                    var orderB = await mExchangeBAPI.PlaceOrderAsync(requestB);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private async void ClosePosition(decimal exchangeAmount)
        {
            try
            {
                //开仓
                ExchangeOrderRequest requestA = new ExchangeOrderRequest();
                requestA.Amount = mConfig.PerTrans;
                requestA.MarketSymbol = mConfig.SymbolA;
                requestA.IsBuy = false;
                requestA.OrderType = OrderType.Market;
                ExchangeOrderRequest requestB = new ExchangeOrderRequest();
                requestB.Amount = exchangeAmount;
                requestB.MarketSymbol = mConfig.SymbolB;
                requestB.IsBuy = true;
                requestB.OrderType = OrderType.Market;
                var orderA = await mExchangeAAPI.PlaceOrderAsync(requestA);
                if (orderA.Result == ExchangeAPIOrderResult.Filled)
                {
                    curAmount -= mConfig.PerTrans;
                    var orderB = await mExchangeBAPI.PlaceOrderAsync(requestB);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        public class IntertemporalConfig
        {
            public string ExchangeNameA;
            public string ExchangeNameB;
            public string SymbolA;
            public string SymbolB;
            public decimal MaxQty;
            /// <summary>
            /// 开仓差
            /// </summary>
            public decimal OPDF;
            /// <summary>
            /// 平仓差
            /// </summary>
            public decimal CPDF;
            public decimal PerTrans;
        }
    }
}
