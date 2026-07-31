using System.Reflection;

namespace Port.Client.Content;

/// <summary>
/// Builds no-op implementations for content-ALC IoC interfaces that have no concrete type
/// in the content-bind stub (e.g. <c>IPrototypeManager</c>).
/// Uses <see cref="DispatchProxy"/> so generic interface methods are supported.
/// </summary>
public static class ContentIoCStubEmitter
{
    /// <summary>Returns a concrete <see cref="Type"/> — prefer <see cref="CreateInstance"/> for proxies.</summary>
    public static Type Emit(Type iface)
    {
        // DispatchProxy instances don't expose a reusable public Type for IoC Register&lt;TIface,TImpl&gt;.
        // Callers should use CreateInstance + RegisterInstance.
        throw new NotSupportedException(
            "Use ContentIoCStubEmitter.CreateInstance + RegisterInstance for interface stubs.");
    }

    public static object CreateInstance(Type iface)
    {
        ArgumentNullException.ThrowIfNull(iface);
        if (!iface.IsInterface)
            throw new ArgumentException("Expected interface", nameof(iface));

        var create = typeof(DispatchProxy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(DispatchProxy.Create) && m.GetGenericArguments().Length == 2);

        return create.MakeGenericMethod(iface, typeof(IoCStubProxy)).Invoke(null, null)
               ?? throw new InvalidOperationException("DispatchProxy.Create returned null for " + iface.FullName);
    }

    // Must not be sealed — DispatchProxy.Create requires an unsealed proxy type.
    public class IoCStubProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;

            var ret = targetMethod.ReturnType;
            if (ret == typeof(void))
                return null;

            if (ret == typeof(bool))
                return false;
            if (ret == typeof(int) || ret == typeof(uint) || ret == typeof(byte) || ret == typeof(long))
                return Convert.ChangeType(0, ret);
            if (ret == typeof(float) || ret == typeof(double))
                return Convert.ChangeType(0, ret);
            if (ret == typeof(string))
                return string.Empty;

            if (ret.IsValueType)
                return Activator.CreateInstance(ret);

            // IEnumerable<T> / arrays — return empty via Array.Empty when possible
            if (ret.IsArray)
                return Array.CreateInstance(ret.GetElementType()!, 0);

            if (ret.IsGenericType)
            {
                var def = ret.GetGenericTypeDefinition();
                if (def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>) || def == typeof(IList<>)
                    || def == typeof(List<>) || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
                {
                    var elem = ret.GetGenericArguments()[0];
                    return Array.CreateInstance(elem, 0);
                }
            }

            return null;
        }
    }
}
