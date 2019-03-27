﻿using System;
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
        /// <summary>
        /// 自身id
        /// </summary>
        private int mId;


        public CrossMarket(CrossMarketConfig config, int mId)
        {
            this.mId = mId;
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
                    if (mRunningTask != null && !mRunningTask.IsCompleted)
                    {
                        var ticks = DateTime.Now.Ticks;
                        await mRunningTask;
                        Console.WriteLine("mId:"+mId+"  "+"OnOrderbookBHandler mRunningTask use time:" + (DateTime.Now.Ticks - ticks));
                    }
                }
                catch (Exception ex)
                {
                    mRunningTask = Task.Delay(5 * 1000);
                    await mRunningTask;
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
            Console.WriteLine("mId:"+mId+"  "+"-------------------- Order Filed ---------------------------");
            Console.WriteLine(order.ToString());
            Console.WriteLine(order.ToExcleString());
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
            try
            {
                ReverseOpenMarketOrder(order);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

        }
        void OnOrderCanceled(ExchangeOrderResult order)
        {
            Console.WriteLine("mId:"+mId+"  "+"-------------------- Order Canceled ---------------------------");
            Console.WriteLine(order.ToExcleString());
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
                return;

            if (mAskOrder != null && mAskOrder.OrderId == order.OrderId)
                mAskOrder = order;
            if (mBidOrder != null && mBidOrder.OrderId == order.OrderId)
                mBidOrder = order;
            if (mCloseOrder != null && mCloseOrder.OrderId == order.OrderId)
                mCloseOrder = order;

            switch (order.Result)
            {
                case ExchangeAPIOrderResult.Unknown:
                    Console.WriteLine("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Console.WriteLine(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Filled:
                    OnOrderFilled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartially:
                    // TODO 战且不处理部分成交的问题
                    Console.WriteLine("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Console.WriteLine(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Pending:
                    Console.WriteLine("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Console.WriteLine(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Error:
                    Console.WriteLine("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Console.WriteLine(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Canceled:
                    OnOrderCanceled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartiallyAndCancelled:
                    Console.WriteLine("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Console.WriteLine(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.PendingCancel:
                    Console.WriteLine("mId:" + mId + "  " + "-------------------- Order Other ---------------------------");
                    Console.WriteLine(order.ToExcleString());
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
            req.IsBuy = !order.IsBuy;
            var buyPrice = mOrderbookB.Asks.First().Value.Price * 1.015m;
            var sellPrice = mOrderbookB.Bids.First().Value.Price * 0.985m;
            req.Price = req.IsBuy ? NormalizationMinUnit(buyPrice) : NormalizationMinUnit(sellPrice);
            req.IsMargin = true;
            req.OrderType = OrderType.Limit;
            req.MarketSymbol = mConfig.SymbolB;
            Console.WriteLine("mId:"+mId+"  "+"----------------------------ReverseOpenMarketOrder---------------------------");
            Console.WriteLine(order.ToString());
            Console.WriteLine(order.ToExcleString());
            var ticks = DateTime.Now.Ticks;
            try
            {
                var res = await mExchangeBAPI.PlaceOrderAsync(req);
                mRunningTask = Task.Delay(5 * 1000);
                await mRunningTask;
                Console.WriteLine(DateTime.Now.Ticks - ticks);
                Console.WriteLine(res.ToString());
                Console.WriteLine(res.OrderId);
            }
            catch (Exception ex)
            {
                Logger.Error(req.ToString());
                Logger.Error(ex);
                throw ex;
            }
            


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
                Console.WriteLine("mId:" + mId + "  " + "--------------- OpenPosition ------------------");
                Console.WriteLine("mId:" + mId + "  " + "{0},{1},{2},{3}", bidReq.Price, askPrice.Price, mOrderbookB.Bids.First().Value.Price, mOrderbookB.Asks.First().Value.Price);
                if (mBidOrder != null)
                {
                    Console.WriteLine("mId:" + mId + "  " + "Change price mBidOrder.OrderId:" + mBidOrder.OrderId);
                    Console.WriteLine("mId:" + mId + "  " + "mBidOrder.OrderId:" + mBidOrder.OrderId);
                    Console.WriteLine("mId:" + mId + "  " + mBidOrder.ToExcleString());
                }

                if (mAskOrder != null)
                {
                    Console.WriteLine("mId:" + mId + "  " + "Change price mAskOrder.OrderId:" + mAskOrder.OrderId);
                    Console.WriteLine("mId:" + mId + "  " + "mAskOrder.OrderId:" + mAskOrder.OrderId);
                    Console.WriteLine("mId:" + mId + "  " + mAskOrder.ToExcleString());
                }
                var orders = await mExchangeAAPI.PlaceOrdersAsync(requests);
                foreach (var o in orders)
                {
                    if (o.IsBuy)
                    {
                        if (mBidOrder == null)
                            mBidOrder = o;
                    }
                    else
                    {
                        if (mAskOrder == null)
                            mAskOrder = o;
                    }
                }
                if (mBidOrder != null && (mBidOrder.Result == ExchangeAPIOrderResult.Canceled || mBidOrder.Result == ExchangeAPIOrderResult.Error))
                {
                    OnOrderCanceled(mBidOrder);
                    Console.WriteLine("mId:" + mId + "  " + "mBidOrder OpenPosition  Canceled");
                }
                else
                {
                    Console.WriteLine("mId:" + mId + "orders.Length" + orders.Length + "  mBidOrder" + "--------------- OpenPosition OK ------------------");
                    Console.WriteLine(mBidOrder.ToExcleString());
                }
                if (mAskOrder != null && (mAskOrder.Result == ExchangeAPIOrderResult.Canceled || mAskOrder.Result == ExchangeAPIOrderResult.Error))
                {
                    OnOrderCanceled(mAskOrder);
                    Console.WriteLine("mId:" + mId + "  " + "mAskOrder OpenPosition  Canceled");
                }
                else
                {
                    Console.WriteLine("mId:" + mId + "  mAskOrder" + "--------------- OpenPosition OK ------------------");
                    Console.WriteLine(mAskOrder.ToExcleString());
                }
            }

        }
        /// <summary>
        /// 平仓
        /// </summary>
        async Task ClosePosition()
        {
            var slippage = 0.0001m;
            var priceBid = GetBidFirst(mOrderbookB).Price + GetStandardDev();
            var priceAsk = GetAskFirst(mOrderbookB).Price + GetStandardDev();
            priceBid = NormalizationMinUnit(priceBid * (1m - slippage));
            priceAsk = NormalizationMinUnit(priceAsk * (1m + slippage));
            var req = new ExchangeOrderRequest()
            {
                Amount = mFilledOrder.Amount,
                Price = mFilledOrder.IsBuy ? priceAsk : priceBid,
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
                Console.WriteLine("mId:"+mId+"  "+"--------------- ClosePosition ------------------");
                Console.WriteLine("mId:"+mId+"  "+"{0},{1},{2},{3}", priceBid, priceAsk, mOrderbookB.Bids.First().Value.Price, mOrderbookB.Asks.First().Value.Price);
                
                if (mCloseOrder != null)
                    Console.WriteLine(mCloseOrder.OrderId);
                var orders = await mExchangeAAPI.PlaceOrdersAsync(requests);
                foreach (var o in orders)
                {
                    mCloseOrder = o;
                    Console.WriteLine(o.ToExcleString());
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
            if (Math.Abs(priceDiff) < 0.3m && Math.Abs(amountDiff) < 0.2m)
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
            var amount = orderBook.Bids.Where(op => op.Key >= price).Select((op) => { return op.Value.Amount; }).Sum();
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
            var amount = orderBook.Asks.Where(op => op.Key <= price).Select((op) => { return op.Value.Amount; }).Sum();
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
            var bidFirst = GetBidFirst(orderBook);
            var fees = mConfig.Fees * bidFirst.Price;
            var price = bidFirst.Price * (1 - mConfig.MinIRS) - fees * 2 + GetStandardDev();
            var amount = bidFirst.Amount * mConfig.POR;
            price = mExchangeAAPI.PriceComplianceCheck(price);
            amount = mExchangeAAPI.AmountComplianceCheck(amount);
            amount = mExchangeBAPI.AmountComplianceCheck(amount);
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
            var askFirst = GetAskFirst(orderBook);
            var fees = mConfig.Fees * askFirst.Price;
            var price = askFirst.Price * (mConfig.MinIRS + 1) + fees * 2 + GetStandardDev();
            var amount = askFirst.Amount * mConfig.POR;
            price = mExchangeAAPI.PriceComplianceCheck(price);
            amount = mExchangeAAPI.AmountComplianceCheck(amount);
            amount = mExchangeBAPI.AmountComplianceCheck(amount);
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
            var t1 = smaA.Take(100);
            var t2 = smaB.Take(100);
            
            var dev = (t1.Sum()- t2.Sum())/100;
            return dev.ConvertInvariant<decimal>();
        }
        private IEnumerable<MarketCandle> marketCandleA;
        private IEnumerable<MarketCandle> marketCandleB;
        private double[] smaA;
        private double[] smaB;

        private decimal last = 0;

        private async Task GetExchangeCandles(int periodSeconds)
        {
            try
            {
                marketCandleA = await mExchangeAAPI.GetCandlesAsync(mConfig.SymbolA, periodSeconds, limit: 200);
                marketCandleB = await mExchangeBAPI.GetCandlesAsync(mConfig.SymbolB, periodSeconds, limit: 200);
                var closeA = marketCandleA.Select((candle, index) => { return candle.ClosePrice.ConvertInvariant<float>(); }).Reverse();
                var closeB = marketCandleB.Select((candle, index) => { return candle.ClosePrice.ConvertInvariant<float>(); }).Reverse();
                int outBegIdx;
                int outNBElement;
                smaA = new double[closeA.Count()];
                smaB = new double[closeB.Count()];
                TicTacTec.TA.Library.Core.Sma(0, closeA.Count() - 1, closeA.ToArray(), mConfig.TimePeriod, out outBegIdx, out outNBElement, smaA);
                TicTacTec.TA.Library.Core.Sma(0, closeB.Count() - 1, closeB.ToArray(), mConfig.TimePeriod, out outBegIdx, out outNBElement, smaB);
                decimal current = GetStandardDev();
                if (last!=0)
                {
                    
                    if(last> current)
                    {
                        Console.WriteLine("变小");
                    }
                }
                last = current;
                Console.WriteLine(GetStandardDev());

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            await Task.Delay(periodSeconds * 60000);
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
            Console.WriteLine("mId:"+mId+"  "+"---------------------Switch State to Open Position-----------------------------");
            isClosePositionState = false;
            mCloseOrder = null;
        }
        /// <summary>
        /// 切换到平仓状态
        /// </summary>
        async void SwitchStateToClosePosition()
        {
            Console.WriteLine("mId:"+mId+"  "+"---------------------Switch State to Close Position-----------------------------");
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

        public void Start()
        {
            Console.WriteLine("mId:" + mId + "  " + "Start {0},{1},{2},{3}", 1m, 2m, 3m, 4m);
            mExchangeAAPI.LoadAPIKeys(ExchangeName.BitMEX);
            mExchangeBAPI.LoadAPIKeys(ExchangeName.HBDM);
            WhileGetExchangeCandles();
            mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 25, mConfig.SymbolA);
            mExchangeBAPI.GetFullOrderBookWebSocket(OnOrderbookBHandler, 25, mConfig.SymbolB);
            mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);



            //testc();

            //             mExchangeBAPI.PlaceOrderAsync(new ExchangeOrderResult
            //             {
            //                 Amount = 100,
            //                 AmountFilled = 0,
            //                 Price = 3888,
            //                 IsBuy = false,
            //                 MarketSymbol = mConfig.SymbolB,
            //             });
        }
        public async Task testc()
        {
            //             //蜡烛线
            //             await mExchangeBAPI.GetCandlesAsync(mConfig.SymbolB, 60, null, null, 150);
            //             //获取订单
            //             IExchangeAPI mExchangeCAPI = ExchangeAPI.GetExchangeAPI(ExchangeName.GateioDM);
            //             mExchangeCAPI.LoadAPIKeys(ExchangeName.GateioDM);
            // 
            ExchangeOrderRequest req = new ExchangeOrderRequest()
            {
                Amount = 100,
                Price = 3888,
                IsBuy = false,
                MarketSymbol = mConfig.SymbolB,
                OrderType = OrderType.Limit,
            };
            try
            {
                ExchangeOrderResult re = await mExchangeBAPI.PlaceOrderAsync(req);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw ex;
            }
            
            //ExchangeOrderResult re = await mExchangeCAPI.PlaceOrderAsync(req);
            //long lastTime = DateTime.Now.Ticks;
            //Console.WriteLine(new DateTime( lastTime).);

            //1 买入orderId1
            //var bidReq1 = new ExchangeOrderRequest()
            //{
            //    Amount = 100,
            //    Price = 4000,
            //    MarketSymbol = mConfig.SymbolA,
            //    IsBuy = false,
            //    ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
            //};
            //var orders1 = await mExchangeAAPI.PlaceOrdersAsync(bidReq1);
            ////2 cancle roderId1
            //await mExchangeAAPI.CancelOrderAsync(orders1[0].OrderId, orders1[0].MarketSymbol);
            ////3 change orderId1 prices
            //var bidReq3 = new ExchangeOrderRequest()
            //{
            //    Amount = 100,
            //    Price = 4002,
            //    MarketSymbol = mConfig.SymbolA,
            //    IsBuy = false,
            //    ExtraParameters = { { "execInst", "ParticipateDoNotInitiate" } }
            //};
            //bidReq3.ExtraParameters.Add("orderID", orders1[0].OrderId);
            //var orders3 = await mExchangeAAPI.PlaceOrdersAsync(bidReq3);
            //await Task.Delay(100);

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
        /// C交易所币种的的Symbol
        /// </summary>
        public string SymbolC;
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

