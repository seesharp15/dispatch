using Dispatch.Web.Data;
using FeedDiscovery;
using Dispatch.Web.Models;
using Dispatch.Web.Options;
using Dispatch.Web.Services;
using Dispatch.Web.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json.Serialization;
using System.Data;
using System.Globalization;
using System.Text.Json;
using DiscoveryBroadcastifyOptions = FeedDiscovery.Broadcastify.BroadcastifyOptions;

string ResolveRecordingPath(string path, IHostEnvironment env)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return path;
    }

    return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path));
}

double ComputeTranscriptProgress(Recording recording, TranscriptionOptions options, long? fileSizeBytes)
{
    return recording.TranscriptStatus switch
    {
        TranscriptStatus.Complete => 100,
        TranscriptStatus.Skipped => 100,
        TranscriptStatus.Failed => 0,
        TranscriptStatus.Processing => EstimateProcessingProgress(recording, options, fileSizeBytes),
        _ => 0
    };
}

double EstimateProcessingProgress(Recording recording, TranscriptionOptions options, long? fileSizeBytes)
{
    if (recording.TranscriptStartedUtc == null || recording.DurationSeconds <= 0)
    {
        if (recording.TranscriptStartedUtc == null)
        {
            return 5;
        }
    }

    var rate = Math.Max(options.ExpectedRealtimeFactor, 0.1);
    var expectedAudioSeconds = EstimateDurationFromFileSize(fileSizeBytes, options);
    if (expectedAudioSeconds <= 0 && recording.DurationSeconds > 0)
    {
        expectedAudioSeconds = recording.DurationSeconds;
    }

    if (expectedAudioSeconds <= 0)
    {
        expectedAudioSeconds = 1;
    }

    var expectedSeconds = Math.Max(expectedAudioSeconds / rate, 1);
    var elapsed = (DateTime.UtcNow - recording.TranscriptStartedUtc.Value).TotalSeconds;
    var percent = Math.Min(99, (elapsed / expectedSeconds) * 100);
    return Math.Max(5, percent);
}

double EstimateDurationFromFileSize(long? fileSizeBytes, TranscriptionOptions options)
{
    if (fileSizeBytes == null || fileSizeBytes <= 0)
    {
        return 0;
    }

    // WAV header is ~44 bytes for PCM.
    var audioBytes = Math.Max(0, fileSizeBytes.Value - 44);
    var bytesPerSecond = Math.Max(options.EstimatedBytesPerSecond, 1000);
    return audioBytes / (double)bytesPerSecond;
}

DateTime AsUtc(DateTime value)
{
    return value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

long? GetRecordingFileSize(Recording recording, IWebHostEnvironment env)
{
    if (string.IsNullOrWhiteSpace(recording.FilePath))
    {
        return null;
    }

    var path = ResolveRecordingPath(recording.FilePath, env);
    try
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        return info.Length;
    }
    catch
    {
        return null;
    }
}

async Task<(Dictionary<Guid, int> Map, int Total)> GetPendingQueueAsync(DispatchDbContext db, CancellationToken cancellationToken)
{
    var pendingIds = await db.Recordings.AsNoTracking()
        .Where(r => r.TranscriptStatus == TranscriptStatus.Pending && !r.IsArchived)
        .OrderBy(r => r.CreatedUtc)
        .Select(r => r.Id)
        .ToListAsync(cancellationToken);

    var map = new Dictionary<Guid, int>(pendingIds.Count);
    for (var i = 0; i < pendingIds.Count; i++)
    {
        map[pendingIds[i]] = i + 1;
    }

    return (map, pendingIds.Count);
}

var builder = WebApplication.CreateBuilder(args);

string ResolvePath(string basePath, string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return path;
    }

    return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path));
}

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.PostConfigure<StorageOptions>(options =>
{
    var root = builder.Environment.ContentRootPath;
    options.RootPath = ResolvePath(root, options.RootPath);
    options.RecordingsPath = ResolvePath(root, options.RecordingsPath);
    options.DatabasePath = ResolvePath(root, options.DatabasePath);
});
builder.Services.Configure<SegmentationOptions>(builder.Configuration.GetSection("Segmentation"));
builder.Services.Configure<StreamOptions>(builder.Configuration.GetSection("Stream"));
builder.Services.Configure<BroadcastifyOptions>(builder.Configuration.GetSection("Broadcastify"));
builder.Services.Configure<TranscriptionOptions>(builder.Configuration.GetSection("Transcription"));
builder.Services.Configure<DecoderOptions>(builder.Configuration.GetSection("Decoder"));
builder.Services.AddBroadcastifyFeedDiscovery(builder.Configuration);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
storageOptions.RootPath = ResolvePath(builder.Environment.ContentRootPath, storageOptions.RootPath);
storageOptions.RecordingsPath = ResolvePath(builder.Environment.ContentRootPath, storageOptions.RecordingsPath);
storageOptions.DatabasePath = ResolvePath(builder.Environment.ContentRootPath, storageOptions.DatabasePath);

Directory.CreateDirectory(storageOptions.RootPath);
Directory.CreateDirectory(storageOptions.RecordingsPath);
var dbDirectory = Path.GetDirectoryName(storageOptions.DatabasePath);
if (!string.IsNullOrWhiteSpace(dbDirectory))
{
    Directory.CreateDirectory(dbDirectory);
}

builder.Services.AddDbContext<DispatchDbContext>(options =>
    options.UseSqlite($"Data Source={storageOptions.DatabasePath}"));

builder.Services.AddHttpClient("stream")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DispatchFeedMonitor/1.0");
    });

builder.Services.AddSingleton<BroadcastifyResolver>();
builder.Services.AddSingleton<IRecordingEventHub, RecordingEventHub>();
builder.Services.AddSingleton<FeedRecorder>();
builder.Services.AddSingleton<FeedCoordinator>();

builder.Services.AddSingleton<ITranscriber>(sp =>
{
    var options = sp.GetRequiredService<IOptions<TranscriptionOptions>>().Value;
    if (!options.Enabled)
    {
        return new NoopTranscriber();
    }

    return options.Provider.Equals("whisper-cli", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<WhisperCliTranscriber>()
        : new NoopTranscriber();
});

builder.Services.AddSingleton<WhisperCliTranscriber>();
builder.Services.AddHostedService<TranscriptionWorker>();
builder.Services.AddHostedService<FeedStartupWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
    db.Database.EnsureCreated();

    var connection = db.Database.GetDbConnection();
    connection.Open();
    var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "PRAGMA table_info('Recordings');";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            existingColumns.Add(reader.GetString(1));
        }
    }

    if (!existingColumns.Contains("TranscriptStartedUtc"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Recordings ADD COLUMN TranscriptStartedUtc TEXT NULL;");
    }

    if (!existingColumns.Contains("IsArchived"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Recordings ADD COLUMN IsArchived INTEGER NOT NULL DEFAULT 0;");
    }

    if (!existingColumns.Contains("ArchivedUtc"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Recordings ADD COLUMN ArchivedUtc TEXT NULL;");
    }
}

app.MapGet("/api/discovery/states", (IOptions<DiscoveryBroadcastifyOptions> options) =>
{
    var states = options.Value.StateIdMap.Keys
        .OrderBy(s => s)
        .ToList();

    return Results.Ok(states);
});

app.MapGet("/api/discovery/feeds", async (string state, string? county, IFeedDiscoveryService service, CancellationToken ct) =>
{
    var feeds = string.IsNullOrWhiteSpace(county)
        ? await service.GetFeedsAsync(state, ct)
        : await service.GetFeedsAsync(state, county, ct);

    var response = feeds.Select(feed =>
    {
        var feedId = feed.AudioSource.AbsolutePath.Trim('/');
        return new DiscoveryFeedDto(
            feed.State,
            feed.County,
            feed.FeedName,
            feed.FeedStatus,
            feedId,
            feed.AudioSource.ToString());
    });

    return Results.Ok(response);
});

app.MapGet("/api/feeds", async (DispatchDbContext db, FeedCoordinator coordinator) =>
{
    var feeds = await db.Feeds.AsNoTracking().OrderBy(f => f.Name).ToListAsync();
    var response = feeds.Select(feed => new FeedDto(
        feed.Id,
        feed.Name,
        feed.BroadcastifyUrl,
        feed.StreamUrl,
        feed.FeedIdentifier,
        feed.IsActive,
        coordinator.IsRunning(feed.Id),
        AsUtc(feed.CreatedUtc),
        feed.LastStartedUtc.HasValue ? AsUtc(feed.LastStartedUtc.Value) : null,
        feed.LastStoppedUtc.HasValue ? AsUtc(feed.LastStoppedUtc.Value) : null));

    return Results.Ok(response);
});

app.MapPost("/api/feeds", async (AddFeedRequest request, DispatchDbContext db, BroadcastifyResolver resolver) =>
{
    if (!resolver.TryResolve(request.BroadcastifyUrl, out var feedId, out var streamUrl))
    {
        return Results.BadRequest(new { message = "Unable to parse Broadcastify feed URL or ID." });
    }

    var name = string.IsNullOrWhiteSpace(request.Name) ? $"Feed {feedId}" : request.Name.Trim();
    var feed = new Feed
    {
        Id = Guid.NewGuid(),
        Name = name,
        BroadcastifyUrl = request.BroadcastifyUrl.Trim(),
        StreamUrl = streamUrl,
        FeedIdentifier = feedId,
        IsActive = false,
        CreatedUtc = DateTime.UtcNow
    };

    db.Feeds.Add(feed);
    await db.SaveChangesAsync();

    return Results.Ok(new FeedDto(
        feed.Id,
        feed.Name,
        feed.BroadcastifyUrl,
        feed.StreamUrl,
        feed.FeedIdentifier,
        feed.IsActive,
        false,
        feed.CreatedUtc,
        feed.LastStartedUtc,
        feed.LastStoppedUtc));
});

app.MapPost("/api/feeds/{id:guid}/start", async (Guid id, DispatchDbContext db, FeedCoordinator coordinator, CancellationToken ct) =>
{
    var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == id, ct);
    if (feed == null)
    {
        return Results.NotFound();
    }

    feed.IsActive = true;
    feed.LastStartedUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);

    await coordinator.StartAsync(feed, ct);

    return Results.Ok();
});

app.MapPost("/api/feeds/{id:guid}/stop", async (Guid id, DispatchDbContext db, FeedCoordinator coordinator, CancellationToken ct) =>
{
    var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == id, ct);
    if (feed == null)
    {
        return Results.NotFound();
    }

    feed.IsActive = false;
    feed.LastStoppedUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);

    await coordinator.StopAsync(feed.Id);

    return Results.Ok();
});

app.MapGet("/api/feeds/{id:guid}/recordings", async (Guid id, bool includeArchived, DateTimeOffset? since, DispatchDbContext db, IOptions<TranscriptionOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
{
    var query = db.Recordings.AsNoTracking()
        .Where(r => r.FeedId == id);

    if (!includeArchived)
    {
        query = query.Where(r => !r.IsArchived);
    }

    if (since.HasValue)
    {
        var sinceUtc = since.Value.UtcDateTime;
        query = query.Where(r => r.StartUtc > sinceUtc);
    }

    var recordings = await query
        .OrderByDescending(r => r.StartUtc)
        .ToListAsync(ct);

    var queueInfo = await GetPendingQueueAsync(db, ct);

    var response = recordings.Select(r => new RecordingDto(
        r.Id,
        r.FeedId,
        r.FilePath,
        AsUtc(r.StartUtc),
        AsUtc(r.EndUtc),
        r.DurationSeconds,
        r.TranscriptStatus,
        ComputeTranscriptProgress(r, options.Value, GetRecordingFileSize(r, env)),
        queueInfo.Map.TryGetValue(r.Id, out var position) ? position : null,
        queueInfo.Total > 0 ? queueInfo.Total : null,
        r.TranscriptStartedUtc.HasValue ? AsUtc(r.TranscriptStartedUtc.Value) : null,
        r.TranscriptText,
        r.TranscriptPath,
        r.TranscriptProvider,
        r.IsArchived,
        r.ArchivedUtc.HasValue ? AsUtc(r.ArchivedUtc.Value) : null));

    return Results.Ok(response);
});

app.MapGet("/api/recordings/{id:guid}/audio", async (Guid id, DispatchDbContext db, IWebHostEnvironment env) =>
{
    var recording = await db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    if (recording == null)
    {
        return Results.NotFound();
    }

    var filePath = ResolveRecordingPath(recording.FilePath, env);

    if (!File.Exists(filePath))
    {
        return Results.NotFound();
    }

    return Results.File(filePath, "audio/wav", enableRangeProcessing: true);
});

app.MapGet("/api/recordings/{id:guid}", async (Guid id, DispatchDbContext db, IOptions<TranscriptionOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
{
    var recording = await db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    if (recording == null)
    {
        return Results.NotFound();
    }

    var queueInfo = await GetPendingQueueAsync(db, ct);

    return Results.Ok(new RecordingDto(
        recording.Id,
        recording.FeedId,
        recording.FilePath,
        AsUtc(recording.StartUtc),
        AsUtc(recording.EndUtc),
        recording.DurationSeconds,
        recording.TranscriptStatus,
        ComputeTranscriptProgress(recording, options.Value, GetRecordingFileSize(recording, env)),
        queueInfo.Map.TryGetValue(recording.Id, out var position) ? position : null,
        queueInfo.Total > 0 ? queueInfo.Total : null,
        recording.TranscriptStartedUtc.HasValue ? AsUtc(recording.TranscriptStartedUtc.Value) : null,
        recording.TranscriptText,
        recording.TranscriptPath,
        recording.TranscriptProvider,
        recording.IsArchived,
        recording.ArchivedUtc.HasValue ? AsUtc(recording.ArchivedUtc.Value) : null));
});

app.MapPost("/api/recordings/batch", async (BatchRecordingsRequest request, DispatchDbContext db, IOptions<TranscriptionOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
{
    if (request?.RecordingIds == null || request.RecordingIds.Count == 0)
    {
        return Results.Ok(Array.Empty<RecordingDto>());
    }

    var ids = request.RecordingIds.Distinct().ToArray();
    var recordings = await db.Recordings.AsNoTracking()
        .Where(r => ids.Contains(r.Id))
        .ToListAsync(ct);

    var queueInfo = await GetPendingQueueAsync(db, ct);

    var response = recordings.Select(r => new RecordingDto(
        r.Id,
        r.FeedId,
        r.FilePath,
        AsUtc(r.StartUtc),
        AsUtc(r.EndUtc),
        r.DurationSeconds,
        r.TranscriptStatus,
        ComputeTranscriptProgress(r, options.Value, GetRecordingFileSize(r, env)),
        queueInfo.Map.TryGetValue(r.Id, out var position) ? position : null,
        queueInfo.Total > 0 ? queueInfo.Total : null,
        r.TranscriptStartedUtc.HasValue ? AsUtc(r.TranscriptStartedUtc.Value) : null,
        r.TranscriptText,
        r.TranscriptPath,
        r.TranscriptProvider,
        r.IsArchived,
        r.ArchivedUtc.HasValue ? AsUtc(r.ArchivedUtc.Value) : null));

    return Results.Ok(response);
});

app.MapPost("/api/recordings/{id:guid}/reprocess", async (Guid id, DispatchDbContext db, CancellationToken ct) =>
{
    var recording = await db.Recordings.FirstOrDefaultAsync(r => r.Id == id, ct);
    if (recording == null)
    {
        return Results.NotFound();
    }

    if (!string.IsNullOrWhiteSpace(recording.TranscriptPath) && File.Exists(recording.TranscriptPath))
    {
        try
        {
            File.Delete(recording.TranscriptPath);
        }
        catch
        {
            // Ignore delete failures; reprocess will overwrite
        }
    }

    var fallbackTranscript = recording.FilePath + ".txt";
    if (File.Exists(fallbackTranscript))
    {
        try
        {
            File.Delete(fallbackTranscript);
        }
        catch
        {
            // Ignore delete failures
        }
    }

    recording.TranscriptStatus = TranscriptStatus.Pending;
    recording.TranscriptStartedUtc = null;
    recording.TranscribedUtc = null;
    recording.TranscriptText = null;
    recording.TranscriptPath = null;
    recording.TranscriptProvider = null;
    recording.Error = null;

    await db.SaveChangesAsync(ct);
    return Results.Ok();
});

app.MapPost("/api/recordings/{id:guid}/archive", async (Guid id, DispatchDbContext db, IRecordingEventHub eventHub, CancellationToken ct) =>
{
    var recording = await db.Recordings.FirstOrDefaultAsync(r => r.Id == id, ct);
    if (recording == null)
    {
        return Results.NotFound();
    }

    if (!recording.IsArchived)
    {
        recording.IsArchived = true;
        recording.ArchivedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(new RecordingEvent(recording.Id, recording.FeedId, RecordingEventType.Archived));
    }

    return Results.Ok();
});

app.MapPost("/api/feeds/{id:guid}/recordings/archive", async (Guid id, string day, DispatchDbContext db, IRecordingEventHub eventHub, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(day) ||
        !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayDate))
    {
        return Results.BadRequest(new { message = "Day must be in yyyy-MM-dd format." });
    }

    var localStart = dayDate.ToDateTime(TimeOnly.MinValue);
    var localEnd = localStart.AddDays(1);
    var utcStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Local));
    var utcEnd = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Local));

    var recordings = await db.Recordings
        .Where(r => r.FeedId == id && !r.IsArchived && r.StartUtc >= utcStart && r.StartUtc < utcEnd)
        .ToListAsync(ct);

    if (recordings.Count == 0)
    {
        return Results.Ok(new { archived = 0 });
    }

    var now = DateTime.UtcNow;
    foreach (var recording in recordings)
    {
        recording.IsArchived = true;
        recording.ArchivedUtc = now;
    }

    await db.SaveChangesAsync(ct);
    foreach (var recording in recordings)
    {
        await eventHub.PublishAsync(new RecordingEvent(recording.Id, recording.FeedId, RecordingEventType.Archived));
    }
    return Results.Ok(new { archived = recordings.Count });
});

app.MapGet("/api/stream", async (string url, BroadcastifyResolver resolver, IHttpClientFactory httpClientFactory, HttpContext context, CancellationToken ct) =>
{
    if (!resolver.TryResolve(url, out _, out var streamUrl))
    {
        return Results.BadRequest(new { message = "Invalid Broadcastify feed URL or ID." });
    }

    var client = httpClientFactory.CreateClient("stream");
    using var response = await client.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, ct);
    response.EnsureSuccessStatusCode();

    context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
    await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
    await responseStream.CopyToAsync(context.Response.Body, ct);
    return Results.Empty;
});

app.MapGet("/api/ui-config", (IOptions<TranscriptionOptions> options) =>
{
    var opt = options.Value;
    return Results.Ok(new
    {
        expectedRealtimeFactor = opt.ExpectedRealtimeFactor,
        estimatedBytesPerSecond = opt.EstimatedBytesPerSecond
    });
});

app.MapGet("/api/recordings/stream", async (Guid feedId, IRecordingEventHub eventHub, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    await foreach (var evt in eventHub.Subscribe(ct))
    {
        if (evt.FeedId != feedId)
        {
            continue;
        }

        var payload = JsonSerializer.Serialize(evt, jsonOptions);
        var eventName = evt.Type.ToString().ToLowerInvariant();
        await context.Response.WriteAsync($"event: {eventName}\n", ct);
        await context.Response.WriteAsync($"data: {payload}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
});

app.MapFallbackToFile("index.html");

app.Run();
