using Microsoft.Extensions.Primitives;
using System.Security.Cryptography;

namespace MihuBot.Helpers;

public static class TokenHelper
{
    public static bool CheckToken(IHeaderDictionary headers, string headerName, string expected) =>
        headers.TryGetValue(headerName, out StringValues actual) &&
        actual.Count == 1 &&
        CryptographicOperations.FixedTimeEquals(expected, actual);
}
