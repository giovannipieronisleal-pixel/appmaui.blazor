using System.Reflection;

namespace Microsoft.VisualStudio.TestTools.UnitTesting
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TestClassAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestMethodAttribute : Attribute;

    public sealed class AssertFailedException(string message) : Exception(message);

    public static class Assert
    {
        public static void AreEqual<T>(T expected, T actual, string? message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertFailedException(message ?? $"Assert.AreEqual falhou. Esperado: <{expected}>. Atual: <{actual}>.");
        }

        public static void IsTrue(bool condition, string? message = null)
        {
            if (!condition)
                throw new AssertFailedException(message ?? "Assert.IsTrue falhou.");
        }

        public static void IsFalse(bool condition, string? message = null)
        {
            if (condition)
                throw new AssertFailedException(message ?? "Assert.IsFalse falhou.");
        }

        public static void IsNotNull(object? value, string? message = null)
        {
            if (value is null)
                throw new AssertFailedException(message ?? "Assert.IsNotNull falhou.");
        }

        public static void IsNull(object? value, string? message = null)
        {
            if (value is not null)
                throw new AssertFailedException(message ?? "Assert.IsNull falhou.");
        }

        public static TException ThrowsException<TException>(Action action, string? message = null)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new AssertFailedException(message ?? $"Esperava exceção {typeof(TException).Name}, mas lançou {ex.GetType().Name}: {ex.Message}");
            }

            throw new AssertFailedException(message ?? $"Esperava exceção {typeof(TException).Name}, mas nenhuma exceção foi lançada.");
        }

        public static async Task<TException> ThrowsExceptionAsync<TException>(Func<Task> action, string? message = null)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new AssertFailedException(message ?? $"Esperava exceção {typeof(TException).Name}, mas lançou {ex.GetType().Name}: {ex.Message}");
            }

            throw new AssertFailedException(message ?? $"Esperava exceção {typeof(TException).Name}, mas nenhuma exceção foi lançada.");
        }
    }
}

namespace ClothingStore.UnitTests
{
    public static class Program
    {
        public static async Task<int> Main()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var testClasses = assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<Microsoft.VisualStudio.TestTools.UnitTesting.TestClassAttribute>() is not null)
                .ToList();

            var passed = 0;
            var failed = 0;

            Console.WriteLine("==============================================================");
            Console.WriteLine("  CLOTHING STORE - SUÍTE DE TESTES AUTOMATIZADOS (.NET 10)");
            Console.WriteLine("==============================================================");

            foreach (var testClass in testClasses)
            {
                var methods = testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.GetCustomAttribute<Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute>() is not null)
                    .ToList();

                foreach (var method in methods)
                {
                    var instance = Activator.CreateInstance(testClass);
                    try
                    {
                        var result = method.Invoke(instance, null);
                        if (result is Task task)
                        {
                            await task;
                        }
                        passed++;
                        Console.WriteLine($"  [PASSOU] {testClass.Name}.{method.Name}");
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException is not null)
                    {
                        failed++;
                        Console.WriteLine($"  [FALHOU] {testClass.Name}.{method.Name} -> {tie.InnerException.Message}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine($"  [FALHOU] {testClass.Name}.{method.Name} -> {ex.Message}");
                    }
                }
            }

            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine($"  Total: {passed + failed} | Aprovados: {passed} | Falhas: {failed}");
            Console.WriteLine("==============================================================");

            return failed == 0 ? 0 : 1;
        }
    }
}
