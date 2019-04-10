using System;
using System.Collections.Generic;
using System.Text;
using StackExchange.Redis;
using Newtonsoft.Json;

namespace cuckoo_csharp.Tools
{
    public static class RedisDB
    {
        private static ConnectionMultiplexer connection;
        private static IDatabase instance;

        public static IDatabase Instance
        {
            get
            {
                if (connection == null || !connection.IsConnected)
                {
                    connection = ConnectionMultiplexer.Connect("localhost");
                    instance = connection.GetDatabase();
                }
                return instance;
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
    }

}
