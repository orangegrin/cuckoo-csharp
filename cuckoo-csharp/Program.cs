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
            new CrossMarket(GetCrossMarketConfig()).Start();
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
            string publickey = "2xrwtDdMimp5Oi3F6oSmtsew";
            string privatekey = "rxgzE8FCETaWXxXAXe5daqxRJWshqJoD-ERIipxdC_H2hexs";

            //string publickey = "v_eancoUBO7lRJf9_AXsCGbg";
            //string privatekey = "jBLZ2aUdS9U-LDP5ZvBrItW1Rg6akLgNGqUQCuQq3wK0HRS4";
            CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.BitMEX, new string[2] { publickey, privatekey });
            string publickey2 = "440757a5-e78ac402-84903e36-194b1";
            string privatekey2 = "7f0a0c5c-24fd0bb9-eb64134f-2e1b6";
            CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.HBDM, new string[2] { publickey2, privatekey2 });
        }

        static CrossMarketConfig GetCrossMarketConfig()
        {
            var crossMarketConfig = new CrossMarketConfig();
            crossMarketConfig.ExchangeNameA = ExchangeName.BitMEX;
            crossMarketConfig.ExchangeNameB = ExchangeName.HBDM;
            crossMarketConfig.SymbolA = "XBTUSD";
            crossMarketConfig.SymbolB = "BTC_CW";
            crossMarketConfig.MaxQty = 10;
            crossMarketConfig.MinIRS = 0.0001m;
            crossMarketConfig.FeesA = -0.00025m;
            crossMarketConfig.FeesB = 0.00025m;
            crossMarketConfig.POR = 0.6m;
            crossMarketConfig.MinPriceUnit = 0.5m;
            crossMarketConfig.PeriodFreq = 60;
            crossMarketConfig.TimePeriod = 30;
            return crossMarketConfig;
        }
    }
}
