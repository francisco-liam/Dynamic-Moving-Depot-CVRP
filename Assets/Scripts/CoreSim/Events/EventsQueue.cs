#nullable enable
using System.Collections.Generic;

namespace CoreSim.Events
{
    public sealed class EventQueue
    {
        private readonly List<SimEvent> _events = new List<SimEvent>();

        public int Count => _events.Count;

        public void Enqueue(SimEvent e)
        {
            // Insert in sorted order by time (stable)
            int lo = 0;
            int hi = _events.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_events[mid].Time <= e.Time) lo = mid + 1;
                else hi = mid;
            }
            _events.Insert(lo, e);
        }

        public bool TryPeek(out SimEvent e)
        {
            if (_events.Count == 0) { e = default; return false; }
            e = _events[0];
            return true;
        }

        public bool TryDequeue(out SimEvent e)
        {
            if (_events.Count == 0) { e = default; return false; }
            e = _events[0];
            _events.RemoveAt(0);
            return true;
        }

        public void Clear() => _events.Clear();

        public List<SimEvent> ToList() => new List<SimEvent>(_events);
    }
}