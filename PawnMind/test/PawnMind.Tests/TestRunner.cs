using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PawnMind.Tests
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
            True(!condition, message);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertionException(message + " (expected=" + expected + ", actual=" + actual + ")");
        }

        public static void NotNull(object value, string message)
        {
            if (value == null) throw new AssertionException(message + " (was null)");
        }

        public static void Null(object value, string message)
        {
            if (value != null) throw new AssertionException(message + " (was not null)");
        }

        public static void Contains(string haystack, string needle, string message)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new AssertionException(message + " (missing: " + needle + ")");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            var methods = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name)
                .ToList();

            int passed = 0;
            var failures = new List<string>();

            foreach (MethodInfo method in methods)
            {
                TestAttribute attr = method.GetCustomAttribute<TestAttribute>();
                string name = string.IsNullOrEmpty(attr.Name) ? method.DeclaringType.Name + "." + method.Name : attr.Name;
                if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                try
                {
                    method.Invoke(null, null);
                    passed++;
                    Console.WriteLine("PASS " + name);
                }
                catch (TargetInvocationException tie)
                {
                    Exception inner = tie.InnerException ?? tie;
                    failures.Add(name + ": " + inner.Message);
                    Console.WriteLine("FAIL " + name);
                    Console.WriteLine("     " + inner.GetType().Name + ": " + inner.Message);
                }
                catch (Exception e)
                {
                    failures.Add(name + ": " + e.Message);
                    Console.WriteLine("FAIL " + name);
                    Console.WriteLine("     " + e.GetType().Name + ": " + e.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine("passed=" + passed + " failed=" + failures.Count);
            if (failures.Count == 0) return 0;

            Console.WriteLine("--- failures ---");
            foreach (string failure in failures) Console.WriteLine(failure);
            return 1;
        }
    }
}
