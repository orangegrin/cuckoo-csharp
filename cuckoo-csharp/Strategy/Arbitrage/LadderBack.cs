using ExchangeSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace cuckoo_csharp.Strategy.Arbitrage
{
    public class LadderBack
    {
        private Config mConfig;
        private IExchangeAPI mExchangeAPI;
        private ExchangeMarginPositionResult mPosition;
        private decimal mInitialPrice;
        private decimal mLastPrice;
        private SortedDictionary<decimal, ExchangeOrderResult> mOrderDic = new SortedDictionary<decimal, ExchangeOrderResult>();
        private decimal mInitialQty;
        private decimal mTotalAmount
        {
            get
            {
                return mInitialQty + mPosition.Amount;
            }
        }
        public LadderBack(Config config)
        {
            mConfig = config;
            mExchangeAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeName);
        }
        public async void Start()
        {
            mExchangeAPI.LoadAPIKeys(mConfig.ExchangeName);
            mExchangeAPI.GetPositionDetailsWebSocket(OnPositionHandler);
            mExchangeAPI.GetOrderDetailsWebSocket(OnOrderDetailsHandler);
            mExchangeAPI.GetTradesWebSocket(OnTradesHandler, mConfig.Symbol);
            while (true)
            {
                OnLastPriceChangedHandler();
                await Task.Delay(1000 * 10);
            }
        }

        private void OnTradesHandler(KeyValuePair<string, ExchangeTrade> obj)
        {
            if (mLastPrice != obj.Value.Price)
            {
                mLastPrice = obj.Value.Price;
                if (mInitialPrice == 0)
                {
                    mInitialPrice = mLastPrice;
                    mInitialQty = mConfig.KeepValue / mInitialPrice;
                }
                //OnLastPriceChangedHandler();
            }
        }

        private void OnLastPriceChangedHandler()
        {
            if (mLastPrice == mInitialPrice)
                return;
            if (mPosition == null)
                return;

            Console.WriteLine("Initial Price:" + mInitialPrice);
            Console.WriteLine("Last Price:" + mLastPrice);
            var buyOrders = GetBuyOrders();
            var sellOrders = GetSellOrders();
            var requestOrders = new List<ExchangeOrderRequest>();
            requestOrders.AddRange(buyOrders);
            requestOrders.AddRange(sellOrders);
            foreach (var order in mOrderDic)
            {
                bool isMatch = false;
                foreach (var req in requestOrders)
                {
                    if (req.Price == order.Value.Price)
                    {
                        req.ExtraParameters.Add("orderID", order.Value.OrderId);
                        isMatch = true;
                        break;
                    }
                }
                if (!isMatch)
                {
                    try
                    {
                        mExchangeAPI.CancelOrderAsync(order.Value.OrderId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                    }
                }
            }
            mExchangeAPI.PlaceOrdersAsync(requestOrders.ToArray());
        }

        decimal NormalizationMinUnit(decimal price)
        {
            var s = 1 / mConfig.MinPriceUnit;
            return Math.Round(price * s) / s;
        }
        private List<ExchangeOrderRequest> GetSellOrders(int stepCount = 5)
        {
            decimal soldAmount = 0m;
            List<ExchangeOrderRequest> orderRequests = new List<ExchangeOrderRequest>();
            for (int i = 0; i < stepCount; i++)
            {
                var stepLen = (mLastPrice - mInitialPrice) / mInitialPrice / mConfig.Step;
                var stepLenUp = NormalizationMinUnit(stepLen) + i * i * mConfig.Step;
                var sellPrice = stepLenUp + mInitialPrice;
                var sellQty = Math.Floor(sellPrice * (mTotalAmount - soldAmount) - mConfig.KeepValue);
                if (sellQty == 0)
                    continue;
                soldAmount += sellQty / sellPrice;
                decimal total = (mTotalAmount - soldAmount) * sellPrice;
                Console.WriteLine("Sell: {0} , qty {1} , total {2}", sellPrice, sellQty, total);
                var req = new ExchangeOrderRequest()
                {
                    Amount = Math.Abs(sellQty),
                    Price = sellPrice,
                    MarketSymbol = mConfig.Symbol,
                    IsBuy = false,
                    ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
                };
                orderRequests.Add(req);
            }
            return orderRequests;
        }
        private List<ExchangeOrderRequest> GetBuyOrders(int stepCount = 5)
        {
            decimal soldAmount = 0m;
            List<ExchangeOrderRequest> orderRequests = new List<ExchangeOrderRequest>();
            for (int i = 0; i < stepCount; i++)
            {
                var stepLen = (mLastPrice - mInitialPrice) / mInitialPrice / mConfig.Step;
                var stepLenDown = NormalizationMinUnit(stepLen) - i * i * mConfig.Step;
                var buyPrice = stepLenDown + mInitialPrice;
                var buyQty = Math.Floor(buyPrice * (mTotalAmount + soldAmount) - mConfig.KeepValue);
                if (buyQty == 0)
                    continue;
                soldAmount -= buyQty / buyPrice;
                decimal total = (mTotalAmount + soldAmount) * buyPrice;
                Console.WriteLine("Buy: {0} , qty {1} , total {2}", buyPrice, Math.Abs(buyQty), total);
                var req = new ExchangeOrderRequest()
                {
                    Amount = Math.Abs(buyQty),
                    Price = buyPrice,
                    MarketSymbol = mConfig.Symbol,
                    IsBuy = true,
                    ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
                };
                orderRequests.Add(req);
            }
            orderRequests.Reverse();
            return orderRequests;
        }

        private void OnOrderDetailsHandler(ExchangeOrderResult order)
        {
            mOrderDic[order.Price] = order;
            if (order.Result == ExchangeAPIOrderResult.Filled || order.Result == ExchangeAPIOrderResult.Canceled || order.Result == ExchangeAPIOrderResult.FilledPartially)
                mOrderDic.Remove(order.Price);
        }

        private void OnPositionHandler(ExchangeMarginPositionResult position)
        {
            if (position.MarketSymbol == mConfig.Symbol)
                mPosition = position;
        }

        public class Config
        {
            public string ExchangeName;
            public string Symbol;
            public decimal Step;
            public decimal KeepValue;
            public decimal MinPriceUnit;
        }
    }
}
