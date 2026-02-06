using System;
using System.Collections.Generic;

namespace CoreSim.Utils
{
    /// <summary>
    /// Deterministic RNG wrapper. Use this everywhere instead of UnityEngine.Random.
    /// </summary>
    public sealed class DeterministicRng
    {
        private readonly Random _random;

        public int Seed { get; }

        public DeterministicRng(int seed)
        {
            Seed = seed;
            _random = new Random(seed);
        }

        /// <summary>
        /// Returns an int in [minInclusive, maxExclusive).
        /// </summary>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            return _random.Next(minInclusive, maxExclusive);
        }

        /// <summary>
        /// Returns a float in [0, 1).
        /// </summary>
        public float NextFloat01()
        {
            return (float)_random.NextDouble();
        }

        /// <summary>
        /// Returns a float in [minInclusive, maxInclusive).
        /// </summary>
        public float NextFloat(float minInclusive, float maxInclusive)
        {
            return minInclusive + (maxInclusive - minInclusive) * NextFloat01();
        }

        /// <summary>
        /// Returns true with probability p (clamped).
        /// </summary>
        public bool NextBool(float pTrue)
        {
            if (pTrue <= 0f) return false;
            if (pTrue >= 1f) return true;
            return NextFloat01() < pTrue;
        }

        /// <summary>
        /// Fisher-Yates shuffle (in place).
        /// </summary>
        public void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = NextInt(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// Picks a random element from a non-empty list.
        /// </summary>
        public T Pick<T>(IList<T> list)
        {
            if (list == null || list.Count == 0)
                throw new ArgumentException("Cannot pick from an empty list.");

            return list[NextInt(0, list.Count)];
        }
    }
}