
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.PaoloAlgorithm.Utilities
{

    //TODO move with array
    internal class FixedCircularBuffer<T>
    {
        public int Capacity {  get; private set; }
        public int Length { get => _values.Count; }

        private Queue<T> _values = new Queue<T>();

        internal FixedCircularBuffer(int capacity)
        {
            Capacity = capacity;
        }

        internal void Push(T data)
        {
            _values.Enqueue(data);
            while (Length > Capacity)
            {
                _values.Dequeue();
            }
        }

        internal T Pop()
        {
            return _values.Dequeue();
        }        

        internal T[] ToArray()
        {
            return _values.ToArray();
        }
    }
}
