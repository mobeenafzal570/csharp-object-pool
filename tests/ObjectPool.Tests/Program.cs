using System;
using Mobeen.Pooling;

internal static class Program
{
    private sealed class Item { public int Value; }
    private static int tests;
    private static int failures;
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Test(string name, Action action)
    {
        tests++;
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
    private static int Main()
    {
        Test("rent creates and tracks an active lease", () =>
        {
            using var pool = new ObjectPool<Item>(() => new Item());
            using var lease = pool.Rent();
            Check(lease.Value != null && pool.ActiveCount == 1 && pool.CreatedCount == 1);
        });
        Test("returned objects are reused", () =>
        {
            using var pool = new ObjectPool<Item>(() => new Item());
            var first = pool.Rent(); var value = first.Value; first.Dispose();
            using var second = pool.Rent();
            Check(ReferenceEquals(value, second.Value) && pool.CreatedCount == 1);
        });
        Test("reset occurs before an object is reused", () =>
        {
            using var pool = new ObjectPool<Item>(() => new Item(), reset: i => i.Value = 0);
            var first = pool.Rent(); first.Value.Value = 9; first.Dispose();
            using var second = pool.Rent(); Check(second.Value.Value == 0);
        });
        Test("lease disposal is idempotent", () =>
        {
            using var pool = new ObjectPool<Item>(() => new Item());
            var lease = pool.Rent(); lease.Dispose(); lease.Dispose();
            Check(pool.ActiveCount == 0 && pool.RetainedCount == 1 && lease.IsReturned);
        });
        Test("retention capacity discards overflow", () =>
        {
            int discarded = 0;
            using var pool = new ObjectPool<Item>(() => new Item(), 1, destroy: i => discarded++);
            var a = pool.Rent(); var b = pool.Rent(); a.Dispose(); b.Dispose();
            Check(pool.RetainedCount == 1 && discarded == 1 && pool.ActiveCount == 0);
        });
        Test("zero capacity retains nothing", () =>
        {
            int discarded = 0;
            using var pool = new ObjectPool<Item>(() => new Item(), 0, destroy: i => discarded++);
            pool.Rent().Dispose(); Check(pool.RetainedCount == 0 && discarded == 1);
        });
        Test("a returned lease rejects access", () =>
        {
            using var pool = new ObjectPool<Item>(() => new Item());
            var lease = pool.Rent(); lease.Dispose();
            Throws<ObjectDisposedException>(() => { var value = lease.Value; });
        });
        Test("disposed pools reject new rentals", () =>
        {
            var pool = new ObjectPool<Item>(() => new Item()); pool.Dispose();
            Throws<ObjectDisposedException>(() => pool.Rent());
        });
        Test("pool disposal cleans retained objects once", () =>
        {
            int discarded = 0;
            var pool = new ObjectPool<Item>(() => new Item(), 2, destroy: i => discarded++);
            var a = pool.Rent(); var b = pool.Rent(); a.Dispose(); b.Dispose();
            pool.Dispose(); pool.Dispose();
            Check(pool.IsDisposed && pool.RetainedCount == 0 && discarded == 2);
        });
        Test("active leases outlive pool disposal until their return", () =>
        {
            int discarded = 0, resets = 0;
            var pool = new ObjectPool<Item>(() => new Item(), reset: i => resets++, destroy: i => discarded++);
            var lease = pool.Rent(); pool.Dispose(); lease.Value.Value = 7;
            Check(pool.ActiveCount == 1); lease.Dispose();
            Check(pool.ActiveCount == 0 && discarded == 1 && resets == 0);
        });
        Test("failed reset discards rather than caching a dirty object", () =>
        {
            int discarded = 0;
            using var pool = new ObjectPool<Item>(() => new Item(),
                reset: i => throw new InvalidOperationException("reset"), destroy: i => discarded++);
            var lease = pool.Rent(); Throws<InvalidOperationException>(() => lease.Dispose());
            Check(lease.IsReturned && pool.ActiveCount == 0 && pool.RetainedCount == 0 && discarded == 1);
        });
        Test("failed factory leaves counters unchanged", () =>
        {
            using var pool = new ObjectPool<Item>(() => throw new InvalidOperationException());
            Throws<InvalidOperationException>(() => pool.Rent());
            Check(pool.ActiveCount == 0 && pool.CreatedCount == 0);
        });
        Test("null factory results are rejected", () =>
        {
            using var pool = new ObjectPool<Item>(() => null);
            Throws<InvalidOperationException>(() => pool.Rent()); Check(pool.ActiveCount == 0);
        });
        Test("duplicate factory instances do not destroy an active object", () =>
        {
            var shared = new Item(); int discarded = 0;
            using var pool = new ObjectPool<Item>(() => shared, destroy: i => discarded++);
            using var lease = pool.Rent();
            Throws<InvalidOperationException>(() => pool.Rent());
            Check(pool.ActiveCount == 1 && pool.CreatedCount == 1 && discarded == 0);
        });
        Test("reset and cleanup failures are both reported", () =>
        {
            using var pool = new ObjectPool<Item>(() => new Item(),
                reset: i => throw new InvalidOperationException(), destroy: i => throw new ArgumentException());
            var lease = pool.Rent(); var error = Throws<AggregateException>(() => lease.Dispose());
            Check(error.InnerExceptions.Count == 2 && pool.ActiveCount == 0 && pool.RetainedCount == 0);
        });
        Test("dispose attempts every retained cleanup even when callbacks fail", () =>
        {
            int attempts = 0;
            var pool = new ObjectPool<Item>(() => new Item(), destroy: i => { attempts++; throw new Exception(); });
            var a = pool.Rent(); var b = pool.Rent(); a.Dispose(); b.Dispose();
            var error = Throws<AggregateException>(() => pool.Dispose());
            Check(attempts == 2 && error.InnerExceptions.Count == 2 && pool.RetainedCount == 0);
        });
        Test("same-pool callback reentrancy is rejected", () =>
        {
            ObjectPool<Item> pool = null;
            pool = new ObjectPool<Item>(() => new Item(), reset: i => pool.Rent());
            var lease = pool.Rent(); Throws<InvalidOperationException>(() => lease.Dispose());
            Check(pool.ActiveCount == 0 && pool.RetainedCount == 0); pool.Dispose();
        });
        Test("rejected nested return leaves the other lease valid", () =>
        {
            ObjectPool<Item>.Lease other = null; bool nested = true;
            using var pool = new ObjectPool<Item>(() => new Item(), reset: i => { if (nested) other.Dispose(); });
            var first = pool.Rent(); other = pool.Rent();
            Throws<InvalidOperationException>(() => first.Dispose());
            Check(first.IsReturned && !other.IsReturned && pool.ActiveCount == 1);
            nested = false; other.Dispose(); Check(pool.ActiveCount == 0 && pool.RetainedCount == 1);
        });
        Test("constructor validates configuration", () =>
        {
            Throws<ArgumentNullException>(() => new ObjectPool<Item>(null));
            Throws<ArgumentOutOfRangeException>(() => new ObjectPool<Item>(() => new Item(), -1));
        });
        Console.WriteLine($"{tests - failures}/{tests} tests passed");
        return failures == 0 ? 0 : 1;
    }
}
