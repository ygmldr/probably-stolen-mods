using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace CompactSort.Tests
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class TestAttribute : Attribute
    {
        public readonly string Name;

        public TestAttribute(string name = null)
        {
            Name = name;
        }
    }

    internal sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    internal static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new AssertionException(message);
        }

        public static void False(bool condition, string message)
        {
            if (condition) throw new AssertionException(message);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertionException(message + " (expected=" + expected + ", actual=" + actual + ")");
        }

        public static void NotEqual<T>(T unexpected, T actual, string message)
        {
            if (EqualityComparer<T>.Default.Equals(unexpected, actual))
                throw new AssertionException(message + " (unexpected=" + unexpected + ")");
        }

        public static void NotNull(object value, string message)
        {
            if (value == null) throw new AssertionException(message + " (was null)");
        }

        public static void Same(object expected, object actual, string message)
        {
            if (!ReferenceEquals(expected, actual))
                throw new AssertionException(message + " (references differ)");
        }

        public static void InRange(int value, int min, int max, string message)
        {
            if (value < min || value > max)
                throw new AssertionException(message + " (value=" + value + ", range=" + min + ".." + max + ")");
        }

        public static void LessOrEqual(long value, long max, string message)
        {
            if (value > max)
                throw new AssertionException(message + " (value=" + value + ", max=" + max + ")");
        }

        public static void GreaterOrEqual(long value, long min, string message)
        {
            if (value < min)
                throw new AssertionException(message + " (value=" + value + ", min=" + min + ")");
        }

        public static void SequenceEqual<T>(IList<T> expected, IList<T> actual, string message)
        {
            if (expected == null || actual == null || expected.Count != actual.Count)
                throw new AssertionException(message + " (count " +
                    (expected == null ? "null" : expected.Count.ToString()) + " vs " +
                    (actual == null ? "null" : actual.Count.ToString()) + ")");
            for (int i = 0; i < expected.Count; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
                    throw new AssertionException(message + " (index=" + i + ", expected=" + expected[i] +
                                                 ", actual=" + actual[i] + ")");
            }
        }
    }

    internal static class TestRunner
    {
        public static int Main(string[] args)
        {
            var tests = typeof(TestRunner).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length > 0)
                .OrderBy(m => m.DeclaringType == null ? "" : m.DeclaringType.Name)
                .ThenBy(m => m.Name)
                .ToList();

            string filter = args != null && args.Length > 0 ? args[0] : null;
            if (!string.IsNullOrEmpty(filter))
            {
                tests = tests.Where(m =>
                    m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (m.DeclaringType != null &&
                     m.DeclaringType.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            }

            Console.WriteLine("TDD suite: " + tests.Count + " test(s)" +
                              (string.IsNullOrEmpty(filter) ? "" : ", filter='" + filter + "'"));
            Console.WriteLine();

            int passed = 0;
            int failed = 0;
            var total = Stopwatch.StartNew();
            foreach (var test in tests)
            {
                var attr = (TestAttribute)test.GetCustomAttributes(typeof(TestAttribute), false)[0];
                string name = string.IsNullOrEmpty(attr.Name) ? test.Name : attr.Name;
                var sw = Stopwatch.StartNew();
                try
                {
                    test.Invoke(null, null);
                    sw.Stop();
                    passed++;
                    Console.WriteLine("PASS  " + name + "  (" + sw.ElapsedMilliseconds + " ms)");
                }
                catch (TargetInvocationException e)
                {
                    sw.Stop();
                    failed++;
                    var inner = e.InnerException ?? e;
                    Console.WriteLine("FAIL  " + name + "  (" + sw.ElapsedMilliseconds + " ms)");
                    Console.WriteLine("      " + inner.GetType().Name + ": " + inner.Message);
                    if (!(inner is AssertionException))
                        Console.WriteLine(inner.StackTrace);
                }
                catch (Exception e)
                {
                    sw.Stop();
                    failed++;
                    Console.WriteLine("FAIL  " + name + "  (" + sw.ElapsedMilliseconds + " ms)");
                    Console.WriteLine("      " + e.GetType().Name + ": " + e.Message);
                    Console.WriteLine(e.StackTrace);
                }
            }

            total.Stop();
            Console.WriteLine();
            Console.WriteLine("Result: " + passed + " passed, " + failed + " failed, " +
                              total.ElapsedMilliseconds + " ms");
            return failed == 0 ? 0 : 1;
        }
    }
}
