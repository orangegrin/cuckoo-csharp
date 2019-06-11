using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace cuckoo_csharp.Tools
{
    public static class Utils
    {
        public static long Get1970ToNowMilliseconds()
        {
            return (System.DateTime.UtcNow.ToUniversalTime().Ticks - 621355968000000000) / 10000;
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
        public static string Str2Json(params object[] args)
        {
            StringBuilder jsStr = new StringBuilder("");
            if (args.Length == 0 || args.Length % 2 == 1)
                return jsStr.ToString();
            jsStr.Append("{");
            for (int i = 0; i < args.Length; i += 2)
            {
                jsStr.Append($"\"{args[i]}\":{args[i + 1]}");
                jsStr.Append(",");
            }
            jsStr.Append("}");
            return jsStr.ToString();
        }
    }
}
