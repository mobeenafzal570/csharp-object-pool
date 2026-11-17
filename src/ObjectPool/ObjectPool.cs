using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Mobeen.Pooling
{
    /// <summary>A single-threaded pool with bounded retention and explicit lease ownership.</summary>
    public sealed class ObjectPool<T> : IDisposable where T : class
    {
        private readonly Func<T> factory;
        private readonly Action<T> reset;
        private readonly Action<T> destroy;
        private readonly Stack<T> retained = new Stack<T>();
        private readonly HashSet<T> owned = new HashSet<T>(new IdentityComparer());
        private bool disposed;
        private bool busy;

        public ObjectPool(Func<T> factory, int maxRetained = 32,
            Action<T> reset = null, Action<T> destroy = null)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            if (maxRetained < 0) throw new ArgumentOutOfRangeException(nameof(maxRetained));
            MaxRetained = maxRetained;
            this.reset = reset;
            this.destroy = destroy;
        }

        public int MaxRetained { get; }
        public int ActiveCount { get; private set; }
        public int RetainedCount => retained.Count;
        public int CreatedCount { get; private set; }
        public bool IsDisposed => disposed;

        public Lease Rent()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ObjectPool<T>));
            Guard();
            busy = true;
            try
            {
                T value;
                if (retained.Count > 0) value = retained.Pop();
                else
                {
                    value = factory();
                    if (ReferenceEquals(value, null))
                        throw new InvalidOperationException("The factory returned null.");
                    if (!owned.Add(value))
                        throw new InvalidOperationException("The factory returned an object already owned by this pool.");
                    CreatedCount++;
                }
                var lease = new Lease(this, value);
                ActiveCount++;
                return lease;
            }
            finally { busy = false; }
        }

        private void Return(Lease lease)
        {
            Guard();
            busy = true;
            try
            {
                T value = lease.Detach();
                ActiveCount--;
                if (disposed) { Discard(value); return; }
                try { reset?.Invoke(value); }
                catch (Exception original)
                {
                    try { Discard(value); }
                    catch (Exception cleanup)
                    { throw new AggregateException("Reset and cleanup both failed.", original, cleanup); }
                    throw;
                }
                if (retained.Count < MaxRetained) retained.Push(value);
                else Discard(value);
            }
            finally { busy = false; }
        }

        private void Discard(T value)
        {
            owned.Remove(value);
            destroy?.Invoke(value);
        }

        /// <summary>Dispose retained objects now. Active leases remain valid until returned.</summary>
        public void Dispose()
        {
            if (disposed) return;
            Guard();
            disposed = true;
            busy = true;
            var failures = new List<Exception>();
            try
            {
                while (retained.Count > 0)
                {
                    try { Discard(retained.Pop()); }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            finally { busy = false; }
            if (failures.Count > 0)
                throw new AggregateException("One or more retained objects could not be cleaned up.", failures);
        }

        private void Guard()
        {
            if (busy) throw new InvalidOperationException("Pool callbacks cannot re-enter the same pool.");
        }

        private sealed class IdentityComparer : IEqualityComparer<T>
        {
            public bool Equals(T left, T right) => ReferenceEquals(left, right);
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }

        public sealed class Lease : IDisposable
        {
            private ObjectPool<T> owner;
            private readonly T value;

            internal Lease(ObjectPool<T> owner, T value)
            { this.owner = owner; this.value = value; }

            public T Value => owner != null ? value : throw new ObjectDisposedException(nameof(Lease));
            public bool IsReturned => owner == null;

            internal T Detach() { owner = null; return value; }

            public void Dispose()
            {
                // Return validates reentrancy before detaching, so a rejected nested
                // return leaves this lease valid and available for a later retry.
                if (owner != null) owner.Return(this);
            }
        }
    }
}
