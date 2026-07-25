using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.Options;

namespace MihuBot.Configuration;

public static class OptionalDependencies
{
    /// <summary>
    /// Returns the first constructor parameter type that can't be resolved from DI, if any.
    /// Optional services aren't registered when they aren't configured, in which case
    /// the types depending on them are skipped instead of failing at runtime.
    /// </summary>
    public static Type GetMissingDependency(IServiceProvider services, Type type)
    {
        Type firstMissing = null;

        foreach (var constructor in type.GetConstructors())
        {
            Type missing = constructor.GetParameters()
                .Where(p => !p.HasDefaultValue && !p.ParameterType.IsValueType && services.GetService(p.ParameterType) is null)
                .Select(p => p.ParameterType)
                .FirstOrDefault();

            if (missing is null)
            {
                // This constructor can be satisfied.
                return null;
            }

            firstMissing ??= missing;
        }

        return firstMissing;
    }

    // Which services are registered doesn't change after startup, so the lookup is only done once per component.
    private static readonly ConcurrentDictionary<Type, Type> s_missingInjectedServices = new();

    /// <summary>
    /// Returns the first '@inject'ed component property that can't be resolved from DI, if any.
    /// Components are still routable when their functionality is disabled, so pages have to be
    /// checked before rendering them.
    /// </summary>
    public static Type GetMissingInjectedService(IServiceProvider services, Type componentType)
    {
        return s_missingInjectedServices.GetOrAdd(componentType, static (componentType, services) =>
        {
            for (Type type = componentType; type is not null && type != typeof(ComponentBase); type = type.BaseType)
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (property.GetCustomAttribute<InjectAttribute>() is not null &&
                        services.GetService(property.PropertyType) is null)
                    {
                        return property.PropertyType;
                    }
                }
            }

            return null;
        }, services);
    }
}

/// <summary>
/// Removes API controllers that depend on services which aren't available, so their routes 404 instead of throwing.
/// </summary>
public sealed class RemoveUnavailableControllersConvention(IServiceProvider services) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (ControllerModel controller in application.Controllers.ToArray())
        {
            if (OptionalDependencies.GetMissingDependency(services, controller.ControllerType) is { } missingDependency)
            {
                Console.WriteLine($"Skipping {controller.ControllerType.Name} as {missingDependency.Name} is not available.");
                application.Controllers.Remove(controller);
            }
        }
    }
}

public static class ControllerConventionServiceCollectionExtensions
{
    public static void AddRemoveUnavailableControllersConvention(this IServiceCollection services)
    {
        services.AddSingleton<IConfigureOptions<MvcOptions>>(serviceProvider =>
            new ConfigureNamedOptions<MvcOptions>(Options.DefaultName, options =>
                options.Conventions.Add(new RemoveUnavailableControllersConvention(serviceProvider))));
    }
}
