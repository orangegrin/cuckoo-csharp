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
            if (args.Length > 0)
            {
                Parser.Default.ParseArguments<Options>(args).WithParsed<Options>(OnParsedHandler);
            }
            else
            {
                BuildingKey();
            }
        }
        private static void BuildingKey()
        {
            Console.WriteLine("Please enter the file name.");
            string fileName = Console.ReadLine();
            Console.WriteLine("Please enter the private key.");
            string privateKey = Console.ReadLine();
            Console.WriteLine("Please enter the public key.");
            string publicKey = Console.ReadLine();
            CryptoUtility.SaveUnprotectedStringsToFile(fileName, new string[2] { publicKey, privateKey });
        }

        private static void OnParsedHandler(Options op)
        {
            Unilateral.Options config = null;
            if (File.Exists(op.ConfigPath))
            {
                string text;
                using (var streamReader = new StreamReader(op.ConfigPath, Encoding.UTF8))
                {
                    text = streamReader.ReadToEnd();
                }
                if (!string.IsNullOrEmpty(text))
                {
                    config = JsonConvert.DeserializeObject<Unilateral.Options>(text);
                }
                else
                {
                    Console.WriteLine("data is empty :" + op.ConfigPath);
                }
            }
            if (config != null)
            {
                Unilateral it = new Unilateral(config, op.ID);
                it.Start();
                while (true)
                {
                    Console.ReadLine();
                }
            }
        }
    }
}
