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
    /// 
    /// 
    /// </summary>
    public class BB_SMA
    {
        private IExchangeAPI mExchangeAAPI;
        private IWebSocket mOrderws;
        private IWebSocket mOrderBookAws;

        private int mOrderBookAwsCounter = 0;
        private int mOrderDetailsAwsCounter = 0;
        private int mId;
        /// <summary>
        /// A交易所的的订单薄
        /// </summary>
        private ExchangeOrderBook mOrderBookA;
        /// <summary>
        /// 当前挂出去的订单
        /// </summary>
        private ExchangeOrderResult mCurOrderA;
        /// <summary>
        /// A交易所的历史订单ID
        /// </summary>
        private List<string> mOrderIds = new List<string>();
        /// <summary>
        /// A交易所 历史成交订单详情
        /// </summary>
        private Dictionary<string, ExchangeOrderResult> mOrderResultsDic = new Dictionary<string, ExchangeOrderResult>();
        /// <summary>
        /// A交易所止损止盈订单ID
        /// </summary>
        private List<string> mProfitOrderIds = new List<string>();
        /// <summary>
        /// A交易所的历史成交订单ID
        /// </summary>
        private List<string> mOrderFiledIds = new List<string>();
        /// <summary>
        /// 部分填充
        /// </summary>
        private Dictionary<string, decimal> mFilledPartiallyDic = new Dictionary<string, decimal>();
        private Options mData { get; set; }
        /// <summary>
        /// 差价历史记录
        /// </summary>
        private List<decimal> mDiffHistory = null;
        /// <summary>
        /// 当前为第一次开仓
        /// </summary>
        private bool mIsFirstOpen = true;
        /// <summary>
        /// 叠加等待时间
        /// </summary>
        private int mCurCDTime = 0;

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
        private bool mOrderwsConnect = false;
        private bool mOrderBookAwsConnect = false;
        private bool mOnCheck = false;
        private bool mOnTrade = false;//是否在交易中
        private bool mOnConnecting = false;
        private bool mBuyAState;
        private decimal mAllPosition;
        /// <summary>
        /// 每次购买数量
        /// </summary>
        private decimal mPerBuyAmount = 0;

        public BB_SMA(Options config, int id = -1)
        {
            mId = id;
            mDBKey = string.Format("BB_SMA:CONFIG:{0}:{1}:{2}", config.ExchangeNameA,  config.SymbolA,  id);
            RedisDB.Init(config.RedisConfig);
            mData = Options.LoadFromDB<Options>(mDBKey);
            if (mData == null)
            {
                mData = config;
                config.SaveToDB(mDBKey);
            }
            if (mData.CurAAmount != 0)
                mIsFirstOpen = false;
            if (mData.LastTradeDate!=null  &&  mData.LastTradeDate.Year> (DateTime.UtcNow.Year-1))
            {
                TimeSpan cd =  mData.LastTradeDate.AddSeconds(mData.TradeCoolDownTime*mData.GetPerTimeSeconds()) - DateTime.UtcNow;
                var temp = Convert.ToInt32(cd.TotalSeconds);
                mCurCDTime = temp > 0 ? temp : 0;
                Logger.Debug("remainder time " + mCurCDTime + " seconds    mData.LastTradeDate:" + mData.LastTradeDate.ToString("yyyy-MM-dd hh:mm:ss"));

            }
            mExchangeAAPI = ExchangeAPI.GetExchangeAPI(mData.ExchangeNameA);
        }
        public void Start()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
            mExchangeAAPI.LoadAPIKeys(mData.EncryptedFileA);
            mExchangeAAPI.SubAccount = mData.SubAccount;

            SubWebSocket();
            WebSocketProtect();
            CheckPosition();
            CheckOrderBook();
        }
       
        #region Connect
        private void SubWebSocket(ConnectState state = ConnectState.All)
        {
            mOnConnecting = true;
            if (state.HasFlag(ConnectState.Orders))
            {
                mOrderws = mExchangeAAPI.GetOrderDetailsWebSocket(OnOrderAHandler);
                mOrderws.Connected += async (socket) => { mOrderwsConnect = true; Logger.Debug("GetOrderDetailsWebSocket 连接"); OnConnect(); };
                mOrderws.Disconnected += async (socket) =>
                {
                    mOrderwsConnect = false;
                    WSDisConnectAsync("GetOrderDetailsWebSocket 连接断开");
                };
            }
            //避免没有订阅成功就开始订单
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
                mOrderDetailsAwsCounter = 0;
                await Task.Delay(1000 * delayTime);
                Logger.Debug(Utils.Str2Json("mOrderBookAwsCounter", mOrderBookAwsCounter,  "mOrderDetailsAwsCounter", mOrderDetailsAwsCounter));
                bool detailConnect = true;
                if (mOrderDetailsAwsCounter == 0)
                    detailConnect = await IsConnectAsync();
                Logger.Debug(Utils.Str2Json("mOrderDetailsAwsCounter", mOrderDetailsAwsCounter));
                if (mOrderBookAwsCounter < 1  || (!detailConnect))
                {
                    Logger.Error(new Exception("ws 没有收到推送消息"));
                    await ReconnectWS();
                }
            }
        }

        private async Task ReconnectWS(ConnectState state = ConnectState.All)
        {
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
            if (state.HasFlag(ConnectState.Orders))
            {
                mOrderwsConnect = false;
                mOrderws.Dispose();
            }
               
        }

        /// <summary>
        /// 测试 GetOrderDetailsWebSocket 是否有推送消息
        /// 发送一个多单限价，用卖一+100作为价格（一定被取消）。 等待10s如果GetOrderDetailsWebSocket没有返回消息说明已经断开
        private async Task<bool> IsConnectAsync()
        {
            await mOrderws.SendMessageAsync("ping");
            await Task.Delay(5 * 1000);
            return mOrderDetailsAwsCounter > 0;
        }
        private bool OnConnect()
        {
            bool connect = mOrderwsConnect & mOrderBookAwsConnect ;
            if (connect)
                mOnConnecting = false;
            return connect;
        }
        
        private async Task WSDisConnectAsync(string tag)
        {
            
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
                if (!OnConnect())
                {
                    await Task.Delay(5 * 1000);
                    continue;
                }
                if (mOnTrade || mExchangePending == true)//交易正在进行或者，准备开单。检查数量会出现问题
                {
                    await Task.Delay(200);
                    continue;
                }
                mOnCheck = true;
                Logger.Debug("-----------------------CheckPosition-----------------------------------");
                lock(mOrderResultsDic)//清理数据
                    mOrderResultsDic.Clear();
                                
                ExchangeMarginPositionResult posA ;
                try
                {
                    //等待10秒 避免ws虽然推送数据刷新，但是rest 还没有刷新数据
                    await Task.Delay(10* 1000);
                    posA = await mExchangeAAPI.GetOpenPositionAsync(mData.SymbolA);
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
                decimal realAmount = posA.Amount;

                mCurAAmount = realAmount;
                mOnCheck = false;
                await Task.Delay(5 * 60 * 1000);
            }
        }
        #endregion


        private async Task CountDiffGridMaxCount(decimal marketPrice)
        {
            try
            {
                //decimal noUseBtc =  await mExchangeAAPI.GetWalletSummaryAsync(""); 
                //decimal allCoin = noUseBtc;
                decimal avgPrice = marketPrice;
                //mAllPosition = allCoin * mData.Leverage / avgPrice ;//单位eth个数
                decimal newPerTrans = Math.Round(mData.PerBuyUSD / avgPrice / mData.MinAmountA) * mData.MinAmountA;
                
                if (mData.PerTrans != newPerTrans )
                {
                    mData.PerTrans = newPerTrans;
                    mData.SaveToDB(mDBKey);
                }
                Logger.Debug(Utils.Str2Json("mData.PerTrans", mData.PerTrans, "allPosition", mAllPosition));
            }
            catch (System.Exception ex)
            {
                Logger.Error("ChangeMaxCount ex" + ex.ToString());
            }
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            Logger.Debug("------------------------ OnProcessExit ---------------------------");
            mExchangePending = true;
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
                if (mCurCDTime>0)
                {
                    await Task.Delay(mCurCDTime * 1000);
                    mCurCDTime = 0;
                }
                Options temp = Options.LoadFromDB<Options>(mDBKey);
                Options last_mData = mData;
                if (mCurOrderA==null)//避免多线程读写错误
                    mData = temp;
                else
                {
                    temp.CurAAmount = mData.CurAAmount;
                    mData = temp;
                }
                //Logger.Debug(mData.PerBuyUSD.ToString());
                await Execute();
                await Task.Delay(mData.IntervalMillisecond );
                mExchangePending = false;
            }
        }
        private bool Precondition()
        {
            if (!OnConnect())
                return false;
            if (mOrderBookA == null )
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
        private async Task Execute()
        {
            int outTime = 60;

            DateTime now = DateTime.UtcNow;
            double perTimeSeconds = mData.GetPerTimeSeconds();

            var smaCandles = await mExchangeAAPI.GetCandlesAsync(mData.SymbolA, mData.GetPerTimeSeconds(), now.AddSeconds(-perTimeSeconds * (mData.SMPPerTime+1)-5), now.AddSeconds(-perTimeSeconds));
            //await Task.Delay(1 * 1000);
            var bbCandles = await mExchangeAAPI.GetCandlesAsync(mData.SymbolA, mData.GetPerTimeSeconds(), now.AddSeconds(-mData.GetPerTimeSeconds() * (mData.BollingerBandsPer+1)-5), now.AddSeconds(-perTimeSeconds));
            //test  
//             var lastTime = new DateTime(2020, 6, 28, 18, 41, 33, DateTimeKind.Utc);
//             var smaCandles = await mExchangeAAPI.GetCandlesAsync(mData.SymbolA, mData.GetPerTimeSeconds(),lastTime.AddSeconds(-mData.GetPerTimeSeconds() * mData.SMPPerTime ), lastTime.AddSeconds(0));
//             await Task.Delay(5 * 1000);
// 
//             var bbTime = new DateTime(2020, 6, 28, 18, 41, 33, DateTimeKind.Utc);
//             var bbCandles = await mExchangeAAPI.GetCandlesAsync(mData.SymbolA, mData.GetPerTimeSeconds(), bbTime.AddSeconds(-mData.GetPerTimeSeconds() * mData.BollingerBandsPer ), bbTime.AddSeconds(0));

            decimal lastHourClose = smaCandles.Last<MarketCandle>().ClosePrice;
            var sma = GetLastSMA(smaCandles.Select(candle => candle.ClosePrice).ToArray());
            decimal closePrice = smaCandles.Last<MarketCandle>().ClosePrice;
            var bbLimit = GetBollingerBandsLimit(bbCandles.ToList());
            bool closePosition1 = Math.Abs((closePrice - sma) / sma) > mData.CloseRate;
            bool closePosition2 = bbLimit < 0.5m-mData.BBCloseRate || bbLimit > 0.5m+mData.BBCloseRate;
            await CountDiffGridMaxCount(closePrice);
            Logger.Debug("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++4"+ now.ToString());
            Logger.Debug("closePrice:" + closePrice + " sma:" + sma + "lastSmaTime:" + smaCandles.Last<MarketCandle>().Timestamp.ToString() + "   bbLimit " + bbLimit + "  condtion1:" + (Math.Abs((closePrice - sma) / sma)) + "  canClose " + closePosition1.ToString()+ closePosition2.ToString());

            bool canTrade = false;
            bool isBuy = false;
            decimal amount = 0;
            if ( closePrice!= sma)//如果仓位==0 并且满足开仓条件
            {
                isBuy = closePrice > sma;
                if (mCurAAmount == 0)
                    amount = mData.PerTrans;
                else
                    amount = isBuy ? mData.PerTrans - mCurAAmount : mData.PerTrans - (-mCurAAmount);

                if (amount > 0)
                {
                    if (isBuy == mData.LastIsBuy)
                    {
                        Logger.Debug("is same with last side ,can not trde!!!");
                    }

                    if (mIsFirstOpen || isBuy!= mData.LastIsBuy)//修改为 开仓不能和上次开仓方向相同
                    {
                        canTrade = true;
                        mData.LastIsBuy = isBuy;
                        mData.SaveToDB(mDBKey);
                    }
                }
            }
            if (!canTrade && mCurAAmount!=0 && closePosition1 && closePosition2)//如果仓位不等于0 那么检查是否满足平仓条件, 满足则平仓到0
            {
                canTrade = true;
                isBuy = mCurAAmount<0;
                amount = Math.Abs(mCurAAmount);
                Logger.Debug("start close ");
            }
            //canTrade = false;
            if (canTrade)
            {
                mIsFirstOpen = false;
                mRunningTask = DoTrade(isBuy, amount);
                mCurCDTime = mData.TradeCoolDownTime * mData.GetPerTimeSeconds();
                mData.LastTradeDate = DateTime.UtcNow;
                mData.SaveToDB(mDBKey);
                Logger.Debug("cool down :" + mCurCDTime);
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
                        mCurOrderA = null;
                    mRunningTask = null;
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
                if (mOrderBookA==null ||  !OnConnect() )
                    continue;
                else
                {
                    if (lastA==null)
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
                    if ((lastA.Value.Price == curA.Value.Price && lastA.Value.Amount == curA.Value.Amount))
                    {
                        state = ConnectState.A;
                       
                    }   
                    if (state!=null)
                    {
                        Logger.Error("CheckOrderBook  orderbook 错误");
                        await ReconnectWS(state.Value);
                    }
                    lastA = curA;
                }
               
            }
        }
       



        private void PrintInfo(decimal bidA, decimal askA, decimal bidB, decimal askB, decimal a2bDiff, decimal b2aDiff, decimal A2BDiff, decimal B2ADiff, decimal buyAmount, 
            decimal bidAAmount, decimal askAAmount, decimal bidBAmount, decimal askBAmount )
        {
            Logger.Debug("================================================");
            Logger.Debug(Utils.Str2Json("BA价差当前百分比↑", a2bDiff.ToString(), "BA价差百分比↑", A2BDiff.ToString() )) ;
            Logger.Debug(Utils.Str2Json("BA价差当前百分比↓" , b2aDiff.ToString(), "BA价差百分比↓" , B2ADiff.ToString()));
            Logger.Debug(Utils.Str2Json("Bid A", bidA, " Bid B", bidB, "bidAAmount", bidAAmount, "bidBAmount", bidBAmount));
            Logger.Debug(Utils.Str2Json("Ask A", askA, " Ask B", askB, "askAAmount", askAAmount, "askBAmount", askBAmount));
            Logger.Debug(Utils.Str2Json("mCurAmount", mCurAAmount, " buyAmount",  buyAmount ));


            var csvList = new List<List<string>>();
            List<string> strList = new List<string>()
            {
                (bidA-bidB).ToString(),
            };
            csvList.Add(strList);
            Utils.AppendCSV(csvList, Path.Combine(Directory.GetCurrentDirectory(), "Data_" + mId + ".csv"), false);
        }
    
       
        private void PrintFilledOrder(ExchangeOrderResult order, ExchangeOrderResult backOrder)
        {
            if (order == null)
                return;
            if (backOrder == null)
                return;
            try
            {
                Logger.Debug("--------------PrintFilledOrder--------------");
                Logger.Debug(Utils.Str2Json("filledTime", Utils.GetGMTimeTicks(order.OrderDate).ToString(),
                    "direction", order.IsBuy ? "buy" : "sell",
                    "orderData", order.ToExcleString()));
                //如果是平仓打印日志记录 时间  ，diff，数量
                decimal lastAmount = mCurAAmount + (order.IsBuy? -backOrder.Amount : backOrder.Amount);
                if ((lastAmount >0 && !order.IsBuy) ||//正仓位，卖
                    (lastAmount < 0) && order.IsBuy)//负的仓位，买
                {
                    DateTime dt = backOrder.OrderDate.AddHours(8);
                    List<string> strList = new List<string>()
                    {
                        dt.ToShortDateString()+"/"+dt.ToLongTimeString(),order.IsBuy ? "buy" : "sell",backOrder.Amount.ToString(), (order.AveragePrice-backOrder.AveragePrice).ToString()
                    };
                    Utils.AppendCSV(new List<List<string>>() { strList }, Path.Combine(Directory.GetCurrentDirectory(), "ClosePosition"+mId+".csv"), false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("PrintFilledOrder"+ex);
            }
        }
        /// <summary>
        /// 当A交易所的订单发生改变时候触发
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderAHandler(ExchangeOrderResult order)
        {
            mOrderDetailsAwsCounter++;
            if (order.MarketSymbol.Equals("pong"))
            {
                //Logger.Debug("pong");
                return;
            }
            Logger.Debug("-------------------- OnOrderAHandler ---------------------------");
            if (order.Result == ExchangeAPIOrderResult.FilledPartially || order.Result == ExchangeAPIOrderResult.Filled)
            {
                if ((order.StopPrice > 0 && order.Amount > 0) || mProfitOrderIds.Contains(order.OrderId))
                {
                    Logger.Error("止盈触发停止运行程序");
                    Environment.Exit(0);
                    throw new Exception("止盈触发停止运行程序");
                }
            }
            if (order.MarketSymbol != mData.SymbolA)
                return;
            if (!IsMyOrder(order.OrderId))
            {
                AddFilledOrderResult(order);
                return;
            }

            switch (order.Result)
            {
                case ExchangeAPIOrderResult.Unknown:
                    Logger.Debug("-------------------- Order Unknown ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Filled:
                    //OnOrderFilled(order);
                    Logger.Debug("-------------------- Order Filled ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.FilledPartially:
                    //OnFilledPartiallyAsync(order);
                    Logger.Debug("-------------------- Order FilledPartially ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Pending:
                    Logger.Debug("-------------------- Order Pending ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Error:
                    Logger.Debug("-------------------- Order Error ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.Canceled:
                    Logger.Debug("-------------------- Order Canceled ---------------------------");
                    // OnOrderCanceled(order);
                    break;
                case ExchangeAPIOrderResult.FilledPartiallyAndCancelled:
                    Logger.Debug("-------------------- Order FilledPartiallyAndCancelled ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                case ExchangeAPIOrderResult.PendingCancel:
                    Logger.Debug("-------------------- Order PendingCancel ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
                default:
                    Logger.Debug("-------------------- Order Default ---------------------------");
                    Logger.Debug(order.ToExcleString());
                    break;
            }
        }
     
        /// <summary>
        /// 开始开仓
        /// </summary>
        private async Task DoTrade( bool isBuy,decimal amount)
        {

            var req = new ExchangeOrderRequest();
            req.Amount = amount;
            req.IsBuy = isBuy;
            req.OrderType = OrderType.Market;
            req.MarketSymbol = mData.SymbolA;
            Logger.Debug("----------------------------DoTrade---------------------------");
            Logger.Debug(req.ToStringInvariant());
            for (int i = 1; ; i++)//当B交易所也是bitmex， 防止bitmex overload一直提交到成功
            {
                try
                {
                    var res = await mExchangeAAPI.PlaceOrderAsync(req);
                    mCurAAmount += req.IsBuy ? amount : -amount;
                    Logger.Debug("--------------------------------DoTrade Result-------------------------------------");
                    Logger.Debug(res.ToString());
                    break;
                }
                catch (Exception ex)
                {
                    if (ex.ToString().Contains("overloaded") || ex.ToString().Contains("403 Forbidden") || ex.ToString().Contains("Not logged in"))
                    {
                        Logger.Error(Utils.Str2Json("req", req.ToStringInvariant(), "ex", ex));
                        await Task.Delay(2000);
                    }
                    else if (ex.ToString().Contains("RateLimitError"))
                    {
                        Logger.Error(Utils.Str2Json("req", req.ToStringInvariant(), "ex", ex));
                        await Task.Delay(5000);
                    }
                    else
                    {
                        Logger.Error(Utils.Str2Json("DoTrade抛错", ex.ToString()));
                        throw ex;
                    }
                }
            }
        }
        /// <summary>
        /// 获取当前的sma值
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private decimal GetLastSMA(decimal[] data )
        {
           
            return data.Sum()/data.Length;
        }
        /// <summary>
        /// 获取 布林极限
        /// </summary>
        /// <param name="candles"></param>
        public decimal GetBollingerBandsLimit(List<MarketCandle> candles)
        {
            BollingerBandsPoint bbp = new BollingerBandsPoint();
            bbp.Exe(mData.BollingerBandsPer, mData.BollingerBandsStand, candles);
            Logger.Debug("close:" + candles.Last<MarketCandle>().ClosePrice + "bbp.LowerBandPrice.Value" + bbp.LowerBandPrice.Value + "  bbp.UpperBandPrice.Value" + bbp.UpperBandPrice.Value);
            decimal bollLimt = (candles.Last<MarketCandle>().ClosePrice - bbp.LowerBandPrice.Value) / (bbp.UpperBandPrice.Value - bbp.LowerBandPrice.Value);
            return bollLimt;
        }

        /// <summary>
        /// 判断是否是我的ID
        /// </summary>
        /// <param name="orderId"></param>
        /// <returns></returns>
        private bool IsMyOrder(string orderId)
        {
            return mOrderIds.Contains(orderId);
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
            var s = 1 / mData.MinPriceUnit;
            return Math.Round(price * s) / s;
        }
        private void AddFilledOrderResult(ExchangeOrderResult orderRequest)
        {
            if (orderRequest.Result == ExchangeAPIOrderResult.Filled || orderRequest.Result == ExchangeAPIOrderResult.FilledPartially)
            {
                if (mOrderResultsDic.TryGetValue(orderRequest.OrderId, out ExchangeOrderResult lastResult))
                {
                    if (lastResult.Result != ExchangeAPIOrderResult.Filled)
                    {
                        mOrderResultsDic[orderRequest.OrderId] = orderRequest;
                    }
                }
                else
                    mOrderResultsDic.Add(orderRequest.OrderId, orderRequest);
            }
        }

        
       
        public class Options
        {
            public string ExchangeNameA;
            public string SymbolA;
            public string Symbol;
            public decimal PerBuyUSD = 0;
            public decimal PerTrans = 0;
            /// <summary>
            /// 最小价格单位
            /// </summary>
            public decimal MinPriceUnit = 0.5m;
            /// <summary>
            /// 最少购买币 单位
            /// </summary>
            public decimal MinAmountA = 0.0001m;
            /// <summary>
            /// 当前仓位数量
            /// </summary>
            public decimal CurAAmount = 0;
            /// <summary>
            /// 间隔时间
            /// </summary>
            public int IntervalMillisecond = 60000;
            public int SMPPerTime = 720;
            public int BollingerBandsPer = 2000;
            public int BollingerBandsStand = 2;
            public int Leverage = 1;
            /// <summary>
            /// 如果完成交易 ,那么进入交易冷却时间,交易冷却时间走完才能进行下次交易
            /// </summary>
            public int TradeCoolDownTime = 1;
            /// <summary>
            /// 平仓比例
            /// 正常应该是0.3m
            /// </summary>
            public decimal CloseRate = 0.3m;
            /// <summary>
            /// 用0.5+- 算出   
            /// 正常情况应该是 0.5m
            /// </summary>
            public decimal BBCloseRate = 0.5m;
            /// <summary>
            /// 上次交易时间,用于确定下次交易时间
            /// </summary>
            public DateTime LastTradeDate;
            /// <summary>
            /// h = hour,m=min,s = second
            /// </summary>
            public string PerTimeUnit = TimeUnit.Min;
            /// <summary>
            /// 本位币
            /// </summary>
            public string AmountSymbol = "BTC";
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
            /// 上次开仓是否是多仓
            /// </summary>
            public bool LastIsBuy = false;

            public void SaveToDB(string DBKey)
            {
                RedisDB.Instance.StringSet(DBKey, this);
            }
            public static T LoadFromDB<T>(string DBKey)
            {
                return RedisDB.Instance.StringGet<T>(DBKey);
            }
            public int GetPerTimeSeconds()
            {
                int unit = 1;
                switch (PerTimeUnit)
                {
                    case TimeUnit.Hour:
                        unit = 60 * 60;
                        break;
                    case TimeUnit.Min:
                        unit = 60;
                        break;
                    case TimeUnit.Second:
                        unit = 1;
                        break;
                    default:
                        unit = 1;
                        break;
                }
                return unit;
            }
        }

//         public class IOhlcvEx : IOhlcv
//         {
//             public decimal mOpen;
//             public decimal mHigh;
//             public decimal mLow;
//             public decimal mClose;
//             public decimal mVolume;
// 
//             public DateTimeOffset mDateTime;
// 
//             public IOhlcvEx()
//             {
//             }
// 
//             public IOhlcvEx(decimal mOpen, decimal mHigh, decimal mLow, decimal mClose, decimal mVolume,DateTime dateTime)
//             {
//                 this.mOpen = mOpen;
//                 this.mHigh = mHigh;
//                 this.mLow = mLow;
//                 this.mClose = mClose;
//                 this.mVolume = mVolume;
//                 this.mDateTime = dateTime;
//             }
// 
//             decimal IOhlcv.Open { get => mOpen; set => mOpen =value; }
//             decimal IOhlcv.High { get => mHigh; set => mHigh = value ; }
//             decimal IOhlcv.Low { get => mLow; set => mLow = value ; }
//             decimal IOhlcv.Close { get => mClose ; set => mClose = value ; }
//             decimal IOhlcv.Volume { get => mClose ; set => mVolume = value ; }
// 
//             DateTimeOffset ITick.DateTime => mDateTime ;
// 
//             public static IOhlcvEx GetIOhlcvEx(MarketCandle candle)
//             {
//                 return new IOhlcvEx(candle.OpenPrice,candle.HighPrice,candle.LowPrice,candle.ClosePrice,Convert.ToDecimal(candle.BaseCurrencyVolume),candle.Timestamp.Date);
//             }
//         }

        private class TimeUnit
        {
            public const string Second = "s";
            public const string Hour = "h";
            public const string Min = "m";

        }

        [Flags]
        private enum ConnectState
        {
            All = 7,
            A = 1,
            B = 2,
            Orders = 4
        }

    }
}