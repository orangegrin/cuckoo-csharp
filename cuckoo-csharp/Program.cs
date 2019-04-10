using System;
using System.Linq;
using ExchangeSharp;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using cuckoo_csharp.Strategy.Arbitrage;

namespace cuckoo_csharp
{
    class Program
    {

        static void Main(string[] args)
        {

            BuildKeys();
            //var config1 = GetCrossMarketConfig();
            //config1.MinIRS = 0.0012m;
            //config1.MaxQty = 100;
            ////new CrossMarket(config1).Start();
            //var config2 = GetCrossMarketConfig();
            //config2.MinIRS = 0.0024m;
            //config2.MaxQty = 200;
            ////new CrossMarket(config2).Start();
            //var config3 = GetCrossMarketConfig();
            //config3.MinIRS = 0.0042m;
            //config3.MaxQty = 400;
            //new CrossMarket(config3).Start();

            //var config = new Intertemporal.IntertemporalConfig() { ExchangeNameA = ExchangeName.BitMEX, ExchangeNameB = ExchangeName.Binance, SymbolA = "ETHM19", SymbolB = "ETH_BTC", MaxQty = 1m, OPDF = -0.015m, CPDF = -0.015m, PerTrans = 1m, CurAmount = 0 };
            //new Intertemporal(config).Start();

            //             var config1 = new IntertemporalConfig()
            //             { ExchangeNameA = ExchangeName.BitMEX, ExchangeNameB = ExchangeName.Binance, SymbolA = "XBTM19",
            //                 SymbolB = "BTC_USDT", MaxQty = 40, OPDF = -0.007m, CPDF = -0.027m, PerTrans = 20 ,CurAmount = 0,ProfitRate = 0.004m,
            //                 UseLimit = true
            //             };
            //             new IntertemporalPlus(config1,1).Start();

            var config1 = new IntertemporalConfig()
            {
                ExchangeNameA = ExchangeName.BitMEX,
                ExchangeNameB = ExchangeName.Binance,
                SymbolA = "EOSM19",
                SymbolB = "EOS_BTC",
                AmountSymbol = "BTC",
                MaxQty = 2,
                OPDF = -0.027m,
                CPDF = -0.015m,
                PerTrans = 1m,
                CurAmount = 0,
                ProfitRate = 0.006m,
                UseLimit = true,
                MinPriceUnit=0.0000001m,
                startCoinAmount = 0m,
                FeesA = ExchangeFee.BitMEX_EOS,
                FeesB = ExchangeFee.Binance_EOS,
            };
            

            var config2 = new IntertemporalConfig()
            {
                ExchangeNameA = ExchangeName.BitMEX,
                ExchangeNameB = ExchangeName.Binance,
                SymbolA = "ETHM19",
                SymbolB = "ETH_BTC",
                AmountSymbol = "BTC",
                MaxQty = 28,
                OPDF = 0.004m,
                CPDF = 0.019m,
                PerTrans = 14,
                CurAmount = 0,
                ProfitRate = 0.014m,
                UseLimit = false
            };
            new IntertemporalPlus(config1, 1).Start();
            //new IntertemporalPlus(config2, 2).Start();
            //Test();

            while (true)
            {
                Console.ReadLine();
            }
        }

        private static void Test()
        {
            //
            var mConfig = new IntertemporalConfig()
            {
                ExchangeNameA = ExchangeName.BitMEX,
                ExchangeNameB = ExchangeName.Binance,
                SymbolA = "XBTM19",
                SymbolB = "BTC_USDT",
                AmountSymbol = "USDT",
                MaxQty = 28,
                OPDF = 0.004m,
                CPDF = 0.019m,
                PerTrans = 14,
                CurAmount = 0,
                ProfitRate = 0.014m,
                UseLimit = false
            };
            IExchangeAPI mExchangeBAPI = ExchangeAPI.GetExchangeAPI(mConfig.ExchangeNameB);
            IntertemporalPlus inter = new IntertemporalPlus(mConfig, 1);
            mExchangeBAPI.LoadAPIKeys(ExchangeName.Binance);
            inter.GetAmountsAvailableToTradeAsync(mExchangeBAPI, mConfig.AmountSymbol);



//             List<ExchangeOrderResult> openedBuyOrderListA = new List<ExchangeOrderResult>() {
//                 new ExchangeOrderResult()
//                 {
//                     Amount = 15,
//                     Price = 4982.5m,
//                     FillDate = DateTime.Now.AddDays(-1),
//                     IsBuy = true,
//                 },
//                 new ExchangeOrderResult()
//                 {
//                     Amount = 15,
//                     Price = 4982.5m,
//                     FillDate = DateTime.Now.AddDays(-1),
//                     IsBuy = true,
//                 }
//             };
//             List<ExchangeOrderResult> openedSellOrderListA = new List<ExchangeOrderResult>() {
//                 new ExchangeOrderResult()
//                 {
//                     Amount = 15,
//                     Price = 5305m,
//                     FillDate = DateTime.Now,
//                     IsBuy = false,
// 
//                 },
//                 new ExchangeOrderResult()
//                 {
//                     Amount = 15,
//                     Price = 5298m,
//                     FillDate = DateTime.Now,
//                     IsBuy = false,
//                 }
//             };
//             List<ExchangeOrderResult> closeedSellOrderListB = new List<ExchangeOrderResult>() {
//                 new ExchangeOrderResult()
//                 {
//                     Amount = 0.003024m,
//                     Price = 4982.5m,
//                     FillDate = DateTime.Now.AddDays(-1),
//                     IsBuy = false,
//                 },
//                 new ExchangeOrderResult()
//                 {
//                     Amount = 0.003022m,
//                     Price = 4982.5m,
//                     FillDate = DateTime.Now.AddDays(-1),
//                     IsBuy = false,
//                 }
//             };
//             List<ExchangeOrderResult> closeedBuyOrderListB = new List<ExchangeOrderResult>() {
//                 new ExchangeOrderResult()
//                 {
//                     Amount = 0.002883m,
//                     Price = 4982.5m,
//                     FillDate = DateTime.Now,
//                     IsBuy = true,
//                 },
//                 new ExchangeOrderResult()
//                 {
//                     Amount = 0.002884m,
//                     Price = 4982.5m,
//                     FillDate = DateTime.Now,
//                     IsBuy = true,
//                 }
//             };
// 
//             IntertemporalPlus.CountRewardRate(15, openedBuyOrderListA, openedSellOrderListA, closeedBuyOrderListB, closeedSellOrderListB);
        }

        static void BuildKeys()
        {
            //BITMEX Account 1:
            //string publickey = "2xrwtDdMimp5Oi3F6oSmtsew";
            //string privatekey = "rxgzE8FCETaWXxXAXe5daqxRJWshqJoD-ERIipxdC_H2hexs";

            //BITMEX Account 2:
            //name: t295202690@gmail.com
            //pass: tii540105249
            //             string publickey = "2xrwtDdMimp5Oi3F6oSmtsew";
            //             string privatekey = "rxgzE8FCETaWXxXAXe5daqxRJWshqJoD-ERIipxdC_H2hexs";

            //liuqiba910@gmail.com
            string publickey = "0jLWqkmsL0jQbCfWlL1nIGxY";
            string privatekey = "Pq6amCrncnOvSXh_-Oxf_P4hpsb-MgoBUkPUOlzH9W_p3t8C";
            CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.BitMEX, new string[2] { publickey, privatekey });

            //             string publickey2 = "GXKrqpqZXCnhJ82nhy7MwzPcYGIVtKyd9EtHcVGauxlOVSutTFyNwoa5yn6bteVO";
            //             string privatekey2 = "GtwcSOpV51tYAt8LeOGYDd31a7r7zmjerwYDyyBlRYBaUgw2tUFc0MtyaOvKQ6PR";
            //cuckoo@orangegrin.com
            string publickey2 = "JDNwbXiiihzY5qpRi6Z5AmHIa40baJ2EcL5rEKDfRvjhB7JRGSU8aQf4q2N69c5q";
            string privatekey2 = "rGjyCNhYaQkT9dT09OmARVM2gykTL51HhAOMC26mixRHAcdxypGLS4ApoxPwuuZG";


            CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.Binance, new string[2] { publickey2, privatekey2 });


            //string publickey = "v_eancoUBO7lRJf9_AXsCGbg";
            //string privatekey = "jBLZ2aUdS9U-LDP5ZvBrItW1Rg6akLgNGqUQCuQq3wK0HRS4";
            //CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.BitMEX, new string[2] { publickey, privatekey });
            //string publickey2 = "440757a5-e78ac402-84903e36-194b1";
            //string privatekey2 = "7f0a0c5c-24fd0bb9-eb64134f-2e1b6";
            //CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.HBDM, new string[2] { publickey2, privatekey2 });
            //string publickey3 = "52442d6f217c94e99ee10581c050598f";
            //string privatekey3 = "3a565f3b7873aaab6f8c6d17135d6dde484c14119c15080887047e16ac91e8ef";
            //CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.GateioDM, new string[2] { publickey3, privatekey3 });
        }
        static CrossMarketConfig GetCrossMarketConfig()
        {
            var crossMarketConfig = new CrossMarketConfig();
            crossMarketConfig.ExchangeNameA = ExchangeName.BitMEX;
            crossMarketConfig.ExchangeNameB = ExchangeName.HBDM;
            crossMarketConfig.SymbolA = "XBTUSD";
            crossMarketConfig.SymbolB = "BTC_CW";
            crossMarketConfig.SymbolC = "BTC_USD";
            crossMarketConfig.MaxQty = 100;
            crossMarketConfig.MinIRS = 0.002m;
            crossMarketConfig.FeesA = -0.00025m;
            crossMarketConfig.FeesB = 0.0003m;
            crossMarketConfig.POR = 0.6m;
            crossMarketConfig.MinPriceUnit = 0.5m;
            crossMarketConfig.PeriodFreq = 60;
            crossMarketConfig.TimePeriod = 30;
            return crossMarketConfig;
        }
    }
}
