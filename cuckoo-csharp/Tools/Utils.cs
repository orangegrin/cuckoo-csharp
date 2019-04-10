using System;
using System.Collections.Generic;
using System.Text;

namespace cuckoo_csharp.Tools
{
    public static class Utils
    {
        public static long Get1970ToNowMilliseconds()
        {
            return (System.DateTime.UtcNow.ToUniversalTime().Ticks - 621355968000000000) / 10000;
        }
    }
}
