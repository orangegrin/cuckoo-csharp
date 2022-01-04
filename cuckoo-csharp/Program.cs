using System;
using System.Linq;
using ExchangeSharp;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using Newtonsoft.Json;
using cuckoo_csharp.Strategy.Arbitrage;
using CommandLine;
using System.Text;
using Tools;
using System.Threading;
using StrategyHelper.Tools;

namespace BTC_Candle
{
    class Program
    {

        public class Options
        {
            [Option('c', "configuration", Required = true, HelpText = "Set configuration file path.")]
            public string ConfigPath { get; set; }
            [Option('i', "identify", Required = true, HelpText = "Set identify")]
            public int ID { get; set; }
            [Option('r', "reset", Default = false, Required = false, HelpText = "Is ResetData")]
            public bool ResetData { get; set; }
            [Option("cp", Default = false, Required = false, HelpText = "Is Clean Position")]
            public bool CleanPosition { get; set; }
        }
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(UnhandledExceptionHundle);
            if (args.Length > 0)
            {
                Parser.Default.ParseArguments<Options>(args).WithParsed<Options>(OnParsedHandler);
            }
            else
            {
                Utils.BuildingKey();
            }
        }

        private static void OnParsedHandler(Options op)
        {
            cuckoo_csharp.Strategy.Arbitrage.BTC_Candle_Correlation_Strategy.Options config = null;
            if (File.Exists(op.ConfigPath))
            {
                string text;
                using (var streamReader = new StreamReader(op.ConfigPath, Encoding.UTF8))
                {
                    text = streamReader.ReadToEnd();
                }
                if (!string.IsNullOrEmpty(text))
                {
                    config = JsonConvert.DeserializeObject<cuckoo_csharp.Strategy.Arbitrage.BTC_Candle_Correlation_Strategy.Options>(text);
                }
                else
                {
                    Console.WriteLine("data is empty :" + op.ConfigPath);
                }
            }
            if (config != null)
            {
                string logName = Path.GetFileName(op.ConfigPath).Replace(".json", "") + "_" + Path.GetFileName(config.EncryptedFileA) + "_" + op.ID;
                AddLog(logName);
                //ZigZagStrategy it = new ZigZagStrategy(config, op.ID,op.ResetData,op.CleanPosition); 4.4
                BTC_Candle_Correlation_Strategy it = new BTC_Candle_Correlation_Strategy(config, op.ID, op.ResetData);
                //ZigZagStrategyTest it = new ZigZagStrategyTest(null, op.ID);
                it.Start();
//                 while (true)
//                 {
//                     Console.ReadLine();
//                 }
                while (true)
                {
                    Thread.Sleep(1 * 1000);
                }
            }
        }
        /// <summary>
        /// 抛出未经处理异常
        /// </summary>
        /// <param name="exception"></param>
        /// <param name="isTerminating"></param>
        static private void UnhandledExceptionHundle(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Error((Exception)e.ExceptionObject);
        }

        static private void AddLog(string name)
        {
            //var config = new NLog.Config.LoggingConfiguration();
            var config = NLog.LogManager.Configuration;

            // Targets where to log to: File and Console
            var logfile = new NLog.Targets.FileTarget("logfileAll") { FileName = "log/" + name + ".log" };
            logfile.ArchiveEvery = NLog.Targets.FileArchivePeriod.Friday;
            //var logconsole = new NLog.Targets.ConsoleTarget("logconsole");

            // Rules for mapping loggers to targets            
            //config.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, logconsole);
            config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, logfile);

            // Apply config           
            NLog.LogManager.Configuration = config;
            Logger.Debug("AddLog file " + name);
        }

    }
}
