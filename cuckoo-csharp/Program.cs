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
            var config1 = new IntertemporalConfig()
            {
                ExchangeNameA = ExchangeName.BitMEX,
                ExchangeNameB = ExchangeName.Binance,
                SymbolA = "EOSM19",
                SymbolB = "EOS_BTC",
                AmountSymbol = "BTC",
                A2BDiff = -0.022m,
                B2ADiff = -0.026m,
                PerTrans = 5m,
                CurAmount = 0,
                UseLimit = true,
                AutoCalcProfitRange = true,
                MinPriceUnit = 0.0000001m,
                InitialExchangeBAmount = 0m,
                MaxAmount = 10m,
                FeesA = ExchangeFee.BitMEX_EOS,
                FeesB = ExchangeFee.Binance_EOS,
            };


            var config2 = new IntertemporalConfig()
            {
                ExchangeNameA = ExchangeName.BitMEX,
                ExchangeNameB = ExchangeName.Binance,
                SymbolA = "EOSM19",
                SymbolB = "EOS_BTC",
                AmountSymbol = "BTC",
                A2BDiff = -0.0185m,
                B2ADiff = -0.0215m,
                PerTrans = 1m,
                CurAmount = 0,
                UseLimit = true,
                AutoCalcProfitRange = true,
                MinPriceUnit = 0.0000001m,
                InitialExchangeBAmount = 0m,
                FeesA = ExchangeFee.BitMEX_EOS,
                FeesB = ExchangeFee.Binance_EOS,
            };
            new IntertemporalPlus(config1, 1).Start();
            //new IntertemporalPlus(config2, 2).Start();
            while (true)
            {
                Console.ReadLine();
            }
        }

        static void BuildKeys()
        {
            //liuqiba910@gmail.com
            string publickey = "0jLWqkmsL0jQbCfWlL1nIGxY";
            string privatekey = "Pq6amCrncnOvSXh_-Oxf_P4hpsb-MgoBUkPUOlzH9W_p3t8C";
            CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.BitMEX, new string[2] { publickey, privatekey });
            //cuckoo@orangegrin.com
            string publickey2 = "JDNwbXiiihzY5qpRi6Z5AmHIa40baJ2EcL5rEKDfRvjhB7JRGSU8aQf4q2N69c5q";
            string privatekey2 = "rGjyCNhYaQkT9dT09OmARVM2gykTL51HhAOMC26mixRHAcdxypGLS4ApoxPwuuZG";
            CryptoUtility.SaveUnprotectedStringsToFile(ExchangeName.Binance, new string[2] { publickey2, privatekey2 });
        }
    }
}
