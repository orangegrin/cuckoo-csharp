using ExchangeSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

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
    }
}
