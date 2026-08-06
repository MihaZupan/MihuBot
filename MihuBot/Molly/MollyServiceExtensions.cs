using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MihuBot.Configuration;
using MihuBot.Molly.Api;

#nullable enable

namespace MihuBot.Molly;

public static class MollyServiceExtensions
{
    /// <summary>Requests larger than this are rejected by the server while the body is being read.</summary>
    private const int MaxRequestBodyLength = 8 * 1024;

    public const string AppSignatureHeader = "X-App-Signature";

    private const string RequestBodyKey = "MollyRequestBody";

    /// <summary>
    /// Registers the Molly services. The caller is responsible for checking
    /// <see cref="OptionalFeatures.Molly"/> first - <see cref="MollyService"/> throws without its keys.
    /// </summary>
    public static IServiceCollection AddMollyServices(this IServiceCollection services)
    {
        services.TryAddSingleton<MollyRateLimiter>();
        services.TryAddSingleton<MollyIdProtector>();
        services.TryAddSingleton<ProtonMailEncryptor>();
        services.TryAddSingleton<MollyService>();

        return services;
    }

    public static RouteGroupBuilder MapMollyApis(this RouteGroupBuilder group)
    {
        // Rate limiting and the app signature check apply to every endpoint in the group.
        group.AddEndpointFilter(ValidateRequestAsync);

        group.MapPost("login", static async (HttpContext context, MollyService molly, CancellationToken cancellationToken) =>
        {
            if (GetRequest<MollyLoginRequest>(context) is not { } request)
            {
                return Results.BadRequest();
            }

            MollyLoginResult result = await molly.LoginAsync(request.KeyHash, cancellationToken);

            return result.Status switch
            {
                // A non-blocking command rides along with the normal payload.
                MollyResultStatus.Ok => Results.Ok(new MollyLoginResponse
                {
                    ServerHmac = Convert.ToBase64String(result.ServerHmac!),
                    Id = result.ProtectedId,
                    Command = result.Command.ToWireValue(),
                }),
                MollyResultStatus.Command => Results.Ok(new MollyLoginResponse { Command = result.Command.ToWireValue() }),
                _ => Results.BadRequest(),
            };
        });

        group.MapPost("associate", static async (HttpContext context, MollyService molly, CancellationToken cancellationToken) =>
        {
            if (GetRequest<MollyAssociateRequest>(context) is not { } request)
            {
                return Results.BadRequest();
            }

            MollyCommandResult result = await molly.AssociateAsync(request.Id, request.Nickname, cancellationToken);

            return ToResult(result, static command => new MollyCommandResponse { Command = command });
        });

        group.MapPost("ping", static async (HttpContext context, MollyService molly, CancellationToken cancellationToken) =>
        {
            if (GetRequest<MollyPingRequest>(context) is not { } request)
            {
                return Results.BadRequest();
            }

            MollyCommandResult result = await molly.PingAsync(request.Id, cancellationToken);

            return ToResult(result, static command => new MollyPingResponse { Command = command });
        });

        group.MapPost("alert", static async (HttpContext context, MollyService molly, CancellationToken cancellationToken) =>
        {
            // The payload is arbitrary beyond the id, so the raw body is stored as-is.
            if (GetRequest<MollyAlertRequest>(context) is not { } request)
            {
                return Results.BadRequest();
            }

            MollyCommandResult result = await molly.SubmitAlertAsync(request.Id, GetRequestBody(context), cancellationToken);

            return ToResult(result, static command => new MollyCommandResponse { Command = command });
        });

        return group;
    }

    /// <summary>The raw bytes that <see cref="ValidateRequestAsync"/> read and verified.</summary>
    private static byte[] GetRequestBody(HttpContext context) => (byte[])context.Items[RequestBodyKey]!;

    /// <summary>
    /// Deserializes the body that <see cref="ValidateRequestAsync"/> already read and verified.
    /// The endpoints can't bind it directly: minimal API filters run after model binding, so the
    /// signature check would otherwise see an already-consumed stream.
    /// </summary>
    /// <returns>
    /// Null if the body isn't valid JSON for <typeparamref name="T"/>, or is the literal <c>null</c>,
    /// which the endpoints turn into a 400.
    /// </returns>
    private static T? GetRequest<T>(HttpContext context) where T : class
    {
        // The filter runs for every endpoint in the group, so the body is always present by this point.
        byte[] body = GetRequestBody(context);

        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the per-IP rate limit and verifies the app signature over the raw request bytes,
    /// which are then stashed for the endpoint to deserialize.
    /// </summary>
    private static async ValueTask<object?> ValidateRequestAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext httpContext = context.HttpContext;
        HttpRequest request = httpContext.Request;

        var rateLimiter = httpContext.RequestServices.GetRequiredService<MollyRateLimiter>();

        if (!rateLimiter.TryEnter(httpContext, out TimeSpan retryAfter))
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // Let the server enforce the size limit while the body is read, instead of counting bytes here.
        // Failing closed: without the limit an unbounded body would be buffered into memory.
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is not { IsReadOnly: false } maxBodySize)
        {
            throw new InvalidOperationException(
                $"Can't set the request body size limit for '{request.GetEncodedPathAndQuery()}'. " +
                $"The server must support {nameof(IHttpMaxRequestBodySizeFeature)} and the body must not have been read yet.");
        }

        maxBodySize.MaxRequestBodySize = MaxRequestBodyLength;

        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, httpContext.RequestAborted);
            body = buffer.ToArray();
        }
        catch (BadHttpRequestException ex)
        {
            // Thrown for an over-sized or otherwise malformed body.
            return Results.StatusCode(ex.StatusCode);
        }

        var molly = httpContext.RequestServices.GetRequiredService<MollyService>();

        if (request.Headers[AppSignatureHeader] is not [{ Length: > 0 } signature] ||
            !molly.VerifyAppSignature(signature, request.GetEncodedPathAndQuery(), body))
        {
            return Results.Unauthorized();
        }

        httpContext.Items[RequestBodyKey] = body;

        return await next(context);
    }

    private static IResult ToResult<TResponse>(MollyCommandResult result, Func<string?, TResponse> createResponse)
    {
        return result.Status switch
        {
            // Ok may still carry a non-blocking command, so both cases report it.
            MollyResultStatus.Ok or MollyResultStatus.Command => Results.Ok(createResponse(result.Command.ToWireValue())),
            _ => Results.BadRequest(),
        };
    }
}
