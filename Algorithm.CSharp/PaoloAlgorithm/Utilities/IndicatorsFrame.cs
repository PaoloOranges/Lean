
namespace QuantConnect.Algorithm.CSharp.PaoloAlgorithm.Utilities
{

    internal struct IndicatorsFrame
    {
        public decimal veryFastMA;
        public decimal fastMA;
        public decimal slowMA;
        public decimal MACD;
        public decimal MACDSignal;
        public decimal MACDHistogram;
        public decimal ADXPlus;
        public decimal ADXMinus;
        public decimal Volume;
        public decimal BBLow;
        public decimal BBMid;
        public decimal BBUp;
    }
}
