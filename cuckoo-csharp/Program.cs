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

namespace cuckoo_csharp
{
    class Program
    {

        public class Options
        {
            [Option('c', "configuration", Required = true, HelpText = "Set configuration file path.")]
            public string ConfigPath { get; set; }
            [Option('i', "identify", Required = true, HelpText = "Set identify")]
            public int ID { get; set; }
        }
        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args).WithParsed<Options>(OnParsedHandler);

        }

        private static void OnParsedHandler(Options op)
        {
            IntertemporalConfig config = null;
            if (File.Exists(op.ConfigPath))
            {
                string text;
                using (var streamReader = new StreamReader(op.ConfigPath, Encoding.UTF8))
                {
                    text = streamReader.ReadToEnd();
                }
                if (!string.IsNullOrEmpty(text))
                {
                    config = JsonConvert.DeserializeObject<IntertemporalConfig>(text);
                }
                else
                {
                    Console.WriteLine("can not load file :" + op.ConfigPath);
                }
            }
            if (config != null)
            {
                IntertemporalPlus it = new IntertemporalPlus(config, op.ID);
                it.Start();
                while (true)
                {
                    Console.ReadLine();
                }
            }
        }
    }
}
