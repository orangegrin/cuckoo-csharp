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
            Intertemporal.Options config = null;
            if (File.Exists(op.ConfigPath))
            {
                string text;
                using (var streamReader = new StreamReader(op.ConfigPath, Encoding.UTF8))
                {
                    text = streamReader.ReadToEnd();
                }
                if (!string.IsNullOrEmpty(text))
                {
                    config = JsonConvert.DeserializeObject<Intertemporal.Options>(text);
                }
                else
                {
                    Console.WriteLine("data is empty :" + op.ConfigPath);
                }
            }
            if (config != null)
            {
                Intertemporal it = new Intertemporal(config, op.ID);
                it.Start();
                while (true)
                {
                    Console.ReadLine();
                }
            }
        }
    }
}
