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
using cuckoo_csharp.Tools;
using System.Threading;

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
            //getAsync();
            //while (true)
            //{
            //    Thread.Sleep(1 * 1000);
            //}

            //return;
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
            P2FAdvance.Options config = null;
            if (File.Exists(op.ConfigPath))
            {
                string text;
                using (var streamReader = new StreamReader(op.ConfigPath, Encoding.UTF8))
                {
                    text = streamReader.ReadToEnd();
                }
                if (!string.IsNullOrEmpty(text))
                {
                    config = JsonConvert.DeserializeObject<P2FAdvance.Options>(text);
                }
                else
                {
                    Console.WriteLine("data is empty :" + op.ConfigPath);
                }
            }
            if (config != null)
            {
                P2FAdvance it = new P2FAdvance(config, op.ID);
                it.Start();
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


        private static async Task getAsync()
        {             //List<string> symoblList = new List<string>() { "ETHU19", "ETHUSD", "XBTUSD" };
            List<string> symoblList = new List<string>() { "ETHUSD", "XBTUSD" };
            FileStream toStream = null;
            foreach (string symobl in symoblList)
            {
                StringBuilder sb = new StringBuilder();
                List<string> pathList = null;
                pathList = new List<string>(Directory.GetFiles(@"C:\Users\87474\Pictures\河洛\qute", "*.csv"));
                string fileInfo = pathList[0];
                
                if (!Directory.Exists(Path.GetDirectoryName(fileInfo) + @"\Advance\"))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fileInfo) + @"\Advance\");
                }
                string toPath = Path.GetDirectoryName(fileInfo) + @"\Advance\" + symobl + Path.GetExtension(fileInfo);
                foreach (string fi in pathList)
                {
                    
                    await Utils.GetExchangeOrderBook(fi, toPath, symobl,sb);
                    //Thread.Sleep(2 * 1000);

                }
                toStream = new FileStream(toPath, FileMode.OpenOrCreate, FileAccess.Write);
                StreamWriter streamWriter = new StreamWriter(toStream);
                await streamWriter.WriteAsync(sb.ToString());

                await streamWriter.FlushAsync();
                await toStream.FlushAsync();
                streamWriter.Close();
                toStream.Close();
            }
        }
    }
}
