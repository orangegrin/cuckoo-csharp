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
        private decimal mLastTransactionPrice;
        private SortedDictionary<decimal, ExchangeOrderResult> mOrderDic = new SortedDictionary<decimal, ExchangeOrderResult>();
        private decimal mInitialAmount;
        private decimal mHedgeAmount;
        private decimal mTotalAmount
        {
            get
            {
                return mInitialAmount + (mPosition.Amount - mHedgeAmount);
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
                await Task.Delay(1000 * 10);
                if (mLastPrice == mInitialPrice)
                    continue;
                if (mPosition == null)
                    continue;
                Console.WriteLine("======================={0}=============================", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Console.WriteLine("Initial Price:" + mInitialPrice);
                Console.WriteLine("Last Price:" + mLastPrice);
                Console.WriteLine("Last Transaction Price:" + mLastTransactionPrice);
                Console.WriteLine("Total Amount:" + mTotalAmount);
                LoopHandler();
                LoopHandler2();

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
                    mLastTransactionPrice = mInitialPrice;
                    mInitialAmount = mConfig.KeepValue / mInitialPrice;
                }
            }
        }

        private async void LoopHandler2()
        {
            var qty = mConfig.KeepValue;
            if (mLastPrice / mInitialPrice < 1m - 0.0075m)
            {
                if (Math.Abs(mPosition.Amount * mLastPrice) < mConfig.KeepValue / 2)
                {
                    if (mHedgeAmount < 0)
                        qty = qty * 2;
                    Console.WriteLine("Market Sell:" + qty);
                    var orderResult = await mExchangeAPI.PlaceOrderAsync(new ExchangeOrderRequest { MarketSymbol = mConfig.Symbol, Amount = qty, IsBuy = false, OrderType = OrderType.Limit, Price = Math.Round(mLastPrice / 2) });
                    mHedgeAmount = orderResult.Amount / orderResult.Price;
                }
            }
            if (mLastPrice / mInitialPrice > 1m + 0.0075m)
            {
                if (mPosition.Amount * mLastPrice < mConfig.KeepValue / 2)
                {
                    if (mHedgeAmount > 0)
                        qty = qty * 2;
                    Console.WriteLine("Market Buy:" + qty);
                    var orderResult = await mExchangeAPI.PlaceOrderAsync(new ExchangeOrderRequest { MarketSymbol = mConfig.Symbol, Amount = qty, IsBuy = true, OrderType = OrderType.Limit, Price = Math.Round(mLastPrice * 2) });

                    mHedgeAmount = orderResult.Amount / orderResult.Price;
                }
            }
            //Console.WriteLine("Total Amount:" + mTotalAmount);
        }

        private void LoopHandler()
        {
            var buyOrders = GetBuyOrders();
            var sellOrders = GetSellOrders();
            var requestOrders = new List<ExchangeOrderRequest>();
            requestOrders.AddRange(buyOrders);
            requestOrders.AddRange(sellOrders);
            var requestOrders2 = new List<ExchangeOrderRequest>(requestOrders.ToArray());
            foreach (var order in mOrderDic)
            {
                bool isMatch = false;
                foreach (var req in requestOrders)
                {
                    if (req.Price == order.Value.Price)
                    {
                        req.ExtraParameters.Add("orderID", order.Value.OrderId);
                        isMatch = true;
                        if (req.Amount == order.Value.Amount)
                            requestOrders2.Remove(req);
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
            if (requestOrders2.Count > 0)
            {
                mExchangeAPI.PlaceOrdersAsync(requestOrders2.ToArray());
                foreach (var order in requestOrders2)
                {
                    Console.WriteLine("{0},{1},{2}", order.IsBuy ? "Buy" : "Sell", order.Price, order.Amount);
                }
            }
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
            for (int i = 1; i < stepCount; i++)
            {
                var stepLen = (mLastPrice - mLastTransactionPrice) / mLastTransactionPrice / mConfig.Step;
                var stepLenUp = NormalizationMinUnit(stepLen) + i * i * mConfig.Step;
                var sellPrice = stepLenUp + mLastTransactionPrice;
                var sellQty = Math.Floor(sellPrice * (mTotalAmount - soldAmount) - mConfig.KeepValue);
                if (Math.Abs(sellQty) < 10)
                    continue;
                soldAmount += sellQty / sellPrice;
                decimal total = (mTotalAmount - soldAmount) * sellPrice;
                //Console.WriteLine("Sell: {0} , qty {1} , total {2}", sellPrice, sellQty, total);
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
            for (int i = 1; i < stepCount; i++)
            {
                var stepLen = (mLastPrice - mLastTransactionPrice) / mLastTransactionPrice / mConfig.Step;
                var stepLenDown = NormalizationMinUnit(stepLen) - i * i * mConfig.Step;
                var buyPrice = stepLenDown + mLastTransactionPrice;
                var buyQty = Math.Floor(buyPrice * (mTotalAmount + soldAmount) - mConfig.KeepValue);
                if (Math.Abs(buyQty) < 10)
                    continue;
                soldAmount -= buyQty / buyPrice;
                decimal total = (mTotalAmount + soldAmount) * buyPrice;
                //Console.WriteLine("Buy: {0} , qty {1} , total {2}", buyPrice, Math.Abs(buyQty), total);
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
            if (order.Result == ExchangeAPIOrderResult.Filled)
            {
                mOrderDic.Remove(order.Price);
                mLastTransactionPrice = mLastPrice;
            }
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
