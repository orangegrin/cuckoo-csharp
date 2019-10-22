using ExchangeSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp;
using System.Linq;

namespace cuckoo_csharp.Tools
{
    public static class Utils
    {
        public static long Get1970ToNowMilliseconds()
        {
            return (System.DateTime.UtcNow.ToUniversalTime().Ticks - 621355968000000000) / 10000;
        }

        public static void BuildingKey()
        {
            Console.WriteLine("Please enter the file name.");
            string fileName = Console.ReadLine();
            Console.WriteLine("Please enter the private key.");
            string privateKey = Console.ReadLine();
            Console.WriteLine("Please enter the public key.");
            string publicKey = Console.ReadLine();
            CryptoUtility.SaveUnprotectedStringsToFile(fileName, new string[2] { publicKey, privateKey });
        }
        /// <summary>
        /// 获取http返回json
        /// </summary>
        public static async Task<JObject> GetHttpReponseAsync(string fullUrl = "www.baidu.com")
        {

            HttpWebRequest request = HttpWebRequest.Create(new UriBuilder(fullUrl).Uri) as HttpWebRequest;
            request.KeepAlive = false;
            HttpWebResponse response = null;
            string responseString = null;
            try
            {
                try
                {

                    response = await request.GetResponseAsync() as HttpWebResponse;
                    if (response == null)
                    {
                        throw new Exception("Unknown response from server");
                    }
                }
                catch (WebException we)
                {
                    response = we.Response as HttpWebResponse;
                    if (response == null)
                    {
                        throw new Exception(we.Message ?? "Unknown response from server");
                    }
                }
                using (Stream responseStream = response.GetResponseStream())
                {
                    responseString = new StreamReader(responseStream).ReadToEnd();
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        // 404 maybe return empty responseString
                        if (string.IsNullOrWhiteSpace(responseString))
                        {
                            throw new Exception(string.Format("{0} - {1}",
                                response.StatusCode.ConvertInvariant<int>(), response.StatusCode));
                        }
                        throw new Exception(responseString);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message + ex.StackTrace);
                throw;
            }
            finally
            {
                response?.Dispose();
            }
            
            string stringResult = responseString;
            JObject jsonResult = JsonConvert.DeserializeObject<JObject>(stringResult);
            Console.WriteLine(jsonResult);
            return jsonResult;
        }
        /// <summary>
        /// 写入csv格式数据
        /// </summary>
        /// <param name="tabDataList"></param>
        /// <param name="fullPath"></param>
        /// <param name="removeBefore"></param>
        public static void AppendCSV(List<List<string>> tabDataList, string fullPath, bool removeBefore = true)//table数据写入csv  
        {
            System.IO.FileInfo fi = new System.IO.FileInfo(fullPath);
            if (!fi.Directory.Exists)
            {
                fi.Directory.Create();
            }
            FileMode fm = removeBefore ? FileMode.Create : FileMode.Append;
            System.IO.FileStream fs = new System.IO.FileStream(fullPath, fm,
              System.IO.FileAccess.Write);
            System.IO.StreamWriter sw = new System.IO.StreamWriter(fs, System.Text.Encoding.UTF8);
            string data = "";
            try
            {

                for (int i = 0; i < tabDataList.Count; i++) //写入各行数据  
                {
                    List<string> rowData = tabDataList[i];
                    data = "";
                    for (int j = 0; j < rowData.Count; j++)
                    {
                        string str = rowData[j].ToString();
                        str = str.Replace("\"", "\"\"");//替换英文冒号 英文冒号需要换成两个冒号  
                        if (str.Contains(',') || str.Contains('"')
                            || str.Contains('\r') || str.Contains('\n')) //含逗号 冒号 换行符的需要放到引号中  
                        {
                            str = string.Format("\"{0}\"", str);
                        }
                        data += str;
                        if (j < rowData.Count - 1)
                        {
                            data += ",";
                        }
                    }
                    sw.WriteLine(data);
                }
                sw.Close();
                fs.Close();
            }
            catch (System.Exception ex)
            {

                throw ex;
            }

        }
        /// <summary>
        /// 获取格林威治时间
        /// </summary>
        /// <param name="dt"></param>
        /// <returns></returns>
        public static long GetGMTimeTicks(DateTime dt)
        {
            return (dt.ToUniversalTime().Ticks - 621355968000000000) / 10000000;
        }

        public static string Str2Json(params object[] args)
        {
            StringBuilder jsStr = new StringBuilder("");
            try
            {
                if (args.Length == 0 || args.Length % 2 == 1)
                    return jsStr.ToString();
                jsStr.Append("{");
                for (int i = 0; i < args.Length; i += 2)
                {
                    jsStr.Append($"\"{args[i]}\":{args[i + 1]}");
                    jsStr.Append(",");
                }
                jsStr.Append("}");
                
            }
            catch (Exception)
            {

                Logger.Error("Str2Json error");
            }
            return jsStr.ToString();
        }
        /// <summary> 
        /// Calculates Exponential Moving Average (EMA) indicator 
        /// </summary> 
        /// <param name="input">Input signal</param> 
        /// <param name="period">Number of periods</param> 
        /// <returns>Object containing operation results</returns> 
        public static List<decimal> EMA(IEnumerable<decimal> input, int period)
        {
            var returnValues = new List<decimal>();

            //decimal multiplier = (2.0 / (period + 1));
            decimal initialSMA = input.Take(period).Average();

            returnValues.Add(initialSMA);

            var copyInputValues = input.ToList();

            for (int i = period; i < copyInputValues.Count; i++)
            {
                var resultValue = (returnValues[i - period] * period - copyInputValues[i - period] + copyInputValues[i]) / period;
                //var resultValue = (copyInputValues[i] - returnValues.Last()) * multiplier + returnValues.Last();

                returnValues.Add(resultValue);
            }
            return returnValues;
        }

    }


        
}