using System;
using System.Collections.Generic;
using System.Text;

namespace cuckoo_csharp.Entity
{
    public class EatData
    {
        /// <summary>
        /// 均N价差均值
        /// </summary>
        public decimal DiffAvgByLv;
        /// <summary>
        /// 均M量均值
        /// </summary>
        public decimal AmountAvgByLv;

        public override string ToString()
        {
            return $"DiffAvgByLv:{DiffAvgByLv}   AmountAvgByLv:{AmountAvgByLv}";
        }
    }

}
