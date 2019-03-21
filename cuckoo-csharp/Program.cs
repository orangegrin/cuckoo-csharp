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
            //CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.BitMEX, "6hj439I7D1v3455ThTZf-Xe4", "6NI1qwEaQfFxPTKM01_Du6DWiJpZiH9dDE_8dGnXOIybMdpY");
            CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.BitMEX, "pFUlk3M9J3n1DGMQt4cMNxOr", "7JPonP208D_JM7wnp_kv1xupKdTwMznB5z1oVgNTGmphav1Q");
            LadderBack lb = new LadderBack(new LadderBack.Config { ExchangeName = ExchangeName.BitMEX, Symbol = "XBTUSD", Step = 1m, KeepValue = 100000m, MinPriceUnit = 0.5m });
            lb.Start();
            //var config1 = GetCrossMarketConfig();
            //config1.MinIRS = 0.001m;
            //config1.MaxQty = 100;
            //new CrossMarket(config1,1).Start();
            //var config2 = GetCrossMarketConfig();
            //config2.MinIRS = 0.0024m;
            //config2.MaxQty = 200;
            //new CrossMarket(config2,2).Start();
            //var config3 = GetCrossMarketConfig();
            //config3.MinIRS = 0.0042m;
            //config3.MaxQty = 400;
            //new CrossMarket(config3,3).Start();
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
