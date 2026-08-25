using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Telltale.App;

/// <summary>
/// The one endpoint that destroys recorded history, and the rules around it.
/// </summary>
/// <remarks>
/// It is mapped here rather than in the viewer's <c>MapTelltaleApi</c>, alongside
/// the session endpoints and for the same two reasons. Only the single-process
/// build has a window token to put in front of it, and only the single-process
/// build has the recorder's writable connection to route it through. The viewer
/// executable opens the capture file read-only, so it neither serves this route
/// nor could act on it, and that is the intended arrangement: there is nowhere a
/// wipe can be asked for without the token.
/// </remarks>
static class CaptureWipeEndpoint
{
    /// <summary>Where the window posts a wipe.</summary>
    public const string Path = "/api/capture/wipe";

    /// <summary>
    /// Serves <see cref="Path"/> from <paramref name="app"/>, acting through
    /// <paramref name="wipe"/> for any request carrying <paramref name="token"/>.
    /// </summary>
    public static void MapCaptureWipe(
        this WebApplication app, ICaptureWipe wipe, string token, RollingLogFile? log = null)
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // POST, never GET. A GET is what a browser issues for a link, an image or a
        // prefetch, and none of those should be able to delete a recording by being
        // followed.
        app.MapPost(Path, async (HttpRequest request) =>
        {
            // Not Found rather than Forbidden, the same answer the session
            // endpoints give: there is nothing to gain by confirming to a caller
            // that guessed wrong that it had found the right endpoint.
            if (!WindowToken.IsPresentedIn(request, token))
                return Results.NotFound();

            // Checked rather than left to ReadFromJsonAsync, which answers a
            // content type it does not recognise with an InvalidOperationException
            // and would leave the handler throwing a bare 500 at a request this
            // endpoint has a perfectly good 400 for.
            if (!request.HasJsonContentType())
            {
                return Problem(json, StatusCodes.Status400BadRequest,
                    "The request must be sent as application/json.");
            }

            WipeRequest? body;
            try
            {
                body = await request.ReadFromJsonAsync<WipeRequest>();
            }
            catch (JsonException)
            {
                return Problem(json, StatusCodes.Status400BadRequest, "The request body is not valid JSON.");
            }

            if (body is null)
                return Problem(json, StatusCodes.Status400BadRequest, "The request body is missing.");

            return Perform(body, wipe, json, log);
        });
    }

    static IResult Perform(
        WipeRequest body, ICaptureWipe wipe, JsonSerializerOptions json, RollingLogFile? log)
    {
        bool everything = string.Equals(body.Scope, "all", StringComparison.OrdinalIgnoreCase);
        bool range = string.Equals(body.Scope, "range", StringComparison.OrdinalIgnoreCase);

        // The scope is named rather than inferred from whether a range arrived. A
        // request whose range failed to serialise would otherwise read as "delete
        // everything", which is the worst possible way for that mistake to land.
        if (!everything && !range)
            return Problem(json, StatusCodes.Status400BadRequest, "Scope must be \"all\" or \"range\".");

        if (range && (body.From is null || body.To is null))
            return Problem(json, StatusCodes.Status400BadRequest, "A range wipe needs both from and to.");

        if (range && body.To < body.From)
            return Problem(json, StatusCodes.Status400BadRequest, "The range ends before it starts.");

        try
        {
            var result = everything
                ? wipe.All()
                : wipe.Range(body.From!.Value, body.To!.Value);

            // The one destructive thing Telltale does on request, so it leaves a
            // trace beside the database rather than none at all. The line names a
            // scope and two counts and nothing else, so it adds no category of
            // information the log did not already carry.
            log?.Append(everything
                ? $"Wiped the whole capture: {result.RowsDeleted} rows, {result.BytesFreed} bytes freed."
                : $"Wiped {body.From}..{body.To}: {result.RowsDeleted} rows, {result.BytesFreed} bytes freed.");

            return Results.Json(
                new WipeResponse(result.RowsDeleted, result.BytesFreed), json);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            // Not an error to fix, and not something to retry silently either. The
            // window says so and the person tries again.
            return Problem(json, StatusCodes.Status409Conflict,
                "The capture database is busy. Try again in a moment.");
        }
        catch (SqliteException ex)
        {
            return Problem(json, StatusCodes.Status500InternalServerError,
                $"The capture database refused the delete: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // The recorder is shutting down underneath us.
            return Problem(json, StatusCodes.Status503ServiceUnavailable,
                "Telltale is stopping. Nothing was deleted.");
        }
    }

    /// <summary>
    /// Whether SQLite refused because someone else held the file, rather than
    /// because the statement was wrong.
    /// </summary>
    static bool IsBusy(SqliteException ex) =>
        ex.SqliteErrorCode is SqliteBusy or SqliteLocked;

    const int SqliteBusy = 5;
    const int SqliteLocked = 6;

    static IResult Problem(JsonSerializerOptions json, int status, string message) =>
        Results.Json(new WipeProblem(message), json, statusCode: status);

    /// <summary>What the window asks for.</summary>
    /// <param name="Scope">Either <c>all</c> or <c>range</c>.</param>
    /// <param name="From">Start of the range, epoch milliseconds, included.</param>
    /// <param name="To">End of the range, epoch milliseconds, included.</param>
    sealed record WipeRequest(
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("from")] long? From,
        [property: JsonPropertyName("to")] long? To);

    /// <summary>What happened, for the window to show.</summary>
    sealed record WipeResponse(long RowsDeleted, long BytesFreed);

    /// <summary>Why nothing happened.</summary>
    sealed record WipeProblem(string Error);
}
