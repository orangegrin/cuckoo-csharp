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
            var config1 = GetCrossMarketConfig();
            config1.MinIRS = 0.0013m;
            config1.MaxQty = 100;
            new CrossMarket(config1).Start();
            var config2 = GetCrossMarketConfig();
            config2.MinIRS = 0.0024m;
            config2.MaxQty = 200;
            new CrossMarket(config2).Start();
            var config3 = GetCrossMarketConfig();
            config3.MinIRS = 0.0042m;
            config3.MaxQty = 400;
            new CrossMarket(config3).Start();
            while (true)
            {
                Console.ReadLine();
            }
        }

        static CrossMarketConfig GetCrossMarketConfig()
        {
            var crossMarketConfig = new CrossMarketConfig();
            crossMarketConfig.ExchangeNameA = ExchangeName.BitMEX;
            crossMarketConfig.ExchangeNameB = ExchangeName.HBDM;
            crossMarketConfig.SymbolA = "XBTUSD";
            crossMarketConfig.SymbolB = "BTC_CW";
            crossMarketConfig.MaxQty = 100;
            crossMarketConfig.MinIRS = 0.002m;
            crossMarketConfig.FeesA = -0.00025m;
            crossMarketConfig.FeesB = 0.0003m;
            crossMarketConfig.POR = 0.6m;
            crossMarketConfig.MinPriceUnit = 0.5m;
            crossMarketConfig.PeriodFreq = 60 * 60;
            crossMarketConfig.TimePeriod = 60;
            return crossMarketConfig;
        }
    }
}
