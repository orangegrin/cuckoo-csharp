using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp;
using System.Threading;
using cuckoo_csharp.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Globalization;

namespace cuckoo_csharp.Strategy.Arbitrage
{
    /// <summary>
    ///  按照比例购买现货
    /// </summary>
    public class BuyCoinStrategy
    {
        private IExchangeAPI mExchangeAAPI;
        private IWebSocket mOrderBookAws;
        private ExchangeOrderBook mOrderBookA;

        private int mOrderBookAwsCounter = 0;
        private int mId;
        /// <summary>
        /// 补漏数量
        /// </summary>
        private decimal mFreeCoinAmount = 0;

        private Options mData { get; set; }
        /// <summary>
        /// 当前开仓数量
        /// </summary>
        private decimal mCurAAmount
        {
            get
            {
                return mData.CurAAmount;
            }
            set
            {
                mData.CurAAmount = value;
                mData.SaveToDB(mDBKey);
            }
        }
        private string mDBKey;
        private Task mRunningTask;
        private bool mExchangePending = false;
        private bool mOrderBookAwsConnect = false;
        private bool mOnCheck = false;
        private bool mOnTrade = false;//是否在交易中
        private bool mOnConnecting = false;
        private bool mBuyAState;

        public BuyCoinStrategy(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("BuyCoinStrategy:CONFIG:{0}:{1}:{2}", config.ExchangeNameA,config.SymbolA, id);
            RedisDB.Init(config.RedisConfig);
            mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null)
            {
                mData = config;
                mData.mCurOpenOrder = new OrderPair(null, null);
                mData.mOpenProfitOrders = new List<ExchangeOrderResult>();
                config.SaveToDB(mDBKey);
            }
            else
            {
                if (mData.mCurOpenOrder==null)
                {
                    mData.mCurOpenOrder = new OrderPair(null, null);
                }
            }
         
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mData.ExchangeNameA);
        }
        public void Start()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
            mExchangeAAPI.LoadAPIKeys(mData.EncryptedFileA);
            mExchangeAAPI.SubAccount = mData.SubAccount;

            //_______________________________test_____________________________________
            //             var t = mExchangeAAPI.GetWalletSummaryAsync("USD");
            //             Task.WaitAll(t);
            //             var amount =(t.Result);
            // 
            //             Logger.Debug(amount.ToString());
            //_______________________________test_____________________________________



            //UpdateAvgDiffAsync();
            SubWebSocket();
            WebSocketProtect();
            //CheckPosition();
            //ChangeMaxCount();
            CheckOrderBook();
        }
        #region Connect
        private void SubWebSocket(ConnectState state = ConnectState.All)
        {
            mOnConnecting = true;
            Thread.Sleep(3 * 1000);
            if (state.HasFlag(ConnectState.A))
            {
                mOrderBookAws = mExchangeAAPI.GetFullOrderBookWebSocket(OnOrderbookAHandler, 20, mData.SymbolA);
                mOrderBookAws.Connected += async (socket) => { mOrderBookAwsConnect = true; Logger.Debug("GetFullOrderBookWebSocket A 连接"); OnConnect(); };
                mOrderBookAws.Disconnected += async (socket) =>
                {
                    mOrderBookAwsConnect = false;
                    WSDisConnectAsync("GetFullOrderBookWebSocket A 连接断开");
                };
            }
           
        }
        /// <summary>
        /// WS 守护线程
        /// </summary>
        private async void WebSocketProtect()
        {
            while (true)
            {
                if (!OnConnect())
                {
                    await Task.Delay(5 * 1000);
                    continue;
                }
                int delayTime = 60;//保证次数至少要3s一次，否则重启
                mOrderBookAwsCounter = 0;
                await Task.Delay(1000 * delayTime);
                Logger.Debug(Utils.Str2Json("mOrderBookAwsCounter", mOrderBookAwsCounter, "mOrderBookBwsCounter"));
                bool detailConnect = true;
                if (mOrderBookAwsCounter < 1 || (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    await ReconnectWS();
                }
            }
        }

        private async Task ReconnectWS(ConnectState state = ConnectState.All)
        {
//             if (mCurOrderA != null)
//             {
//                 await CancelCurOrderA();
//             }
            await CloseWS(state);
            Logger.Debug("开始重新连接ws");
            SubWebSocket(state);
            await Task.Delay(5 * 1000);
        }

        private async Task CloseWS(ConnectState state = ConnectState.All)
        {
            await Task.Delay(5 * 1000);
            Logger.Debug("销毁ws");
            if (state.HasFlag(ConnectState.A))
            {
                mOrderBookAwsConnect = false;
                mOrderBookAws.Dispose();
            }
        }

        private bool OnConnect()
        {
            bool connect =  mOrderBookAwsConnect;
            if (connect)
                mOnConnecting = false;
            return connect;
        }
        
        private async Task WSDisConnectAsync(string tag)
        {
//             if (mCurOrderA != null)
//             {
//                 await CancelCurOrderA();
//             }
            //删除避免重复 重连
            await Task.Delay(40 * 1000);
            Logger.Error(tag + " 连接断开");
            if (OnConnect() == false)
            {
                if (!mOnConnecting)//如果当前正在连接中那么不连接否则开始重连
                {
                    await CloseWS();
                    SubWebSocket();
                }
            }
        }
        /// <summary>
        /// 检查仓位是否对齐
        /// 在非开仓阶段检测，避免A成交B成交中的情况
        /// </summary>
        private async void CheckPosition()
        {
            while (true)
            {
                /*
                if (!OnConnect())
                {
                    await Task.Delay(5 * 1000);
                    continue;
                }
                if (mData.mCurOpenOrder.OpenOrder != null || mOnTrade || mExchangePending == true)//交易正在进行或者，准备开单。检查数量会出现问题
                {
                    await Task.Delay(200);
                    continue;
                }
                */
                mOnCheck = true;
                Logger.Debug("-----------------------CheckPosition-----------------------------------");
                                
                ExchangeMarginPositionResult posA ;
                try
                {
                    //等待10秒 避免ws虽然推送数据刷新，但是rest 还没有刷新数据
                    await Task.Delay(10* 1000);
                    posA = new ExchangeMarginPositionResult();
                    var v = mData.SymbolA.Split("/")[0];
                    posA.Amount = await mExchangeAAPI.GetWalletSummaryAsync(v);
                    
                    if (posA==null )
                    {
                        mOnCheck = false;
                        await Task.Delay(5 * 60 * 1000);
                        continue;
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.Error(Utils.Str2Json("GetOpenPositionAsync ex", ex.ToString()));
                    mOnCheck = false;
                    await Task.Delay(1000);
                    continue;
                }

                try
                {
                   
                }
                catch (System.Exception ex)
                {
                    if (ex.ToString().Contains("Rate limit exceeded") || ex.ToString().Contains("Please try again later") || ex.ToString().Contains("Do not send more than") || ex.ToString().Contains("Try again") || ex.ToString().Contains("Please retry request") || ex.ToString().ToLower().Contains("try again") || ex.ToString().Contains("Not logged in"))
                    {
                        await Task.Delay(2000);
                    }
                    else
                    {
                        Logger.Error(Utils.Str2Json("CheckPosition ex", ex.ToString()));
                        throw ex;
                    }
                }
                mOnCheck = false;
                await Task.Delay(5 * 60 * 1000);
            }
        }
#endregion
        private void OnProcessExit(object sender, EventArgs e)
        {
            Logger.Debug("------------------------ OnProcessExit ---------------------------");
            mExchangePending = true;
            
//             if (mCurOrderA != null)
//             {
//                 CancelCurOrderA();
//                 Thread.Sleep(5 * 1000);
//             }
        }
        /// <summary>
        /// 获取交易所某个币种的数量
        /// </summary>
        /// <param name="exchange"></param>
        /// <param name="symbol"></param>
        /// <returns></returns>
        public async Task<decimal> GetAmountsAvailableToTradeAsync(IExchangeAPI exchange, string symbol)
        {
            var amount = await exchange.GetWalletSummaryAsync(symbol);
            return amount;
        }
        /// <summary>
        /// 根据当前btc数量修改最大购买数量
        /// </summary>
        private async void ChangeMaxCount()
        {
            while (true)
            {
                if (!OnConnect())
                {
                    await Task.Delay(5 * 1000);
                    continue;
                }
                await CountDiffGridMaxCount();
                await Task.Delay(2*3600 * 1000);
            }
        }
        private async Task CountDiffGridMaxCount()
        {
            if (mData.AutoCalcMaxPosition)
            {
                try
                {
//                     decimal noUseBtc = await GetAmountsAvailableToTradeAsync(mExchangeAAPI, "");
//                     decimal allCoin = noUseBtc;
//                     decimal avgPrice = mOrderBookA.Bids.FirstOrDefault().Value.Price ;
//                     decimal mAllPosition = allCoin * mData.Leverage / avgPrice/2;//单位eth个数
//                     mData.PerTrans = Math.Round(mData.PerBuyUSD / avgPrice /mData.MinAmountA) * mData.MinAmountA;
//                     mData.ClosePerTrans = Math.Round(mData.ClosePerBuyUSD / avgPrice / mData.MinAmountA) * mData.MinAmountA;
//                     decimal lastPosition = 0;
//                    
//                     mData.SaveToDB(mDBKey);
//                     Logger.Debug(Utils.Str2Json("noUseBtc", noUseBtc, "allPosition", mAllPosition));
                }
                catch (System.Exception ex)
                {
                    Logger.Error("ChangeMaxCount ex" + ex.ToString());
                }
            }
        }
        private void OnOrderbookAHandler(ExchangeOrderBook order)
        {
            mOrderBookAwsCounter++;
//             if (mOrderBookA == null)
//             {
//                 mOrderBookA = CLone(order);
//             }
            mOrderBookA = order;
            OnOrderBookHandler();
        }

        async void OnOrderBookHandler()
        {
            if (Precondition())
            {
                mExchangePending = true;
                Options temp = Options.LoadFromDB<Options>(mDBKey);
                Options last_mData = mData;
                if (mData.mCurOpenOrder==null || mData.mCurOpenOrder.OpenOrder==null)//避免多线程读写错误
                    mData = temp;
                else
                {
                    temp.CurAAmount = mData.CurAAmount;
                    mData = temp;
                }
//                 if (last_mData.OpenPositionBuyA != mData.OpenPositionBuyA || last_mData.OpenPositionSellA != mData.OpenPositionSellA)//仓位修改立即刷新
//                     CountDiffGridMaxCount();
                await Execute();
                await Task.Delay(mData.IntervalMillisecond);
                mExchangePending = false;
            }
        }
        private bool Precondition()
        {
            if (!OnConnect())
                return false;
            if (mOrderBookA == null)
                return false;
            if (mRunningTask != null)
                return false;
            if (mExchangePending)
                return false;
            if (mOnCheck)
                return false;
            if (mOrderBookA.Asks.Count == 0 || mOrderBookA.Bids.Count == 0 )
                return false;
            return true;
        }
        /// <summary>
        /// 添加开仓订单
        /// </summary>
        /// <param name="buyPriceA"></param>
        /// <param name="openOrder"></param>
        /// <returns></returns>
        private async Task<ExchangeOrderResult> AddOpenOrder(decimal buyPriceA,decimal bidPrice)
        {
            
            decimal amount = NormalizationMinAmountUnit(((mData.StartMoney -mData.UseMoney)/ bidPrice) * mData.OpenPositionRate);
            if (amount < mData.MinOrderAmount)
            {
                Logger.Debug("小于最小开仓数量不能开仓");
                return null;
            }// 340*(1-0.003)
            decimal price = NormalizationMinPriceUnit(buyPriceA * (1 - mData.OpenRate));
            Logger.Debug($"AddOpenOrder amount {amount } price{price} mData.StartMone{mData.StartMoney} mData.UseMoney {mData.UseMoney} buyPriceA {buyPriceA} bidPrice {bidPrice}");
            ExchangeOrderResult openOrder = await AddOrder(true, mData.SymbolA, price, amount);
            if (openOrder == null)
                return null;
            mData.mCurOpenOrder.OpenOrder = openOrder;
            return openOrder;
        }
        /// <summary>
        /// 用于检测计算 使用的币数量
        /// </summary>
        /// <returns></returns>
        private async Task CheckFreeCoinAmount()
        {
            decimal freeCoinAmount = 0;
            try
            {
                freeCoinAmount = (await mExchangeAAPI.GetAmountsAvailableToTradeAsync())[mData.BalanceSymbol];
            }
            catch (Exception ex)
            {

                Logger.Debug("GetAmountsAvailableToTradeAsync error " + ex.ToString());
                return;
            }
            decimal diff = freeCoinAmount - mData.BaseAmont;

            if (diff>= mData.MinAmountUnit)
            {
                mFreeCoinAmount = diff;
                Logger.Debug($"CheckFreeCoinAmount 发现误差：{diff} ");
            }
        }

        private async Task< ExchangeOrderResult> AddFirstOpenOrder(ExchangeOrderResult openOrder, decimal buyPriceA)
        {
            openOrder = await AddOpenOrder(buyPriceA, buyPriceA);
            if (openOrder != null)
            {
                mData.StartPrice = buyPriceA;
            }
            return openOrder;
        }

        private async Task Execute()
        {
            bool hadChange = false;
            //是否完全完成过交易
            bool hadClean = false;
            decimal buyPriceA;
            decimal sellPriceA;
            decimal bidAAmount, askAAmount, bidBAmount, askBAmount;
            decimal buyAmount = mData.StartMoney;
            lock (mOrderBookA)
            {
                if (Precondition())
                    return;
                buyPriceA = mOrderBookA.Bids.FirstOrDefault().Value.Price;
                sellPriceA = mOrderBookA.Asks.FirstOrDefault().Value.Price;
                bidAAmount = mOrderBookA.Bids.FirstOrDefault().Value.Amount;
                askAAmount = mOrderBookA.Asks.FirstOrDefault().Value.Amount;
            }

            //有可能orderbook bids或者 asks没有改变
            //return;
            if (buyPriceA != 0 && sellPriceA != 0  && buyAmount != 0)
            {
                PrintInfo(buyPriceA, sellPriceA, buyAmount, bidAAmount, askAAmount);
                //如果盘口差价超过4usdt 不进行挂单，但是可以改单（bitmex overload 推送ws不及时）
                if ( ((sellPriceA <= buyPriceA) || (sellPriceA - buyPriceA >= 10)))
                {
                    Logger.Debug("范围更新不及时，不纳入计算");
                    return;
                }
                //return;
                //1检查仓位
                decimal amount=0;
                try
                {
                    amount = await mExchangeAAPI.GetWalletSummaryAsync(mData.BalanceSymbol);
                }
                catch (Exception ex)
                {

                    Logger.Debug("GetWalletSummaryAsync error " + ex.ToString());
                    return;
                }
                if (amount < mData.BaseAmont)//大于
                {
                    amount = 0;
                }
                var newUseMoney = amount*buyPriceA;
                if (Math.Abs( mData.UseMoney- newUseMoney)>1)
                {
                    mData.UseMoney = newUseMoney;
                    mData.SaveToDB(mDBKey);
                }
                Logger.Debug($"实际开仓数量 mFreeCoinAmount {mFreeCoinAmount } coinUSDValue {amount } newUseMoney {newUseMoney} mData.LastFillPrice {mData.LastFillPrice}");
                //2没有仓位限价bid1开仓
                var openOrder = mData.mCurOpenOrder.OpenOrder;
                if (amount == 0 || mData.mOpenProfitOrders.Count ==0)
                {
                    if (mData.mCurOpenOrder.OpenOrder==null)
                    {
                        Logger.Debug("首次添加订单");
                        openOrder = await AddFirstOpenOrder(openOrder, buyPriceA);
                        hadChange = true;
                    }
                    else if(mData.mCurOpenOrder.ProfitOrder==null)//首单开仓，没有止盈单的情况下，价格上涨改单，价格下跌不改单
                    {
                        if (mData.StartPrice< buyPriceA)
                        {


                            Logger.Debug("首次添加订单价格上涨取消再添加");
                            await CancelCurOrderA(openOrder.OrderId);
                            openOrder = await AddFirstOpenOrder(openOrder, buyPriceA);
                            hadChange = true;
                        }
                    }

                }
                else if (amount >= 0)
                {
                    if (mData.mCurOpenOrder.OpenOrder == null)
                    {
                        openOrder = await AddOpenOrder(mData.LastFillPrice,buyPriceA);
                        hadChange = true;
                    }
                }
                //3 开始检查 订单是否有成交
                List<ExchangeOrderResult> openList = null;
                List<ExchangeOrderResult> fillList = null;
                try
                {
                    openList = (await mExchangeAAPI.GetOpenOrderDetailsAsync(mData.SymbolA)).ToList();
                    fillList = (await mExchangeAAPI.GetCompletedOrderDetailsAsync(mData.SymbolA)).ToList();
                }
                catch (Exception ex)
                {

                    Logger.Debug("GetOrder error " + ex.ToString());
                    return;
                }
                
                Dictionary<string, ExchangeOrderResult> openDic = new Dictionary<string, ExchangeOrderResult>();
                Dictionary<string, ExchangeOrderResult> fillDic = new Dictionary<string, ExchangeOrderResult>();
                foreach (var item in openList)
                {
                    openDic[item.OrderId]= item;
                }
                foreach (var item in fillList)
                {
                    fillDic[item.OrderId]= item;
                }
                Logger.Debug($"Execute 1 ");
                // 检查开仓单
                if (openOrder != null)
                {
                    if (mData.mCurOpenOrder.OpenOrder.Result == ExchangeAPIOrderResult.Canceled)//有可能限价提交价格高于bid1，被取消。重新挂单
                    {
                        mData.mCurOpenOrder.Clean();
                        mData.SaveToDB(mDBKey);
                        return;
                    }
                    Logger.Debug($"Execute 2 ");
                    bool hadOpen = openDic.TryGetValue(openOrder.OrderId,out ExchangeOrderResult newOpenOrder);
                    bool hadFill = fillDic.TryGetValue(openOrder.OrderId, out ExchangeOrderResult newFillOrder);
                    //部分成交的开仓单对应的止盈订单，完全成交了

                    if (hadOpen)
                    {
                        Logger.Debug($"Execute 3 ");
                        if (newOpenOrder.AmountFilled!=openOrder.AmountFilled || (newOpenOrder.AmountFilled>0 && mData.mCurOpenOrder.ProfitOrder == null))//如果开仓订单部分成交数量改变，重置止盈订单
                        {
                            mData.mCurOpenOrder.OpenOrder = newOpenOrder;
                            openOrder = mData.mCurOpenOrder.OpenOrder;
                            Logger.Debug($"openOrder 部分成交 openOrder.AmountFilled {openOrder.AmountFilled }");
                            if (mData.mCurOpenOrder.ProfitOrder!=null)//删除止盈订单，重新开
                            {
                                await CancelCurOrderA(mData.mCurOpenOrder.ProfitOrder.OrderId);
                                foreach (var item in mData.mOpenProfitOrders)
                                {
                                    if (item.OrderId == mData.mCurOpenOrder.ProfitOrder.OrderId)
                                    {
                                        mData.mOpenProfitOrders.Remove(item);
                                        break;
                                    }
                                } 
                            }
                            mData.mCurOpenOrder.ProfitOrder = await AddProfitOrder(openOrder);//币数量不正确导致的，出问题
                            if (mData.mCurOpenOrder.ProfitOrder!=null)
                            {
                                mData.mOpenProfitOrders.Add(mData.mCurOpenOrder.ProfitOrder);
                                hadChange = true;
                            }
                            
                        }
                    }

                    if (hadOpen == false && hadFill == false)//成交后过期了，通过读取状态查找
                    {
                        var order = await mExchangeAAPI.GetOrderDetailsAsync(openOrder.OrderId, mData.SymbolA);
                        if (order.Result == ExchangeAPIOrderResult.Filled)
                        {
                            newFillOrder = order;
                            hadFill = true;
                        }
                    }
                    if (hadFill)
                    {
                        Logger.Debug($"Execute 4 ");
                        if (newFillOrder.AmountFilled != openOrder.AmountFilled || mData.mCurOpenOrder.ProfitOrder == null)//如果开仓订单全部成交，重置止盈订单
                        {
                            mData.mCurOpenOrder.OpenOrder = newFillOrder;
                            openOrder = mData.mCurOpenOrder.OpenOrder;
                            Logger.Debug($"openOrder 完全成交 openOrder.AmountFilled {openOrder.AmountFilled }");
                            if (mData.mCurOpenOrder.ProfitOrder != null)//删除止盈订单，重新开
                            {
                                await CancelCurOrderA(mData.mCurOpenOrder.ProfitOrder.OrderId);
                                foreach (var item in mData.mOpenProfitOrders)
                                {
                                    if (item.OrderId == mData.mCurOpenOrder.ProfitOrder.OrderId)
                                    {
                                        mData.mOpenProfitOrders.Remove(item);
                                        break;
                                    }
                                }
                            }
                            mData.mCurOpenOrder.ProfitOrder = await AddProfitOrder(openOrder,mFreeCoinAmount);//完全成交的时候添加，然后置零
                            mFreeCoinAmount = 0;
                            if (mData.mCurOpenOrder.ProfitOrder!=null)
                            {
                                mData.mOpenProfitOrders.Add(mData.mCurOpenOrder.ProfitOrder);
                                hadChange = true;
                            }
                        }
                    }
                   

                    if (mData.mCurOpenOrder.ProfitOrder!=null && mData.mCurOpenOrder.ProfitOrder.Amount >= mData.mCurOpenOrder.OpenOrder.Amount)//说明全部成交，更新成交价,刷新止盈订单，清理订单数据,止盈单数量大于说明挂了容错订单
                    {
                        Logger.Debug($"Execute 5 ");
                        var oldUse = mData.UseMoney;
                        mData.UseMoney += mData.mCurOpenOrder.OpenOrder.AveragePrice * mData.mCurOpenOrder.OpenOrder.AmountFilled;
                        mData.LastFillPrice = mData.mCurOpenOrder.OpenOrder.AveragePrice;
                        //mData.mOpenProfitOrders.Add(mData.mCurOpenOrder.ProfitOrder); 前面已经添加过了
                        mData.mCurOpenOrder.Clean();
                        hadClean = true;
                        hadChange = true;
                        Logger.Debug($"OpenOrder 完全成交  ProfitOrder 已经挂完毕 oldUse {oldUse } mData.UseMoney {mData.UseMoney } mData.LastFillPrice {mData.LastFillPrice } ");
                    }

                    //如果 开仓订单全部成交，且止盈订单也已经挂上去，添加止盈订单到止盈订单队列，准备下轮新的开仓
                }
                //4检查止盈订单成交情况
                Logger.Debug($"Execute 6 ");
                //没有找到的已经成交的止盈订单，挂单时间过久，ftx的获取订单历史按照创建订单时间来排序的，成交完但是挂单早的订单就看不到了，只能根据订单id查找对应状态
                if (mData.mOpenProfitOrders.Count>0)
                {
                    //删除空订单，由于可平仓数量不够导致的空订单
                    for (int i = mData.mOpenProfitOrders.Count - 1; i >= 0; i--)
                    {
                        var item = mData.mOpenProfitOrders[i];
                        if (item ==null)
                        {
                            mData.mOpenProfitOrders.RemoveAt(i);
                            Logger.Debug("有空订单");
                            break;
                        }
                    }
                    //循环止盈订单，如果止盈订单中在open和fill中都找不到，那么在订单状态中查找；
                    bool restOpen = false;
                    Logger.Debug($"止盈订单数量 mData.mOpenProfitOrders.Count {mData.mOpenProfitOrders.Count }  ");
                    List<ExchangeOrderResult> removeList = new List<ExchangeOrderResult>();
                    for (int i = mData.mOpenProfitOrders.Count-1; i >=0; i--)
                    {
                        var item = mData.mOpenProfitOrders[i];
                        var lastProfit = item;
                        bool hadFillProfit = fillDic.TryGetValue(lastProfit.OrderId, out ExchangeOrderResult newFillProfitOrder);
                        if (!hadFillProfit)//不在成交订单中
                        {
                            bool hadOpen = openDic.TryGetValue(lastProfit.OrderId, out ExchangeOrderResult newOpenProfitOrder);
                            if (!hadOpen)//也不在打开订单中，只能通过订单状态读取
                            {
                                lastProfit = await mExchangeAAPI.GetOrderDetailsAsync(lastProfit.OrderId, mData.SymbolA);
                                newFillProfitOrder = lastProfit;
                                if (lastProfit.Result == ExchangeAPIOrderResult.Filled)
                                {
                                    hadFillProfit = true;
                                }
                                Logger.Debug("止盈订单不在打开和成交订单中在订单状态中获取:" + lastProfit.ToString());
                                if (lastProfit.Result == ExchangeAPIOrderResult.Canceled)
                                {
                                    mData.mOpenProfitOrders.RemoveAt(i);
                                    hadChange = true;
                                    Logger.Debug("止盈订单被取消，不知道为什么:" + lastProfit.ToString());
                                    break;
                                }
                                
                            }
                        }

                        if (hadFillProfit)
                        {
                            Logger.Debug($"止盈订单数量 mData.mOpenProfitOrders.Count {mData.mOpenProfitOrders.Count }  最近止盈订单 id {lastProfit.OrderId}");
                            mData.UseMoney -= newFillProfitOrder.AveragePrice * newFillProfitOrder.Amount;
                            mData.LastFillPrice = newFillProfitOrder.AveragePrice;
                            removeList.Add(newFillProfitOrder);
                            //mData.mOpenProfitOrders.Remove(newFillProfitOrder);
                            hadChange = true;
                            restOpen = true;
                            Logger.Debug($"止盈订单完全成交 mData.UseMoney {mData.UseMoney}  mData.LastFillPriced {mData.LastFillPrice}");
                        }
                    }
                    //删除成交的订单
                    foreach (var item in removeList)
                    {
                        foreach (var profit in mData.mOpenProfitOrders)
                        {
                            if (profit.OrderId == item.OrderId)
                            {
                                mData.mOpenProfitOrders.Remove(profit);
                                break;
                            }
                            
                        }
                        
                    }
                    if (restOpen)//止盈订单成交，重置开仓订单价格数量，
                    {
                        if (mData.mCurOpenOrder.OpenOrder!=null)//删除当前开仓订单
                        {
                            await CancelCurOrderA(mData.mCurOpenOrder.OpenOrder.OrderId);
                        }
                        if (mData.mCurOpenOrder.ProfitOrder!=null)//如果开仓订单部分成交，导致有止盈订单，把止盈订单加入止盈队列
                        {
                            mData.mOpenProfitOrders.Add(mData.mCurOpenOrder.ProfitOrder);
                        }
                        mData.mCurOpenOrder.Clean();
                        hadChange = true;
                    }
                }
                if (hadClean)//完全完成交易后才能检测 空币数量
                {
                    await CheckFreeCoinAmount();
                }

                if (hadChange)//数据改变，修改数据库
                {
                    mData.SaveToDB(mDBKey);
                }

                if (mRunningTask != null)
                {
                    try
                    {
                        await mRunningTask;
                        mRunningTask = null;
                    }
                    catch (System.Exception ex)
                    {
                        Logger.Error(Utils.Str2Json("mRunningTask ex", ex));
                        if (ex.ToString().Contains("Invalid orderID") || ex.ToString().Contains("Not Found") || ex.ToString().Contains("Order already closed"))
                            mData.mCurOpenOrder.Clean();
                        mRunningTask = null;
                    }
                }
            }
        }
        private async Task CheckOrderBook()
        {
            ExchangeOrderPrice? lastA =null ;
            ExchangeOrderPrice? curA;
            while (true)
            {
                await Task.Delay(30 * 1000);
                if (mOrderBookA==null || !OnConnect() )
                    continue;
                else
                {
                    try
                    {
                        if (lastA == null)
                        {
                            lock (mOrderBookA)
                            {
                                lastA = mOrderBookA.Asks.FirstOrDefault().Value;
                            }
                            await Task.Delay(15 * 1000);
                        }


                        lock (mOrderBookA)
                        {
                            curA = mOrderBookA.Asks.FirstOrDefault().Value;
                        }

                        ConnectState? state = null;
                        Logger.Debug($"lastA.Value.Price {lastA.Value.Price} curA.Value.Price{curA.Value.Price}");
                        if ((lastA.Value.Price == curA.Value.Price && lastA.Value.Amount == curA.Value.Amount))
                        {
                            state = ConnectState.A;

                        }

                        if (state != null)
                        {
                            Logger.Error("CheckOrderBook  orderbook 错误");
                            await ReconnectWS(state.Value);
                        }
                        lastA = curA;
                    }
                    catch (Exception ex)
                    {

                        Logger.Error("CheckOrderBook " + ex.ToString());
                    }
                    
                }
               
            }
        }
        /// <summary>
        /// 刷新差价
        /// </summary>
        public async Task UpdateAvgDiffAsync()
        {
            string dataUrl = "";//$"{"http://150.109.52.225:8006/arbitrage/process?programID="}{mId}{"&symbol="}{mData.Symbol}{"&exchangeB="}{mData.ExchangeNameB.ToLowerInvariant()}{"&exchangeA="}{mData.ExchangeNameA.ToLowerInvariant()}";
            dataUrl = "http://150.109.52.225:8006/arbitrage/process?programID=" + mId + "&symbol=BTCZH&exchangeB=bitmex&exchangeA=bitmex";
            Logger.Debug(dataUrl);
            //decimal lastLeverage = mData.Leverage;
            while (true)
            {
                try
                {
                    JObject jsonResult = await Utils.GetHttpReponseAsync(dataUrl);
                  
                    //mData.Leverage = jsonResult["leverage"].ConvertInvariant<decimal>();
//                     mData.OpenPositionBuyA = jsonResult["openPositionBuyA"].ConvertInvariant<int>() == 0 ? false : true;
//                     mData.OpenPositionSellA = jsonResult["openPositionSellA"].ConvertInvariant<int>() == 0 ? false : true;
                    var rangeList = JArray.Parse(jsonResult["profitRange"].ToStringInvariant());
                    decimal avgDiff = jsonResult["maAvg"].ConvertInvariant<decimal>();
//                     if (mData.AutoCalcProfitRange)
//                         avgDiff = jsonResult["maAvg"].ConvertInvariant<decimal>();

                    avgDiff = Math.Round(avgDiff, 4);//强行转换
                    string remark = jsonResult["remark"].ToString().ToLower();
                    //mData.OpenProcess = !remark.Contains("close");

                    if (rangeList.Count%2==0)
                    {
                        for (int i = 0; i < rangeList.Count / 2; i++)
                        {
                            int mid = rangeList.Count / 2;
                           
                            mData.SaveToDB(mDBKey);
                        }
                         CountDiffGridMaxCount();
                    }
                   
                    Logger.Debug("SetDiffBuyMA:" + avgDiff);
//                     if (lastLeverage != lastLeverage || lastOpenPositionBuyA != mData.OpenPositionBuyA || lastOpenPositionSellA != mData.OpenPositionSellA)// 仓位修改立即刷新
//                     {
//                         CountDiffGridMaxCount();
//                     }
                    Logger.Debug(Utils.Str2Json(" UpdateAvgDiffAsync avgDiff", avgDiff));
                }
                catch (Exception ex)
                {
                    Logger.Debug(" UpdateAvgDiffAsync avgDiff:" + ex.ToString());
                }
                
                await Task.Delay(60 * 1000);
            }
        }

        private void PrintInfo(decimal bidA, decimal askA,   decimal buyAmount, 
            decimal bidAAmount, decimal askAAmount )
        {
            Logger.Debug("================================================");
            Logger.Debug(Utils.Str2Json("Bid A", bidA, "bidAAmount", bidAAmount, "bidBAmount"));
            Logger.Debug(Utils.Str2Json("Ask A", askA, "askAAmount", askAAmount, "askBAmount"));
            Logger.Debug(Utils.Str2Json("mCurAmount", mCurAAmount, " buyAmount",  buyAmount ));
        }

        private async Task<ExchangeOrderResult> AddOrder(bool isBuy, string symbol, decimal buyPrice, decimal buyAmount)
        {
            //A限价买
            ExchangeOrderRequest requestA = new ExchangeOrderRequest()
            {
                ExtraParameters = { { "postOnly", false } }
            };
            requestA.Amount = buyAmount;
            requestA.MarketSymbol = symbol;
            requestA.IsBuy = isBuy;
            requestA.OrderType = OrderType.Limit;
            requestA.Price = NormalizationMinPriceUnit(buyPrice);
            bool isAddNew = true;
            try
            {
                var v = await mExchangeAAPI.PlaceOrderAsync(requestA);
                Logger.Debug(Utils.Str2Json("requestA", requestA.ToString()));
                Logger.Debug(Utils.Str2Json("Add mCurrentLimitOrder", v.ToExcleString(), "CurAmount", mData.CurAAmount));
                if (v.Result == ExchangeAPIOrderResult.Canceled)
                {
                    mOnTrade = false;
                    await Task.Delay(2000);
                    return null;
                }
                await Task.Delay(100);
                return v;
            }
            catch (Exception ex)
            {
                Logger.Error(Utils.Str2Json("ex", ex));
                //如果是添加新单那么设置为null 
                if (isAddNew || ex.ToString().Contains("Order already closed") || ex.ToString().Contains("Not Found"))
                {
                    mOnTrade = false;
                }
                if (ex.ToString().Contains("405 Method Not Allowed"))//删除订单
                {
                    mOnTrade = false;
                }
                if (ex.ToString().Contains("Not enough balances"))
                {
                    throw ex;
                }
                Logger.Error(Utils.Str2Json("ex", ex));
                if (ex.ToString().Contains("Rate limit exceeded") || mExchangeAAPI.ErrorTradingSyatemIsBusy(ex))
                    await Task.Delay(5000);
                if (ex.ToString().Contains("RateLimitError"))
                    await Task.Delay(30000);

            }
            return null;

        }
        private async Task CancelCurOrderA(string orderID)
        {

            //在onOrderCancle的时候处理
            string orderIDA = orderID;
            try
            {
                Logger.Debug(" CancelCurOrderA " + orderID);
                await mExchangeAAPI.CancelOrderAsync(orderID, mData.SymbolA);
                //|| ex.ToString().Contains("Order already closed")
            }
            catch (Exception ex)
            {
                Logger.Debug("CancelCurOrderA ::"+ex.ToString());
                if (ex.ToString().Contains("CancelOrderEx") || ex.ToString().Contains("Not logged in") )
                {
                    await Task.Delay(5000);
                    await mExchangeAAPI.CancelOrderAsync(orderID, mData.SymbolA);
                }
            }
        }
        private async Task CancelCurOrderA()
        {
            if (mData.mCurOpenOrder.OpenOrder == null)
            {
                return;
            }
            string orderID = mData.mCurOpenOrder.OpenOrder.OrderId;
            CancelCurOrderA(orderID);
        }

        /// <summary>
        /// 开仓止盈单
        /// 0 如果完全成交，开个新止盈单
        /// 1如果是订单首次部分成交，那么开新止盈单
        /// 2如果分首次部分成交那么删除对应止盈订单，开个数量对等的止盈订单
        /// </summary>
        /// <param name="openOrder"></param>
        /// <returns></returns>
        private async Task<ExchangeOrderResult> AddProfitOrder(ExchangeOrderResult openOrder,decimal addAmount = 0)
        {
            decimal price = openOrder.AveragePrice * (1 + mData.ProfitRate);
            price = NormalizationMinPriceUnit(price);
            decimal amount = NormalizationMinAmountUnit(openOrder.AmountFilled + addAmount);
            ExchangeOrderResult order = null;
            try
            {
                Logger.Debug($"AddProfitOrder  amount {amount} openOrder.AmountFilled {openOrder.AmountFilled} addAmount {addAmount} price {price} openOrder.Price{openOrder.Price}");
                order = await AddOrder(false, mData.SymbolA, price, amount);
            }
            catch (Exception ex)
            {

                if (ex.ToString().Contains("Not enough balances"))
                {
                    try
                    {

                        //                         var amountWallet = await mExchangeAAPI.GetWalletSummaryAsync(mData.BalanceSymbol);
                        //                         if (amountWallet < mData.BaseAmont)//大于
                        //                         {
                        //                             amountWallet = 0;
                        //                             throw ex;
                        //                         }
                        //                         else
                        //                         {
                        //                             amountWallet =   NormalizationMinAmountUnit(amountWallet - mData.BaseAmont);
                        //                             Logger.Debug($"AddProfitOrder 保证金不够重新提交订单 amountWallet {amountWallet} openOrder.AmountFilled {openOrder.AmountFilled} addAmount {addAmount} price {price} openOrder.Price{openOrder.Price}");
                        //                             order = await AddOrder(false, mData.SymbolA, price, amountWallet);
                        //                         }
                        //直接提交对应数量市价订单
                        var amountWallet = (await mExchangeAAPI.GetAmountsAvailableToTradeAsync())[mData.BalanceSymbol];
                        if (amountWallet < mData.BaseAmont)//大于
                        {
                            amountWallet = 0;
                            throw ex;
                        }
                        else
                        {
                            amountWallet = NormalizationMinAmountUnit(amountWallet - mData.BaseAmont);
                        }

                        if (amount - amountWallet>0)
                        {
                            ExchangeOrderRequest requestA = new ExchangeOrderRequest();

                            requestA.Amount = amount - amountWallet;
                            requestA.MarketSymbol = mData.SymbolA;
                            requestA.IsBuy = true;
                            requestA.OrderType = OrderType.Market;
                            var v = await mExchangeAAPI.PlaceOrderAsync(requestA);
                            Logger.Debug("数量对不齐，容错处理");
                        }
                        

                    }
                    catch (Exception GetWalletSummaryAsyncEX)
                    {

                        Logger.Debug("GetWalletSummaryAsync error " + GetWalletSummaryAsyncEX.ToString());
                    }
                   
                }
            }
            
           
            return order;
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
        decimal NormalizationMinPriceUnit(decimal price)
        {
            var s = 1 / mData.MinPriceUnit;
            return Math.Round(price * s) / s;
        }
        decimal NormalizationMinAmountUnit(decimal amount)
        {
            var s = 1 / mData.MinAmountUnit;
            return Math.Floor(amount * s) / s;
        }
   
        public class Options
        {
            public string ExchangeNameA;
            public string SymbolA;
            public string BalanceSymbol;
            /// <summary>
            /// 初始价值,单位usd，可能直接超过最大本金（相当于开高杠杆）
            /// </summary>
            public decimal StartMoney;
            /// <summary>
            /// 已使用价值，开仓占用的价值
            /// </summary>
            public decimal UseMoney;
            /// <summary>
            /// 开仓率
            /// </summary>
            public decimal OpenRate;
            /// <summary>
            /// 止盈率
            /// </summary>
            public decimal ProfitRate;
            /// <summary>
            /// 开仓仓位率
            /// </summary>
            public decimal OpenPositionRate;
            /// <summary>
            /// 首单开仓起始价格
            /// </summary>
            public decimal StartPrice;
            /// <summary>
            /// 上次成交均价
            /// </summary>
            public decimal LastFillPrice;
            /// <summary>
            /// 基础数量 ，用于扣手续费
            /// </summary>
            public decimal BaseAmont;


            /// <summary>
            /// 最小价格单位
            /// </summary>
            public decimal MinPriceUnit = 0.5m;
            /// <summary>
            /// 最小订单总价格
            /// </summary>
            public decimal MinOrderAmount = 1m;
            /// <summary>
            /// 最小购买数量
            /// </summary>
            public decimal MinAmountUnit = 0.1m;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurAAmount = 0;
            /// <summary>
            /// 间隔时间
            /// </summary>
            public int IntervalMillisecond = 500;
            /// <summary>
            /// 自动计算最大开仓数量
            /// </summary>
            public bool AutoCalcMaxPosition = true;
            /// <summary>
            /// A交易所加密串路径
            /// </summary>
            public string EncryptedFileA;
            /// <summary>
            /// redis连接数据
            /// </summary>
            public string RedisConfig = "localhost,password=l3h2p1w0*";
            /// <summary>
            /// 子账号标识
            /// </summary>
            public string SubAccount = "";
            /// <summary>
            /// 最新开仓的订单，一个开仓单，一个对应的止盈单
            /// </summary>
            public OrderPair mCurOpenOrder = new OrderPair(null,null);
            /// <summary>
            /// 挂单中的止盈订单，顺序由老到新，完全成交后删除
            /// </summary>
            public List<ExchangeOrderResult> mOpenProfitOrders = new List<ExchangeOrderResult>();
            public void SaveToDB(string DBKey)
            {

                RedisDB.Instance.StringSet(DBKey, this);
            }
            public static T LoadFromDB<T>(string DBKey)
            {
                return RedisDB.Instance.StringGet<T>(DBKey);
            }
        }
      
        [Flags]
        private enum ConnectState
        {
            All = 7,
            A = 1,
            B = 2,
            Orders = 4
        }
        public class OrderPair
        {
            /// <summary>
            /// 开仓单
            /// </summary>
            public ExchangeOrderResult OpenOrder;
            /// <summary>
            /// 止盈单
            /// </summary>
            public ExchangeOrderResult ProfitOrder;

            public OrderPair(ExchangeOrderResult openOrder, ExchangeOrderResult profitOrder)
            {
                OpenOrder = openOrder;
                ProfitOrder = profitOrder;
            }

            public void Clean()
            {
                Logger.Debug("清理所有订单");
                OpenOrder = null;
                ProfitOrder = null;
            }
        }

    }
}