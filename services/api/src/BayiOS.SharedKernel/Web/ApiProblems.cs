using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BayiOS.SharedKernel.Web;

/// <summary>RFC 9457 problem factory using the stable <c>code</c> catalogue of api-strategy §6.</summary>
public static class ApiProblems
{
    public const string TypeBase = "https://bayios.app/problems/";

    public static ProblemDetails Create(int status, string code, string title, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Type = TypeBase + code,
            Title = title,
            Status = status,
            Detail = detail,
        };
        problem.Extensions["code"] = code;
        return problem;
    }

    public static HttpValidationProblemDetails Validation(IDictionary<string, string[]> errors)
    {
        var problem = new HttpValidationProblemDetails(errors)
        {
            Type = TypeBase + "validation.failed",
            Title = "Validation failed",
            Status = StatusCodes.Status422UnprocessableEntity,
        };
        problem.Extensions["code"] = "validation.failed";
        return problem;
    }

    /// <summary>Writes a problem response through <see cref="IProblemDetailsService"/> (adds correlationId).</summary>
    public static async Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        var service = context.RequestServices.GetService(typeof(IProblemDetailsService)) as IProblemDetailsService;
        if (service is null || !await service.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }))
        {
            await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
        }
    }
}
