using ExchangeSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace cuckoo_csharp.Tools
{
    public class BollingerBandsPoint
    {
        public decimal? UpperBandPrice;
        public decimal? LowerBandPrice;


        private  decimal _numStddevs;
        private  int _numPeriods;

        private decimal _currentSum;
        

        public BollingerBandsPoint()
        {
           
        }

        /// <summary>
        /// recalculate the upper and lower bands
        /// </summary>
        private void Recalculate(List<MarketCandle> _prices)
        {// add the newest price
            _currentSum = _prices.Select(price => price.ClosePrice).Sum();

            

            // recalculate the Bollinger band
            if (_prices.Count < _numPeriods) return;

            // calculate the mean
            decimal mean = _currentSum / _numPeriods;

            // calculate the sum of the differences
            decimal variance = _prices.Select(price => price.ClosePrice - mean).Select(diff => diff * diff).Sum() / _numPeriods;
            decimal stddev = Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(variance)));

            UpperBandPrice = mean + _numStddevs * stddev;
            LowerBandPrice = mean - _numStddevs * stddev;
        }


        public void Exe(int numPeriods, decimal numStddevs, List<MarketCandle> prices)
        {
            _numPeriods = numPeriods;
            _numStddevs = numStddevs;
            if (prices.Count>_numPeriods)
            {
                prices.RemoveRange(0, prices.Count - _numPeriods);
            }
            if (prices.Count<_numPeriods)
            {
                throw new Exception("candle num must bigger than mumPeriods!  count:"+ prices.Count);
            }

            Recalculate(prices);
        }
    }
}
