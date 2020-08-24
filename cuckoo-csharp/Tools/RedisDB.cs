using System;
using System.Collections.Generic;
using System.Text;
using StackExchange.Redis;
using Newtonsoft.Json;
using System.Threading;

namespace cuckoo_csharp.Tools
{
    public static class RedisDB
    {
        private static ConnectionMultiplexer connection;
        private static IDatabase instance;
        private static string configStr = null;

        static RedisDB()
        {
            ThreadPool.SetMinThreads(200, 200);
        }

        public static IDatabase Instance
        {
            get
            {
                if (connection == null || !connection.IsConnected)
                {
                    if (RedisDB.configStr == null || RedisDB.configStr.Equals(string.Empty))
                        throw new Exception("请先调用 Init");
                    Init(RedisDB.configStr);
                }
                return instance;
            }
        }
        public static void Init(string configStr)
        {
            RedisDB.configStr = configStr;
            if (connection == null || !connection.IsConnected)
            {
                connection = ConnectionMultiplexer.Connect(RedisDB.configStr); 
                instance = connection.GetDatabase();
            }
        }


        public static T StringGet<T>(this IDatabase database, RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            string res = database.StringGet(key, flags);
            if (res == null)
                return default(T);
            return JsonConvert.DeserializeObject<T>(res);
        }
        public static bool StringSet(this IDatabase database, RedisKey key, object value, TimeSpan? expiry = null, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            var val = JsonConvert.SerializeObject(value);
            return database.StringSet(key, val, expiry, when, flags);
        }
        public static void SubScribe(string cnl)
        {
            Console.WriteLine("主线程：" + Thread.CurrentThread.ManagedThreadId);
            var sub = connection.GetSubscriber();
            sub.Subscribe(cnl, SubHandel);
            Console.WriteLine("订阅了一个频道：" + cnl);
        }
        public static void SubHandel(RedisChannel cnl, RedisValue val)
        {
            Console.WriteLine();
            Console.WriteLine("频道：" + cnl + "\t收到消息:" + val); ;
            Console.WriteLine("线程：" + Thread.CurrentThread.ManagedThreadId + ",是否线程池：" + Thread.CurrentThread.IsThreadPoolThread);
            if (val == "close")
                connection.GetSubscriber().Unsubscribe(cnl);
            if (val == "closeall")
                connection.GetSubscriber().UnsubscribeAll();
        }
    }

}
