using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace StorageService.Storage;

public static class StorageServiceExtensions
{
    public static IServiceCollection AddStorageServices(this IServiceCollection services)
    {
        services.TryAddSingleton<StorageService>();

        return services;
    }

    public static RouteGroupBuilder MapStorageApis(this RouteGroupBuilder group)
    {
        var container = group.MapGroup("{container}");

        container.AddEndpointFilter(async (context, next) =>
        {
            StorageService storage = context.HttpContext.RequestServices.GetRequiredService<StorageService>();
            string? containerName = context.HttpContext.GetRouteValue("container") as string;

            if (!StorageService.ValidateContainerName(containerName))
            {
                return Results.BadRequest("Invalid container name");
            }

            if (!await storage.HasValidAuthorization(context.HttpContext, containerName))
            {
                return Results.Unauthorized();
            }

            return await next(context);
        });

        container.MapMethods("{*path}", [HttpMethods.Get, HttpMethods.Head], static (HttpContext context, StorageService storage, string container, string path) =>
            storage.DownloadFileAsync(context, container, path));

        container.MapPost("{*path}", static  (HttpContext context, StorageService storage, string container, string path) =>
            storage.UploadFileAsync(context, container, path));

        container.MapDelete("{*path}", static (HttpContext context, StorageService storage, string container, string path) =>
            storage.DeleteFile(context, container, path));

        return group;
    }
}
