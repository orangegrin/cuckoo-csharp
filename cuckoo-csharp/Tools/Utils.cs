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
    }
}