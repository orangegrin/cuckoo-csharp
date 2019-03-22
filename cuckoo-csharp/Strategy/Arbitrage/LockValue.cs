using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ExchangeSharp;
using System.Linq;
namespace cuckoo_csharp.Strategy.Arbitrage
{
    public class LockValue
    {
        private Config mConfig;
        public IExchangeAPI mExchangeAPI;
        private decimal mIntervalRange = 0.00075m;
        private ExchangeTicker mTicker;

        private Dictionary<string, ExchangeOrderResult> mOrders = new Dictionary<string, ExchangeOrderResult>();

        public decimal mAmount
        {
            get
            {
                return mOrders.Select((op) => { return op.Value.AmountFilled * (op.Value.IsBuy ? 1 : -1); }).Sum();
            }
        }
        public decimal mTargetAmount
        {
            get
            {
                var value = mConfig.KeepValue;
                return mTicker.Last > mConfig.InitialPrice ? value : -value;
            }
        }

        private string mLimitBuyOrderId = "";
        private string mLimitSellOrderId = "";

        private ExchangeOrderResult mLimitBuyOrder
        {
            get
            {
                if (mOrders.ContainsKey(mLimitBuyOrderId) && mOrders[mLimitBuyOrderId].Result == ExchangeAPIOrderResult.Pending)
                {
                    return mOrders[mLimitBuyOrderId];
                }
                return null;
            }
        }
        private ExchangeOrderResult mLimitSellOrder
        {
            get
            {
                if (mOrders.ContainsKey(mLimitSellOrderId) && mOrders[mLimitSellOrderId].Result == ExchangeAPIOrderResult.Pending)
                {
                    return mOrders[mLimitSellOrderId];
                }
                return null;
            }
        }

        public LockValue(Config config)
        {
            mConfig = config;
            mExchangeAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeName);
        }
        public async void Start()
        {
            mExchangeAPI.LoadAPIKeys(mConfig.ExchangeName);
            mExchangeAPI.GetOrderDetailsWebSocket(OnOrderDetailsHandler);
            mExchangeAPI.GetTickersWebSocket(OnTickersHandler, mConfig.Symbol);
            while (true)
            {
                await Task.Delay(1000 * 5);
                if (mTicker == null || mTicker.Last == mConfig.InitialPrice)
                    continue;
                Console.WriteLine("======================={0}=============================", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Console.WriteLine("Initial Price:" + mConfig.InitialPrice);
                Console.WriteLine("Last Price:" + mTicker.Last);
                try
                {
                    await LoopHandler();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
        }

        private void OnTickersHandler(IReadOnlyCollection<KeyValuePair<string, ExchangeTicker>> tickerPairs)
        {
            foreach (var tickerPair in tickerPairs)
            {
                if (tickerPair.Key == mConfig.Symbol)
                {
                    mTicker = tickerPair.Value;
                    Console.WriteLine("New Ticker Bid {0} Ask {1} Last {2}", mTicker.Bid, mTicker.Ask, mTicker.Last);
                    if (mConfig.InitialPrice == 0)
                        mConfig.InitialPrice = mTicker.Last;
                }
            }
        }

        private Task LoopHandler()
        {
            var diff = (mTargetAmount - mAmount) / mTargetAmount;
            if (Math.Abs(diff) < 0.01m)
            {
                Console.WriteLine("diff is too small, return {0}", diff);
                return Task.CompletedTask;
            }
            return PlaceOrder();
        }
        private async Task PlaceOrder()
        {
            if (mTicker.Last > mConfig.InitialPrice)
            {
                await CancelSellOrder();
                await PlaceBuyOrder(mTicker.Last, mConfig.InitialPrice, mIntervalRange);
            }
            else if (mTicker.Last < mConfig.InitialPrice)
            {
                await CancelBuyOrder();
                await PlaceSellOrder(mTicker.Last, mConfig.InitialPrice, mIntervalRange);
            }
        }

        private async Task CancelSellOrder()
        {
            if (mLimitSellOrder != null)
            {
                await mExchangeAPI.CancelOrderAsync(mLimitSellOrder.OrderId);
                Console.WriteLine("Cancel Sell Order {0}", mLimitBuyOrder.OrderId);
            }
        }
        private async Task CancelBuyOrder()
        {
            if (mLimitBuyOrder != null)
            {
                await mExchangeAPI.CancelOrderAsync(mLimitBuyOrder.OrderId);
                Console.WriteLine("Cancel Buy Order, {0}", mLimitBuyOrder.OrderId);
            }
        }

        private async Task PlaceLimitBuyOrder(decimal lastPrice, decimal initPrice, decimal intervalRange)
        {
            Console.WriteLine("Limit");
            decimal price = mTicker.Bid;
            decimal qty = Math.Round((mTargetAmount - mAmount));
            var req = new ExchangeOrderRequest { MarketSymbol = mConfig.Symbol, Price = price, Amount = qty, IsBuy = true, OrderType = OrderType.Limit, ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } } };
            if (mLimitBuyOrder != null)
            {
                req.ExtraParameters.Add("orderID", mLimitBuyOrder.OrderId);
                if (mLimitBuyOrder.Price == price && mLimitBuyOrder.Amount == qty)
                    return;
            }
            var orders = await mExchangeAPI.PlaceOrdersAsync(req);
            mLimitBuyOrderId = orders[0].OrderId;
            mOrders[orders[0].OrderId] = orders[0];
        }
        private async Task PlaceMarketBuyOrder(decimal lastPrice, decimal initPrice, decimal intervalRange)
        {
            Console.WriteLine("Market");
            await CancelBuyOrder();
            decimal price = mTicker.Ask;
            decimal qty = Math.Round((mTargetAmount - mAmount));
            var req = new ExchangeOrderRequest { MarketSymbol = mConfig.Symbol, Amount = qty, IsBuy = true, OrderType = OrderType.Market };
            var order = await mExchangeAPI.PlaceOrderAsync(req);
            mOrders[order.OrderId] = order;
        }
        private async Task PlaceBuyOrder(decimal lastPrice, decimal initPrice, decimal intervalRange)
        {
            Console.WriteLine("Buy");
            var diff = lastPrice / initPrice - 1m;
            Console.WriteLine(diff);
            if (diff < intervalRange)
            {
                //await PlaceLimitBuyOrder(lastPrice, initPrice, intervalRange);
            }
            else
            {
                await PlaceMarketBuyOrder(lastPrice, initPrice, intervalRange);
            }
        }
        private async Task PlaceLimitSellOrder(decimal lastPrice, decimal initPrice, decimal intervalRange)
        {
            Console.WriteLine("Limit");
            decimal price = mTicker.Ask;
            decimal qty = Math.Round(Math.Abs(mTargetAmount - mAmount));
            var req = new ExchangeOrderRequest { MarketSymbol = mConfig.Symbol, Price = price, Amount = qty, IsBuy = false, OrderType = OrderType.Limit, ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } } };
            if (mLimitSellOrder != null)
            {
                req.ExtraParameters.Add("orderID", mLimitSellOrder.OrderId);
                if (mLimitSellOrder.Price == price && mLimitSellOrder.Amount == qty)
                    return;
            }
            var orders = await mExchangeAPI.PlaceOrdersAsync(req);
            mLimitSellOrderId = orders[0].OrderId;
            mOrders[orders[0].OrderId] = orders[0];
        }
        private async Task PlaceMarketSellOrder(decimal lastPrice, decimal initPrice, decimal intervalRange)
        {
            Console.WriteLine("Market");
            await CancelSellOrder();
            decimal price = mTicker.Bid;
            decimal qty = Math.Round(Math.Abs(mTargetAmount - mAmount));
            var req = new ExchangeOrderRequest { MarketSymbol = mConfig.Symbol, Amount = qty, IsBuy = false, OrderType = OrderType.Market };
            var order = await mExchangeAPI.PlaceOrderAsync(req);
            mOrders[order.OrderId] = order;
        }
        private async Task PlaceSellOrder(decimal lastPrice, decimal initPrice, decimal intervalRange)
        {
            Console.WriteLine("Sell");
            var diff = 1m - lastPrice / initPrice;
            Console.WriteLine(diff);
            if (diff < intervalRange)
            {
                //await PlaceLimitSellOrder(lastPrice, initPrice, intervalRange);
            }
            else
            {
                await PlaceMarketSellOrder(lastPrice, initPrice, intervalRange);
            }
        }

        private void OnOrderDetailsHandler(ExchangeOrderResult order)
        {
            if (mOrders.ContainsKey(order.OrderId))
            {
                mOrders[order.OrderId] = order;
                if (order.Result == ExchangeAPIOrderResult.Canceled && order.AmountFilled == 0)
                {
                    mOrders.Remove(order.OrderId);
                }
            }

        }

        public class Config
        {
            public string ExchangeName;
            public string Symbol;
            public decimal KeepValue;
            public decimal InitialPrice;
        }
    }
}