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
            string publickey = "ub8PPU3GS0svhHvnuWp4iuvf";
            string privatekey = "PFGoUcE1SXaCorv3jWIvns31mEmfkgKvZfiE54rTUX0BOFQH";
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
            crossMarketConfig.MaxQty = 100;
            crossMarketConfig.MinGapRate = 0.0015m;
            crossMarketConfig.FeesA = -0.00025m;
            crossMarketConfig.FeesB = 0.00025m;
            crossMarketConfig.PendingOrderRatio = 0.6m;
            crossMarketConfig.MinUnit = 0.5m;
            return crossMarketConfig;
        }
    }
}
