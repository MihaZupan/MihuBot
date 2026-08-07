using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MihuBot.Configuration;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly.Api;

#nullable enable

namespace MihuBot.Molly;

public static class MollyServiceExtensions
{
    /// <summary>Requests larger than this are rejected by the server while the body is being read.</summary>
    private const int MaxRequestBodyLength = 8 * 1024;

    private const string EncryptedContentType = "application/octet-stream";

    /// <summary>
    /// Registers the Molly services. The caller is responsible for checking
    /// <see cref="OptionalFeatures.Molly"/> first - <see cref="MollyService"/> throws without its keys.
    /// </summary>
    public static IServiceCollection AddMollyServices(this IServiceCollection services)
    {
        services.TryAddSingleton<MollyRateLimiter>();
        services.TryAddSingleton<MollyIdProtector>();
        services.TryAddSingleton<MollyRequestProtector>();
        services.TryAddSingleton<ProtonMailEncryptor>();
        services.TryAddSingleton<MollyService>();

        return services;
    }

    /// <summary>
    /// Maps the single Molly endpoint. Everything - which operation to run and what came of it -
    /// travels inside an encrypted body (see <see cref="MollyRequestProtector"/>), so there is
    /// nothing left to distinguish by path or status code.
    /// </summary>
    public static RouteGroupBuilder MapMollyApis(this RouteGroupBuilder group)
    {
        group.MapPost("", HandleRequestAsync);

        return group;
    }

    private static async Task<IResult> HandleRequestAsync(HttpContext context, MollyService molly, MollyRequestProtector protector, CancellationToken cancellationToken)
    {
        var rateLimiter = context.RequestServices.GetRequiredService<MollyRateLimiter>();

        if (!rateLimiter.TryEnter(context, out TimeSpan retryAfter))
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (await TryReadBodyAsync(context) is not { } body)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // A body that isn't sealed to our public key (or is a stale/replayed request) didn't come from
        // the app, so there's nobody to hand a meaningful (and encryptable) answer to.
        if (!protector.TryDecryptRequest(body, out MollyApiRequest? request, out byte[]? sessionKey))
        {
            return Results.BadRequest();
        }

        MollyApiResponse response = await ExecuteAsync(molly, request, cancellationToken);

        return Results.Bytes(protector.EncryptResponse(response, sessionKey), EncryptedContentType);
    }

    private static async Task<MollyApiResponse> ExecuteAsync(MollyService molly, MollyApiRequest request, CancellationToken cancellationToken)
    {
        switch (request.Action)
        {
            case MollyApiActions.Login:
            {
                if (GetData<MollyLoginRequest>(request) is not { } data)
                {
                    return Invalid();
                }

                MollyLoginResult result = await molly.LoginAsync(data.KeyHash, cancellationToken);

                return new MollyApiResponse
                {
                    Status = result.Status.ToWireValue(),
                    Data = result.Status switch
                    {
                        // A non-blocking command rides along with the normal payload.
                        MollyResultStatus.Ok => new MollyLoginResponse
                        {
                            ServerHmac = Convert.ToBase64String(result.ServerHmac!),
                            Id = result.ProtectedId,
                            Command = result.Command.ToWireValue(),
                        },
                        MollyResultStatus.Command => new MollyLoginResponse { Command = result.Command.ToWireValue() },
                        _ => null,
                    },
                };
            }

            case MollyApiActions.Associate:
            {
                if (GetData<MollyAssociateRequest>(request) is not { } data)
                {
                    return Invalid();
                }

                MollyCommandResult result = await molly.AssociateAsync(data.Id, data.Nickname, cancellationToken);

                return ToResponse(result, static command => new MollyCommandResponse { Command = command });
            }

            case MollyApiActions.Ping:
            {
                if (GetData<MollyPingRequest>(request) is not { } data)
                {
                    return Invalid();
                }

                MollyCommandResult result = await molly.PingAsync(data.Id, cancellationToken);

                return ToResponse(result, static command => new MollyPingResponse { Command = command });
            }

            case MollyApiActions.Alert:
            {
                // The payload is arbitrary beyond the id, so the raw data object is stored as-is.
                if (GetData<MollyAlertRequest>(request) is not { } data)
                {
                    return Invalid();
                }

                byte[] payload = Encoding.UTF8.GetBytes(request.Data.GetRawText());

                MollyCommandResult result = await molly.SubmitAlertAsync(data.Id, payload, cancellationToken);

                return ToResponse(result, static command => new MollyCommandResponse { Command = command });
            }

            default:
                return Invalid();
        }
    }

    /// <summary>
    /// Reads the body, letting the server enforce the size limit while it does instead of counting bytes here.
    /// </summary>
    /// <returns>Null if the body was over-sized or otherwise malformed.</returns>
    private static async Task<byte[]?> TryReadBodyAsync(HttpContext context)
    {
        // Failing closed: without the limit an unbounded body would be buffered into memory.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is not { IsReadOnly: false } maxBodySize)
        {
            throw new InvalidOperationException(
                $"Can't set the request body size limit. The server must support " +
                $"{nameof(IHttpMaxRequestBodySizeFeature)} and the body must not have been read yet.");
        }

        maxBodySize.MaxRequestBodySize = MaxRequestBodyLength;

        try
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            return buffer.ToArray();
        }
        catch (BadHttpRequestException)
        {
            // Thrown for an over-sized or otherwise malformed body.
            return null;
        }
    }

    /// <returns>Null if <see cref="MollyApiRequest.Data"/> isn't a JSON object for <typeparamref name="T"/>.</returns>
    private static T? GetData<T>(MollyApiRequest request) where T : class
    {
        if (request.Data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return request.Data.Deserialize<T>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MollyApiResponse Invalid() => new() { Status = MollyResultStatus.InvalidRequest.ToWireValue() };

    private static MollyApiResponse ToResponse<TResponse>(MollyCommandResult result, Func<string?, TResponse> createResponse)
    {
        return new MollyApiResponse
        {
            Status = result.Status.ToWireValue(),

            // Ok may still carry a non-blocking command, so both cases report it.
            Data = result.Status is MollyResultStatus.Ok or MollyResultStatus.Command
                ? createResponse(result.Command.ToWireValue())
                : null,
        };
    }
}
