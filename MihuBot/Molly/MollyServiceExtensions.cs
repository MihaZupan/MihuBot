using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MihuBot.Configuration;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly.Api;
using MihuBot.Molly.Attestation;
using MihuBot.Molly.Sharing;

#nullable enable

namespace MihuBot.Molly;

public static class MollyServiceExtensions
{
    /// <summary>
    /// Encrypted request bodies larger than this are rejected by the server while the body is being read.
    /// Sized for a padded <c>share-create</c>, which carries a 64 KiB file and the fixed-size trustee roster.
    /// </summary>
    internal const int MaxRequestBodyLength = 256 * 1024;

    private const string EncryptedContentType = "application/octet-stream";

    /// <summary>
    /// Registers the Molly services. The caller is responsible for checking
    /// <see cref="OptionalFeatures.Molly"/> first - <see cref="MollyService"/> throws without its keys.
    /// </summary>
    public static IServiceCollection AddMollyServices(this IServiceCollection services)
    {
        services.TryAddSingleton<MollyRateLimiter>();
        services.TryAddSingleton<MollyResponseTimingFloor>();
        services.TryAddSingleton<MollyIdProtector>();
        services.TryAddSingleton<MollyRequestProtector>();
        services.TryAddSingleton<ProtonMailEncryptor>();
        services.TryAddSingleton<MollyAttestationTrustAnchors>();
        services.TryAddSingleton<MollyAttestationRevocations>();
        services.TryAddSingleton<MollyAttestationService>();
        services.TryAddSingleton<MollyService>();
        services.TryAddSingleton<MollyShareService>();
        services.AddHostedService<MollyAttestationRevocationRefresher>();

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

    private static async Task<IResult> HandleRequestAsync(HttpContext context, MollyService molly, MollyShareService shares, MollyRequestProtector protector, MollyRateLimiter rateLimiter, MollyResponseTimingFloor timingFloor, CancellationToken cancellationToken)
    {
        if (!rateLimiter.TryEnter(context, out TimeSpan retryAfter))
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (await TryReadBodyAsync(context) is not { } body)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        Task minimumResponseDelay = timingFloor.StartAsync();

        try
        {
            // Unknown recipient keys and failed authentication all fail closed.
            if (!protector.TryDecryptRequest(body, out MollyApiRequest? request, out byte[]? responseKey, out MollyPublicKeyId recipientKeyId, out MollyExchangeContext? exchange))
            {
                return Results.BadRequest();
            }

            MollyApiResponse response = request.Action == MollyApiActions.TransportKey
                ? new MollyApiResponse { Status = MollyResultStatus.Ok.ToWireValue(), Data = protector.GetTransportKey() }
                : await ExecuteAsync(molly, shares, request, exchange, cancellationToken);

            context.Response.Headers.CacheControl = "no-store";
            byte[] encrypted = protector.EncryptResponse(response, responseKey, recipientKeyId, bucketed: MollyShareService.IsShareAction(request.Action));
            CryptographicOperations.ZeroMemory(responseKey);
            return Results.Bytes(encrypted, EncryptedContentType);
        }
        finally
        {
            await minimumResponseDelay;
        }
    }

    private static async Task<MollyApiResponse> ExecuteAsync(MollyService molly, MollyShareService shares, MollyApiRequest request, MollyExchangeContext exchange, CancellationToken cancellationToken)
    {
        switch (request.Action)
        {
            case MollyApiActions.Login:
            {
                if (GetData<MollyLoginRequest>(request) is not { } data)
                {
                    return Invalid();
                }

                MollyLoginResult result = await molly.LoginAsync(data, exchange, cancellationToken);

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

                MollyCommandResult result = await shares.AssociateAsync(data, exchange, cancellationToken);

                return ToResponse(result, () => new MollyAssociateResponse
                {
                    Identity = result.Status == MollyResultStatus.Ok ? result.Identity?.ToAssociateWireValue() : null,
                });
            }

            case MollyApiActions.Ping:
            {
                if (GetData<MollyPingRequest>(request) is not { } data)
                {
                    return Invalid();
                }

                MollyCommandResult result = await molly.PingAsync(data.Id, data.BatteryLevel, data.LocationEnabled, data.AppVersion, cancellationToken);

                return ToResponse(result, static () => new MollyPingResponse());
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
                CryptographicOperations.ZeroMemory(payload);

                return ToResponse(result, static () => new MollyCommandResponse());
            }

            case MollyApiActions.RegistrationToken:
            {
                if (GetData<MollyRegistrationTokenRequest>(request) is not { } data)
                {
                    return Invalid();
                }

                MollyCommandResult result = await molly.IssueRegistrationTokenAsync(data.Id, cancellationToken);

                return ToResponse(result, () => new MollyRegistrationTokenResponse
                {
                    Token = result.RegistrationToken?.Token,
                    ExpiresAt = result.RegistrationToken is { } token ? new DateTimeOffset(token.ExpiresAt).ToUnixTimeSeconds() : null,
                });
            }

            case MollyApiActions.ShareReserve:
            case MollyApiActions.ShareCreate:
            case MollyApiActions.ShareUpdate:
            case MollyApiActions.ShareSync:
            case MollyApiActions.ShareGet:
            case MollyApiActions.ShareStart:
            case MollyApiActions.ShareApprove:
            case MollyApiActions.ShareFetch:
            case MollyApiActions.ShareDelete:
            case MollyApiActions.ShareDismiss:
            {
                if (GetData<MollyShareRequest>(request) is not { } data)
                {
                    return Invalid();
                }

                return await shares.ExecuteAsync(request.Action, data, cancellationToken);
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

    private static MollyApiResponse ToResponse<TResponse>(MollyCommandResult result, Func<TResponse> createResponse) where TResponse : MollyCommandResponse
    {
        // Ok may still carry a non-blocking command, so both cases report it.
        if (result.Status is not (MollyResultStatus.Ok or MollyResultStatus.Command))
        {
            return new MollyApiResponse { Status = result.Status.ToWireValue() };
        }

        TResponse response = createResponse();
        response.Command = result.Command.ToWireValue();
        response.ShareUpdatedAt = result.ShareUpdatedAt;

        return new MollyApiResponse { Status = result.Status.ToWireValue(), Data = response };
    }
}
