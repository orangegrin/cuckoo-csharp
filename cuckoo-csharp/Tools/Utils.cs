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

        public static async Task<JObject> PostHttpReponseAsync(string fullUrl, Dictionary<string, object> payload = null, string method = null, Dictionary<string, string> header = null)
        {

            InternalHttpWebRequest internalHttpWebRequest = new InternalHttpWebRequest(new UriBuilder(fullUrl).Uri)
            {
                Method = method
            };
            if (header!=null)
            {
                foreach (var item in header)
                {
                    internalHttpWebRequest.AddHeader(item.Key, item.Value);
                }
            }
            
            HttpWebRequest request = internalHttpWebRequest.request;
            CryptoUtility.WritePayloadJsonToRequestAsync(internalHttpWebRequest, payload);
            
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
                    if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Created)
                    {
                        // 404 maybe return empty responseString
                        if (string.IsNullOrWhiteSpace(responseString))
                        {
                            throw new Exception(string.Format("{0} - {1}",
                                response.StatusCode.ConvertInvariant<int>(), response.StatusCode));
                        }
                        throw new Exception(responseString);
                    }
                    Logger.Error("responseString:::::" + responseString);
                    //RequestStateChanged?.Invoke(this, RequestMakerState.Finished, responseString);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("sdfsdfsdf:::::"+ex.Message + ex.StackTrace);
                throw ;
            }
            finally
            {
                response?.Dispose();
            }
            string stringResult = responseString;
            JObject jsonResult;
            try
            {
                
                jsonResult = JsonConvert.DeserializeObject<JObject>(stringResult);
                Logger.Debug(jsonResult.ToString());
            }
            catch (Exception ex)
            {
                Logger.Error("DDSDSDSSSDSS:::::" + ex.Message + ex.StackTrace);
                throw  ex;
            }
            return jsonResult;
        }

        public static string Post(string url, Dictionary<string, object> dic, Dictionary<string, string> header = null)
        {
            string result = "";
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
//             if (header != null)
//             {
//                 foreach (var item in header)
//                 {
//                     internalHttpWebRequest.AddHeader(item.Key, item.Value);
//                 }
//             }
            req.ContentType = "application/json";
            #region 添加Post 参数
            string body = CryptoUtility.GetJsonForPayload(dic);



            byte[] data = Encoding.UTF8.GetBytes(body.ToString());
            req.ContentLength = data.Length;
            using (Stream reqStream = req.GetRequestStream())
            {
                reqStream.Write(data, 0, data.Length);
                reqStream.Close();
            }
            #endregion
            HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
            Stream stream = resp.GetResponseStream();
            //获取响应内容
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                result = reader.ReadToEnd();
            }
            return result;
        }

        public static void PrintList<T>(this IEnumerable<T> list)
        {
            foreach (var item in list)
            {
                Logger.Debug(item.ToString());
            }
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

        /// <summary>
        /// 带权重的随机
        /// </summary>
        /// <param name="list">原始列表</param>
        /// <param name="count">随机抽取条数</param>
        /// <returns></returns>
        public static List<int> GetRandomList(List<int> list, int count) 
        {
            if (list == null || list.Count <= count || count <= 0)
            {
                return list;
            }

            //计算权重总和
            int totalWeights = 0;
            for (int i = 0; i < list.Count; i++)
            {
                totalWeights += list[i] + 1;  //权重+1，防止为0情况。
            }

            //随机赋值权重
            System.Random ran = new System.Random(GetRandomSeed());  //GetRandomSeed()随机种子，防止快速频繁调用导致随机一样的问题 
            List<KeyValuePair<int, int>> wlist = new List<KeyValuePair<int, int>>();    //第一个int为list下标索引、第一个int为权重排序值
            for (int i = 0; i < list.Count; i++)
            {
                int w = (list[i] + 1) + ran.Next(0, totalWeights);   // （权重+1） + 从0到（总权重-1）的随机数
                wlist.Add(new KeyValuePair<int, int>(i, w));
            }
            //排序
            wlist.Sort(
              delegate (KeyValuePair<int, int> kvp1, KeyValuePair<int, int> kvp2)
              {
                  return kvp2.Value - kvp1.Value;
              });

            //根据实际情况取排在最前面的几个
            List<int> newList = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int entiy = wlist[i].Key;
                newList.Add(entiy);
            }
            //随机法则
            return newList;
        }
        /// <summary>
        /// 随机种子值
        /// </summary>
        /// <returns></returns>
        private static int GetRandomSeed()
        {
            byte[] bytes = new byte[4];
            System.Security.Cryptography.RNGCryptoServiceProvider rng = new System.Security.Cryptography.RNGCryptoServiceProvider();
            rng.GetBytes(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }

       



        public class InternalHttpWebRequest : IHttpWebRequest
        {
            internal readonly HttpWebRequest request;

            public InternalHttpWebRequest(Uri fullUri)
            {
                request = HttpWebRequest.Create(fullUri) as HttpWebRequest;
                request.KeepAlive = false;
            }

            public void AddHeader(string header, string value)
            {
                switch (header.ToStringLowerInvariant())
                {
                    case "content-type":
                        request.ContentType = value;
                        break;

                    case "content-length":
                        request.ContentLength = value.ConvertInvariant<long>();
                        break;

                    case "user-agent":
                        request.UserAgent = value;
                        break;

                    case "accept":
                        request.Accept = value;
                        break;

                    case "connection":
                        request.Connection = value;
                        break;

                    default:
                        request.Headers[header] = value;
                        break;
                }
            }

            public Uri RequestUri
            {
                get { return request.RequestUri; }
            }

            public string Method
            {
                get { return request.Method; }
                set { request.Method = value; }
            }

            public int Timeout
            {
                get { return request.Timeout; }
                set { request.Timeout = value; }
            }

            public int ReadWriteTimeout
            {
                get { return request.ReadWriteTimeout; }
                set { request.ReadWriteTimeout = value; }
            }

            public async Task WriteAllAsync(byte[] data, int index, int length)
            {
                try
                {
                    if (request.Method == "GET")
                    {
                        request.Method = "POST";
                        using (Stream stream = await request.GetRequestStreamAsync())
                        {
                            await stream.WriteAsync(data, 0, data.Length);
                        }
                        request.Method = "GET";
                    }
                    else
                    {
                        using (Stream stream = await request.GetRequestStreamAsync())
                        {
                            await stream.WriteAsync(data, 0, data.Length);
                        }
                    }

                    //                     Stream stream = await request.GetRequestStreamAsync();
                    //                     await stream.WriteAsync(data, 0, data.Length);
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex.ToString());
                    throw ex;
                }

            }
        }
    }
}