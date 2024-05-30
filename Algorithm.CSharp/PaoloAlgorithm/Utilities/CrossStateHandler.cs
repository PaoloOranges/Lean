
namespace QuantConnect.Algorithm.CSharp.PaoloAlgorithm.Utilities
{
    enum CrossStateEnum
    {
        None,
        Up,
        Down,
    }

    internal class CrossStateHandler
    {
        internal CrossStateEnum CrossState { get; private set; } = CrossStateEnum.None;

        private decimal _first = 0m;
        private decimal _second = 0m;

        internal void OnData(decimal first, decimal second)
        {
            if(_first == 0m && _second == 0m)
            {
                CrossState = CrossStateEnum.None;
            }
            else if(first > second && _first <= _second)
            {
                CrossState = CrossStateEnum.Up;
            }
            else if(first < second && _first >= _second)
            {
                CrossState = CrossStateEnum.Down;
            }

            _first = first;
            _second = second;
        }

        internal void Reset()
        {
            _first = 0m;
            _second = 0m;
            CrossState = CrossStateEnum.None;
        }
    }
}
