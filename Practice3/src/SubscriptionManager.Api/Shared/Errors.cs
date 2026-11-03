using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SubscriptionManager.Shared;

// ---------- доменные/прикладные ошибки -> HTTP-коды (раздел 2.7 ТЗ) ----------
public abstract class AppException(string message) : Exception(message);

/// <summary>422</summary>
public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
    : AppException("Validation failed.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>404</summary>
public sealed class NotFoundException(string message) : AppException(message);
/// <summary>403</summary>
public sealed class ForbiddenException(string message) : AppException(message);
/// <summary>409</summary>
public sealed class ConflictException(string message) : AppException(message);
/// <summary>401 (неверные учётные данные / нет пользователя)</summary>
public sealed class UnauthorizedException(string message) : AppException(message);

public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new();
    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = new List<string>();
        list.Add(message);
    }

    public void ThrowIfAny()
    {
        if (Any) throw new ValidationFailedException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public interface IClock { DateTime UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }

public static class ClaimsExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out var id) ? id : throw new UnauthorizedException("Invalid token.");
}

/// <summary>Единая точка преобразования исключений в ответы без раскрытия внутренних деталей (раздел 2.5 ТЗ).</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            ValidationFailedException => (StatusCodes.Status422UnprocessableEntity, "Validation failed"),
            NotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Access denied"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            UnauthorizedException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            BadHttpRequestException or JsonException => (StatusCodes.Status400BadRequest, "Bad request"),
            _ => (StatusCodes.Status500InternalServerError, "Internal server error")
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception");

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception switch
            {
                AppException => exception.Message,
                BadHttpRequestException or JsonException => "The request body or parameters are malformed.",
                _ => "An unexpected error occurred."
            }
        };
        if (exception is ValidationFailedException v) problem.Extensions["errors"] = v.Errors;

        context.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized) context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
