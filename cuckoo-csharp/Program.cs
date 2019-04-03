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

            //var config = new Intertemporal.IntertemporalConfig() { ExchangeNameA = ExchangeName.BitMEX, ExchangeNameB = ExchangeName.Binance, SymbolA = "XBTH19", SymbolB = "BTC_USDT", MaxQty = 50, OPDF = 0.008m, CPDF = 0.003m, PerTrans = 10 };
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
                SymbolA = "XBTM19",
                SymbolB = "BTC_USDT",
                MaxQty = 30,
                OPDF = -0.014m,
                CPDF = 0m,
                PerTrans = 15,
                CurAmount = 0,
                ProfitRate = 0.014m,
                UseLimit = false
            };
            new IntertemporalPlus(config1, 1).Start();

            var config2 = new IntertemporalConfig()
            {
                ExchangeNameA = ExchangeName.BitMEX,
                ExchangeNameB = ExchangeName.Binance,
                SymbolA = "XBTM19",
                SymbolB = "BTC_USDT",
                MaxQty = 28,
                OPDF = 0m,
                CPDF = 0.014m,
                PerTrans = 14,
                CurAmount = 0,
                ProfitRate = 0.014m,
                UseLimit = false
            };
            new IntertemporalPlus(config2,2).Start();
















            while (true)
            {
                Console.ReadLine();
            }
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

            //t295202690@gmail.com
            string publickey = "V8_9x3w_EAaauj7IwY9rcJPt";
            string privatekey = "f88SX0cIVFSD4dgsR1rnm01kohSqb4dar9WHd6h1BwFBgcrT";
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
