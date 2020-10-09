using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace cuckoo_csharp.Tools
{
    public class RedisDBSubscriber
    {
        private ConnectionMultiplexer connection;
        private IDatabase instance;
        private string configStr = null;


        private Action<string> callBack;
        public RedisDBSubscriber(string configStr)
        {
            ThreadPool.SetMinThreads(200, 200);
            Init(configStr);
        }

        public IDatabase Database
        {
            get
            {
                if (connection == null || !connection.IsConnected)
                {
                    if (configStr == null || configStr.Equals(string.Empty))
                        throw new Exception("请先调用 Init");
                    Init(configStr);
                }
                return instance;
            }
        }
        public void Init(string configStr)
        {
            this.configStr = configStr;
            if (connection == null || !connection.IsConnected)
            {
                connection = ConnectionMultiplexer.Connect(configStr);
                instance = connection.GetDatabase();
            }
        }

        public T StringGet<T>(IDatabase database, RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            string res = database.StringGet(key, flags);
            if (res == null)
                return default(T);
            return JsonConvert.DeserializeObject<T>(res);
        }
        public  bool StringSet(IDatabase database, RedisKey key, object value, TimeSpan? expiry = null, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            var val = JsonConvert.SerializeObject(value);
            return database.StringSet(key, val, expiry, when, flags);
        }
        public void SubScribe(string cnl,Action<string> callBack )
        {
            this.callBack = callBack;
            Console.WriteLine("主线程：" + Thread.CurrentThread.ManagedThreadId);
            var sub = connection.GetSubscriber();
            sub.Subscribe(cnl, SubHandel);
            Console.WriteLine("订阅了一个频道：" + cnl);
        }
        public void SubHandel(RedisChannel cnl, RedisValue val)
        {
            Console.WriteLine();
            Console.WriteLine("频道：" + cnl + "\t收到消息:" + val); ;
            Console.WriteLine("线程：" + Thread.CurrentThread.ManagedThreadId + ",是否线程池：" + Thread.CurrentThread.IsThreadPoolThread);
            if (val == "close")
                connection.GetSubscriber().Unsubscribe(cnl);
            if (val == "closeall")
                connection.GetSubscriber().UnsubscribeAll();
            else
            {
                callBack(val);
            }

        }
    }
}
